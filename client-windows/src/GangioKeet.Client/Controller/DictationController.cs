using GangioKeet.Client.Audio;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Context;
using GangioKeet.Client.Paste;
using GangioKeet.Client.Platform;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recovery;

namespace GangioKeet.Client.Controller;

/// <summary>
/// Orchestratore della dettatura: gestisce l'intero ciclo di vita da hotkey a paste.
/// Usa un generation ID per invalidare tentativi precedenti: Esc o errori irreversibili
/// incrementano la generazione e qualsiasi completamento di una generazione precedente
/// viene ignorato. Operazioni asincrone lunghe (cattura e inferenza locale) sono
/// avviate e possedute dal controller, mai awaited nel loop di eventi.
/// </summary>
public sealed class DictationController : IAsyncDisposable
{
    private readonly IAudioCapture _capture;
    private IDictationBackend _backend;
    private IDictationBackend? _pendingBackend;
    private readonly PasteService _paste;
    private readonly RecoveryStore _recovery;
    private readonly IForegroundWindowService _foreground;
    private readonly IElevationChecker _elevation;
    private readonly IPillView _pill;
    private readonly ISoundPlayer _sound;
    private readonly ClientConfig _config;
    private readonly TelemetryStore _telemetry;
    private readonly IMediaPlaybackPause _media;
    private readonly TimeProvider _time;
    private readonly RamAudioBuffer _audioBuffer;
    private static readonly TimeSpan MediaPauseBudget = TimeSpan.FromMilliseconds(750);

    private readonly object _audioGate = new();
    private readonly List<byte[]> _pendingAudio = [];
    private string? _inputDeviceOverride;
    private bool _inputDeviceOverrideSet;

    private int _generation;
    private int _disposed;
    private volatile bool _stopRequested;

    private volatile IActiveDictationTurn? _turn;
    private bool _captureStarted;
    private bool _connected;
    private ForegroundWindowInfo? _target;
    private ResultMessage? _lastResult;
    private DictationProfile _profile;
    private string _wireProfile = "default";
    private ClientState _state = ClientState.Idle;
    private long _turnStarted;
    private TurnTiming? _turnTiming;
    private CancellationTokenSource? _pasteAbort;

    public DictationController(
        IAudioCapture capture,
        IDictationBackend backend,
        PasteService paste,
        RecoveryStore recovery,
        IForegroundWindowService foreground,
        IElevationChecker elevation,
        IPillView pill,
        ISoundPlayer sound,
        ClientConfig config,
        TelemetryStore telemetry,
        IMediaPlaybackPause media,
        TimeProvider time)
    {
        _capture = capture;
        _backend = backend;
        _paste = paste;
        _recovery = recovery;
        _foreground = foreground;
        _elevation = elevation;
        _pill = pill;
        _sound = sound;
        _config = config;
        _telemetry = telemetry;
        _media = media;
        _time = time;
        _audioBuffer = new RamAudioBuffer();

        _capture.Ready += OnCaptureReady;
        _capture.ChunkReady += OnChunkReady;
        _capture.LevelChanged += OnLevelChanged;
        _capture.Failed += OnCaptureFailed;
    }

    public ClientState State => _state;
    public DictationProfile CurrentProfile => _profile;
    public bool HasLastText => Volatile.Read(ref _lastResult) is not null;

    public IDictationBackend CurrentBackend
    {
        get
        {
            lock (_audioGate)
            {
                return _pendingBackend ?? _backend;
            }
        }
    }

    public void SetBackend(IDictationBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        lock (_audioGate)
        {
            if (_state == ClientState.Idle)
            {
                _backend = backend;
                _pendingBackend = null;
            }
            else
            {
                _pendingBackend = backend;
            }
        }
    }

    private void TransitionToIdle()
    {
        lock (_audioGate)
        {
            _state = ClientState.Idle;
            if (_pendingBackend is not null)
            {
                _backend = _pendingBackend;
                _pendingBackend = null;
            }
        }
    }

    /// <summary>
    /// Imposta il microfono per le dettature successive. <paramref name="deviceId"/> null
    /// torna al valore della configurazione. La registrazione eventualmente in corso non
    /// viene toccata.
    /// </summary>
    public void SetInputDevice(string? deviceId)
    {
        lock (_audioGate)
        {
            _inputDeviceOverride = deviceId;
            _inputDeviceOverrideSet = true;
        }
    }

    public void Start()
    {
        if (_state != ClientState.Idle) return;
        _stopRequested = false;

        if (!_backend.IsReady)
        {
            int generation = Interlocked.Increment(ref _generation);
            _pill.Show(PillState.Error, "Modelli locali non pronti", null);
            _ = HidePillAfterAsync(TimeSpan.FromMilliseconds(2500), generation);
            return;
        }

        nint handle = _foreground.CaptureHandle();
        if (handle == 0) return;

        ForegroundWindowInfo target = _foreground.Read(handle);
        if (!_foreground.IsAlive(target)) return;

        _pill.Hide();

        if (_elevation.IsInjectionBlocked(target))
        {
            _pill.Show(PillState.Error, "Finestra elevata: impossibile incollare", null);
            return;
        }

        _target = target;
        _profile = ProfileResolver.Resolve(target.ProcessName, target.WindowTitle, _config);
        _wireProfile = ProfileResolver.ToWire(_profile);
        _state = ClientState.Connecting;

        int currentGen = Interlocked.Increment(ref _generation);
        _ = RunDictationAsync(currentGen, target);
    }

    /// <summary>
    /// Ferma la registrazione: drena la cattura, invia stop e attende il risultato.
    /// Il completamento viene gestito dal flusso della dettatura in corso.
    /// </summary>
    public async Task StopAsync(CancellationToken ct)
    {
        if (_state is not (ClientState.Recording or ClientState.Connecting)) return;

        _stopRequested = true;
        _turnTiming?.MarkReleased(_time.GetTimestamp());

        IActiveDictationTurn? turn = _turn;
        if (turn is null) return;

        try
        {
            await StopCaptureAsync(ct).ConfigureAwait(false);
            await turn.FinishAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stop annullato: il flusso principale resta responsabile del cleanup
        }
    }

    /// <summary>
    /// Annulla la dettatura (Esc): invalida la generazione, interrompe cattura e socket,
    /// svuota il buffer e non offre recupero. Un RecoveryItem precedente non viene toccato.
    /// Lo stato torna Idle solo a cleanup concluso, così un nuovo Start non può sovrapporsi.
    /// </summary>
    public async Task AbortAsync(CancellationToken ct)
    {
        _stopRequested = true;
        int generation = Interlocked.Increment(ref _generation);
        _ = generation;
        try { _pasteAbort?.Cancel(); } catch (ObjectDisposedException) { }
        _turnTiming?.MarkPasteFailed("aborted");

        _audioBuffer.Clear();
        ClearPendingAudio();

        IActiveDictationTurn? turn = _turn;
        try
        {
            await StopCaptureAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (turn is not null)
        {
            try
            {
                await turn.AbortAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await DisposeTurnAsync(turn).ConfigureAwait(false);
        }

        _pill.Hide();
        TransitionToIdle();
    }

    /// <summary>
    /// Ritenta l'ultimo fallimento: se è già disponibile un risultato, copia il testo
    /// negli appunti senza incollarlo; altrimenti reinvia l'audio al backend locale.
    /// </summary>
    public async Task RetryLastAsync(CancellationToken ct)
    {
        RecoveryItem? item = _recovery.GetCurrent();
        if (item is null || _state != ClientState.Idle) return;

        if (item.Result is not null)
        {
            // Niente nuova ASR: il testo esiste già, va solo copiato su richiesta esplicita.
            await CopyTextAsync(item.Result.FinalText, ct).ConfigureAwait(false);
            _recovery.RemoveIfCurrent(item.Id);
            return;
        }

        if (item.Pcm is null || item.Pcm.Length == 0) return;

        _state = ClientState.Processing;
        _pill.Show(PillState.Processing, "Ritentativo…", _profile);

        try
        {
            _telemetry.RecordRetry();
            DictationOutcome outcome = await _backend
                .RetryAsync(item.Pcm, item.Start, ct)
                .ConfigureAwait(false);

            if (outcome.Status == DictationStatus.Result
                && !string.IsNullOrWhiteSpace(outcome.Result!.FinalText))
            {
                await CopyTextAsync(outcome.Result.FinalText, ct).ConfigureAwait(false);
                _recovery.RemoveIfCurrent(item.Id);
                _pill.Show(PillState.Warning, "Testo copiato", _profile);
            }
            else
            {
                _pill.Show(PillState.Error, outcome.Error?.Message ?? "Retry senza testo", _profile);
            }
        }
        catch (OperationCanceledException)
        {
            _pill.Hide();
        }
        catch (Exception ex)
        {
            _pill.Show(PillState.Error, $"Retry fallito: {ex.Message}", _profile);
        }
        finally
        {
            TransitionToIdle();
        }
    }

    /// <summary>Copia l'ultimo testo disponibile negli appunti, senza incollarlo.</summary>
    public async Task CopyLastTextAsync(CancellationToken ct)
    {
        RecoveryItem? item = _recovery.GetCurrent();
        if (item?.Result is not null)
        {
            await CopyTextAsync(item.Result.FinalText, ct).ConfigureAwait(false);
            _recovery.RemoveIfCurrent(item.Id);
            return;
        }

        ResultMessage? last = Volatile.Read(ref _lastResult);
        if (last is not null)
        {
            await CopyTextAsync(last.FinalText, ct).ConfigureAwait(false);
        }
    }

    public void DiscardLast()
    {
        _recovery.Discard();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _capture.Ready -= OnCaptureReady;
        _capture.ChunkReady -= OnChunkReady;
        _capture.LevelChanged -= OnLevelChanged;
        _capture.Failed -= OnCaptureFailed;

        Interlocked.Increment(ref _generation);

        IActiveDictationTurn? turn = _turn;
        if (turn is not null)
        {
            await turn.AbortAsync(CancellationToken.None).ConfigureAwait(false);
            await DisposeTurnAsync(turn).ConfigureAwait(false);
        }

        _captureStarted = false;
        try
        {
            await _media.ResumeAsync().WaitAsync(MediaPauseBudget).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // la ripresa media è best-effort anche in chiusura
        }

        await _capture.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CopyTextAsync(string text, CancellationToken ct)
    {
        await _paste.CopyTextAsync(text, ct).ConfigureAwait(false);
    }

    private async Task RunDictationAsync(int generation, ForegroundWindowInfo target)
    {
        _pill.Show(PillState.Arming, "Avvio…", _profile);
        _audioBuffer.Clear();
        ClearPendingAudio();

        // Il turno è fissato qui, una volta per dettatura: un cambio stack
        // vale dal turno successivo.
        _turnStarted = _time.GetTimestamp();
        _turnTiming = new TurnTiming(_time);
        using CancellationTokenSource pasteAbort = new();
        _pasteAbort = pasteAbort;

        IActiveDictationTurn? turn = null;
        try
        {
            StartMessage start = new(
                AppName: "GangioKeet.Client",
                WindowTitle: target.WindowTitle,
                Profile: _wireProfile);

            turn = _backend.CreateTurn(start);
            _turn = turn;

            long connectStarted = _time.GetTimestamp();
            Task<TurnStartOutcome> startTask = turn.StartAsync(CancellationToken.None);

            if (_stopRequested)
            {
                _pill.Hide();
                TransitionToIdle();
                return;
            }

            await PauseMediaAsync().ConfigureAwait(false);
            if (_stopRequested)
            {
                ResumeMediaInBackground();
                _pill.Hide();
                TransitionToIdle();
                return;
            }

            Task captureTask = StartCaptureAsync();

            TurnStartOutcome connect = await startTask.ConfigureAwait(false);
            TimeSpan startElapsed = _time.GetElapsedTime(connectStarted);
            await captureTask.ConfigureAwait(false);

            if (IsStale(generation)) return;

            if (!connect.Ready)
            {
                await HandleFailureAsync(
                    generation,
                    connect.Error?.Code ?? "start_failed",
                    connect.Error?.Message ?? "Avvio dettatura non riuscito",
                    offerRecovery: true).ConfigureAwait(false);
                return;
            }

            // Backlog pre-start: i chunk catturati durante l'handshake entrano nella sessione
            // prima dei chunk live, nello stesso ordine di arrivo.
            FlushPendingAudioToTurn();

            _state = ClientState.Recording;
            _pill.Show(PillState.Recording, "Registrazione…", _profile);

            if (IsStale(generation))
            {
                _pill.Hide();
                TransitionToIdle();
                return;
            }

            DictationOutcome outcome = await turn.Completion.ConfigureAwait(false);

            if (IsStale(generation)) return;

            await HandleOutcomeAsync(outcome, generation, pasteAbort.Token).ConfigureAwait(false);

            if (outcome.Status == DictationStatus.Result
                && !string.IsNullOrWhiteSpace(outcome.Result?.FinalText)
                && !IsStale(generation))
            {
                _telemetry.RecordDictation(
                    success: true,
                    _time.GetElapsedTime(_turnStarted),
                    startElapsed,
                    turn.BackendName,
                    errorCode: null);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!IsStale(generation))
            {
                await HandleFailureAsync(generation, "error", ex.Message, offerRecovery: true)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (ReferenceEquals(_pasteAbort, pasteAbort)) _pasteAbort = null;
            if (_turnTiming is not null)
            {
                _telemetry.RecordTiming(_turnStarted, _turnTiming.ReleaseToPasteMs, _turnTiming.FailureReason);
                _turnTiming = null;
            }

            if (turn is not null)
            {
                await DisposeTurnAsync(turn).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleOutcomeAsync(DictationOutcome outcome, int generation, CancellationToken pasteToken)
    {
        if (IsStale(generation)) return;

        await StopCaptureAsync(CancellationToken.None).ConfigureAwait(false);

        if (outcome.Status == DictationStatus.Aborted)
        {
            _turnTiming?.MarkPasteFailed("aborted");
            _pill.Hide();
            TransitionToIdle();
            return;
        }

        if (outcome.Status == DictationStatus.Error)
        {
            await HandleFailureAsync(
                generation,
                outcome.Error?.Code ?? "error",
                outcome.Error?.Message ?? "Errore sconosciuto",
                offerRecovery: true).ConfigureAwait(false);
            return;
        }

        ResultMessage result = outcome.Result!;

        if (string.IsNullOrWhiteSpace(result.FinalText))
        {
            _turnTiming?.MarkPasteFailed("empty_result");
            TransitionToIdle();
            _pill.Show(PillState.EmptyResult, "Testo vuoto", _profile);
            _ = HidePillAfterAsync(TimeSpan.FromMilliseconds(800), generation);
            return;
        }

        Volatile.Write(ref _lastResult, result);

        if (result.Warnings.Count > 0)
        {
            _pill.Show(PillState.Warning, "Testo non rifinito", _profile);
        }
        else
        {
            _pill.Show(PillState.Processing, "Incollaggio…", _profile);
        }

        _state = ClientState.Processing;

        ForegroundWindowInfo? target = _target;
        if (target is null || !_foreground.IsAlive(target) || _elevation.IsInjectionBlocked(target))
        {
            // Nessun paste possibile: il testo resta recuperabile, mai perso.
            _turnTiming?.MarkPasteFailed("target_unavailable");
            OfferResultForRecovery(result, target, "uipi_blocked");
            _pill.Show(PillState.Error, "Testo non incollato: recuperabile dalla tray", _profile);
            TransitionToIdle();
            return;
        }

        if (IsStale(generation)) return;

        _state = ClientState.Pasting;
        PasteOutcome pasteResult = await _paste
            .PasteAsync(result.FinalText, target, pasteToken)
            .ConfigureAwait(false);

        if (IsStale(generation)) return;
        if (pasteResult.Status == PasteStatus.Cancelled || pasteToken.IsCancellationRequested) return;

        if (pasteResult.Status == PasteStatus.Pasted)
        {
            _turnTiming?.MarkPasted(_time.GetTimestamp());
            TransitionToIdle();
            _pill.Hide();
            _sound.Play(SoundKind.Stop);
            _audioBuffer.Clear();
            return;
        }

        string pasteFailure = pasteResult.Status switch
        {
            PasteStatus.FocusLost => "focus_lost",
            PasteStatus.BlockedUipi => "uipi_blocked",
            PasteStatus.ClipboardUnavailable => "clipboard_unavailable",
            PasteStatus.InjectionFailed => "injection_failed",
            _ => "paste_failed",
        };
        _turnTiming?.MarkPasteFailed(pasteFailure);
        OfferResultForRecovery(result, target, pasteFailure);

        _pill.Show(PillState.Error, "Paste non riuscito: testo recuperabile", _profile);
        TransitionToIdle();
    }

    private async Task HandleFailureAsync(
        int generation,
        string code,
        string message,
        bool offerRecovery)
    {
        if (IsStale(generation)) return;
        _turnTiming?.MarkPasteFailed(code);

        await StopCaptureAsync(CancellationToken.None).ConfigureAwait(false);

        IActiveDictationTurn? turn = _turn;
        if (turn is not null)
        {
            try
            {
                await turn.AbortAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // best effort: la sessione viene comunque disposta dal flusso chiamante
            }
        }

        if (offerRecovery)
        {
            byte[] pcm = _audioBuffer.Snapshot();
            if (pcm.Length > 0 && _target is not null)
            {
                OfferRecovery(pcm, null, _target, code);
            }
        }

        _pill.Show(PillState.Error, message, _profile);
        TransitionToIdle();

        _telemetry.RecordDictation(
            success: false,
            _time.GetElapsedTime(_turnStarted),
            start: null,
            turn?.BackendName ?? "sconosciuto",
            errorCode: code);
    }

    private void OfferResultForRecovery(ResultMessage result, ForegroundWindowInfo? target, string code)
    {
        if (target is null) return;

        _recovery.Offer(new RecoveryItem(
            Guid.NewGuid(), null, result,
            new StartMessage("GangioKeet.Client", target.WindowTitle, _wireProfile),
            target,
            code,
            _time.GetUtcNow().DateTime.AddSeconds(_config.Recovery.RetentionSeconds)));
    }

    private void OfferRecovery(
        byte[] pcm,
        ResultMessage? result,
        ForegroundWindowInfo target,
        string code)
    {
        _recovery.Offer(new RecoveryItem(
            Guid.NewGuid(), pcm, result,
            new StartMessage("GangioKeet.Client", target.WindowTitle, _wireProfile),
            target,
            code,
            _time.GetUtcNow().DateTime.AddSeconds(_config.Recovery.RetentionSeconds)));
    }

    private async Task HidePillAfterAsync(TimeSpan delay, int generation)
    {
        try
        {
            await Task.Delay(delay, _time, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (IsStale(generation)) return;

        _pill.Hide();
        TransitionToIdle();
    }

    private bool IsStale(int generation) => generation != Volatile.Read(ref _generation);

    private async Task StartCaptureAsync()
    {
        if (_captureStarted) return;

        string? deviceId;
        lock (_audioGate)
        {
            deviceId = _inputDeviceOverrideSet ? _inputDeviceOverride : _config.Audio.InputDevice;
        }

        _captureStarted = true;
        await _capture.StartAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task StopCaptureAsync(CancellationToken ct)
    {
        ResumeMediaInBackground();
        if (!_captureStarted) return;

        _captureStarted = false;
        try
        {
            await _capture.StopAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // lo stop della cattura non deve mascherare l'esito della dettatura
        }
    }

    private async Task PauseMediaAsync()
    {
        try
        {
            await _media.PauseAsync(CancellationToken.None)
                .WaitAsync(MediaPauseBudget, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        catch (Exception)
        {
            // la pausa media è best-effort e non deve mai fermare la dettatura
        }
    }

    private void ResumeMediaInBackground()
    {
        _ = ResumeMediaAsync();
    }

    private async Task ResumeMediaAsync()
    {
        try
        {
            await _media.ResumeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // la ripresa media è best-effort
        }
    }

    private async Task DisposeTurnAsync(IActiveDictationTurn turn)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _turn, null, turn), turn))
        {
            // Un altro flusso lo ha già rimosso e disposto.
            return;
        }

        lock (_audioGate)
        {
            _connected = false;
        }

        try
        {
            await turn.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    private void FlushPendingAudioToTurn()
    {
        IActiveDictationTurn? turn = _turn;
        if (turn is null) return;

        lock (_audioGate)
        {
            _connected = true;
            foreach (byte[] chunk in _pendingAudio)
            {
                if (!turn.TryEnqueueAudio(chunk))
                {
                    _pendingAudio.Clear();
                    _ = HandleFailureAsync(
                        Volatile.Read(ref _generation),
                        "backpressure",
                        "Buffer saturo",
                        offerRecovery: true);
                    return;
                }
            }

            _pendingAudio.Clear();
        }
    }

    private void ClearPendingAudio()
    {
        lock (_audioGate)
        {
            _pendingAudio.Clear();
            _connected = false;
        }
    }

    private void OnCaptureReady(object? sender, EventArgs e) { }

    private void OnChunkReady(object? sender, ReadOnlyMemory<byte> chunk)
    {
        if (_state is not (ClientState.Connecting or ClientState.Recording)) return;

        byte[] owned = chunk.ToArray();
        _audioBuffer.TryAppend(owned);

        IActiveDictationTurn? turn = _turn;
        bool enqueueNow;
        lock (_audioGate)
        {
            if (!_connected)
            {
                _pendingAudio.Add(owned);
                return;
            }

            enqueueNow = turn is not null;
        }

        if (enqueueNow && turn is not null && !turn.TryEnqueueAudio(owned))
        {
            int generation = Interlocked.Increment(ref _generation);
            _ = HandleFailureAsync(generation, "backpressure", "Buffer saturo", offerRecovery: true);
        }
    }

    private void OnLevelChanged(object? sender, double level)
    {
        _pill.SetLevel(level);
    }

    private void OnCaptureFailed(object? sender, CaptureFailure failure)
    {
        int generation = Interlocked.Increment(ref _generation);
        _ = HandleFailureAsync(generation, failure.Code, failure.Message, offerRecovery: true);
    }
}
