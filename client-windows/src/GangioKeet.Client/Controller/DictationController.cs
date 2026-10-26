using System.Net.Http;
using GangioKeet.Client.Audio;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Context;
using GangioKeet.Client.Paste;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recovery;

namespace GangioKeet.Client.Controller;

/// <summary>
/// Orchestratore della dettatura: gestisce l'intero ciclo di vita da hotkey a paste.
/// Usa un generation ID per invalidare tentativi precedenti: Esc o errori irreversibili
/// incrementano la generazione e qualsiasi completamento di una generazione precedente
/// viene ignorato. Operazioni asincrone lunghe (rete, cattura) sono avviate e possedute
/// dal controller, mai awaited nel loop di eventi.
/// </summary>
public sealed class DictationController : IAsyncDisposable
{
    private readonly IAudioCapture _capture;
    private readonly Func<IDictationSession> _sessionFactory;
    private readonly PasteService _paste;
    private readonly RecoveryStore _recovery;
    private readonly IForegroundWindowService _foreground;
    private readonly IElevationChecker _elevation;
    private readonly IPillView _pill;
    private readonly ISoundPlayer _sound;
    private readonly ClientConfig _config;
    private readonly TimeProvider _time;
    private readonly RamAudioBuffer _audioBuffer;

    private readonly object _audioGate = new();
    private readonly List<byte[]> _pendingAudio = [];
    private string? _inputDeviceOverride;
    private bool _inputDeviceOverrideSet;

    private int _generation;
    private int _disposed;

    private volatile IDictationSession? _session;
    private bool _captureStarted;
    private bool _connected;
    private ForegroundWindowInfo? _target;
    private DictationProfile _profile;
    private string _wireProfile = "default";
    private ClientState _state = ClientState.Idle;

    public DictationController(
        IAudioCapture capture,
        Func<IDictationSession> sessionFactory,
        PasteService paste,
        RecoveryStore recovery,
        IForegroundWindowService foreground,
        IElevationChecker elevation,
        IPillView pill,
        ISoundPlayer sound,
        ClientConfig config,
        TimeProvider time)
    {
        _capture = capture;
        _sessionFactory = sessionFactory;
        _paste = paste;
        _recovery = recovery;
        _foreground = foreground;
        _elevation = elevation;
        _pill = pill;
        _sound = sound;
        _config = config;
        _time = time;
        _audioBuffer = new RamAudioBuffer();

        _capture.Ready += OnCaptureReady;
        _capture.ChunkReady += OnChunkReady;
        _capture.LevelChanged += OnLevelChanged;
        _capture.Failed += OnCaptureFailed;
    }

    public ClientState State => _state;
    public DictationProfile CurrentProfile => _profile;

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
        IDictationSession? session = _session;
        if (session is null) return;
        if (_state is not (ClientState.Recording or ClientState.Connecting)) return;

        try
        {
            await StopCaptureAsync(ct).ConfigureAwait(false);
            await session.FinishAsync(ct).ConfigureAwait(false);
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
        int generation = Interlocked.Increment(ref _generation);
        _ = generation;

        _audioBuffer.Clear();
        ClearPendingAudio();

        IDictationSession? session = _session;
        try
        {
            await StopCaptureAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        if (session is not null)
        {
            try
            {
                await session.AbortAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            await DisposeSessionAsync(session).ConfigureAwait(false);
        }

        _pill.Hide();
        _state = ClientState.Idle;
    }

    /// <summary>
    /// Ritenta l'ultimo fallimento. Se è già disponibile un risultato, ristrascrive solo
    /// l'audio via REST; un risultato già trascritto viene copiato negli appunti senza
    /// essere incollato automaticamente.
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
            using HttpClient http = new();
            RestDictationClient rest = new(http, _time);
            Uri baseUri = new($"http://{_config.Server.Host}:{_config.Server.Port}");

            DictationOutcome outcome = await rest.TranscribeAsync(
                baseUri,
                item.Pcm,
                item.Start.Profile,
                ApiCredential.Resolve(_config.Server.ApiKeyEnv, Environment.GetEnvironmentVariable),
                TimeSpan.FromSeconds(_config.Server.UploadTimeoutSeconds),
                TimeSpan.FromSeconds(_config.Server.RestResponseTimeoutSeconds),
                ct).ConfigureAwait(false);

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
            _state = ClientState.Idle;
        }
    }

    /// <summary>Copia l'ultimo testo disponibile negli appunti, senza incollarlo.</summary>
    public async Task CopyLastTextAsync(CancellationToken ct)
    {
        RecoveryItem? item = _recovery.GetCurrent();
        if (item?.Result is null) return;

        await CopyTextAsync(item.Result.FinalText, ct).ConfigureAwait(false);
        _recovery.RemoveIfCurrent(item.Id);
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

        IDictationSession? session = _session;
        if (session is not null)
        {
            await session.AbortAsync(CancellationToken.None).ConfigureAwait(false);
            await DisposeSessionAsync(session).ConfigureAwait(false);
        }

        _captureStarted = false;
        await _capture.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CopyTextAsync(string text, CancellationToken ct)
    {
        await _paste.CopyTextAsync(text, ct).ConfigureAwait(false);
    }

    private async Task RunDictationAsync(int generation, ForegroundWindowInfo target)
    {
        _pill.Show(PillState.Arming, "Connessione…", _profile);
        _audioBuffer.Clear();
        ClearPendingAudio();

        IDictationSession? session = null;
        try
        {
            session = _sessionFactory();
            _session = session;

            Uri uri = new($"ws://{_config.Server.Host}:{_config.Server.Port}/ws/dictate");
            string? token = ApiCredential.Resolve(_config.Server.ApiKeyEnv, Environment.GetEnvironmentVariable);

            StartMessage start = new(
                AppName: "GangioKeet.Client",
                WindowTitle: target.WindowTitle,
                Profile: _wireProfile);

            Task<SessionConnectOutcome> connectTask = session.ConnectAndStartAsync(
                uri, start, token,
                TimeSpan.FromSeconds(_config.Server.ConnectTimeoutSeconds),
                CancellationToken.None);

            Task captureTask = StartCaptureAsync();

            SessionConnectOutcome connect = await connectTask.ConfigureAwait(false);
            await captureTask.ConfigureAwait(false);

            if (IsStale(generation)) return;

            if (!connect.Connected)
            {
                await HandleFailureAsync(
                    generation,
                    connect.Failure?.Code ?? "connect_failed",
                    "Server non raggiungibile",
                    offerRecovery: true).ConfigureAwait(false);
                return;
            }

            // Backlog pre-start: i chunk catturati durante l'handshake entrano nella sessione
            // prima dei chunk live, nello stesso ordine di arrivo.
            FlushPendingAudioToSession();

            _state = ClientState.Recording;
            _pill.Show(PillState.Recording, "Registrazione…", _profile);

            if (IsStale(generation))
            {
                _pill.Hide();
                _state = ClientState.Idle;
                return;
            }

            DictationOutcome outcome = await session.Completion.ConfigureAwait(false);

            if (IsStale(generation)) return;

            await HandleOutcomeAsync(outcome, generation).ConfigureAwait(false);
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
            if (session is not null)
            {
                await DisposeSessionAsync(session).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleOutcomeAsync(DictationOutcome outcome, int generation)
    {
        if (IsStale(generation)) return;

        await StopCaptureAsync(CancellationToken.None).ConfigureAwait(false);

        if (outcome.Status == DictationStatus.Aborted)
        {
            _pill.Hide();
            _state = ClientState.Idle;
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
            _state = ClientState.Idle;
            _pill.Show(PillState.EmptyResult, "Testo vuoto", _profile);
            _ = HidePillAfterAsync(TimeSpan.FromMilliseconds(800), generation);
            return;
        }

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
            OfferResultForRecovery(result, target, "uipi_blocked");
            _pill.Show(PillState.Error, "Testo non incollato: recuperabile dalla tray", _profile);
            _state = ClientState.Idle;
            return;
        }

        if (IsStale(generation)) return;

        _state = ClientState.Pasting;
        PasteOutcome pasteResult = await _paste
            .PasteAsync(result.FinalText, target, CancellationToken.None)
            .ConfigureAwait(false);

        if (IsStale(generation)) return;

        if (pasteResult.Status == PasteStatus.Pasted)
        {
            _state = ClientState.Idle;
            _pill.Hide();
            _sound.Play(SoundKind.Stop);
            _audioBuffer.Clear();
            return;
        }

        OfferResultForRecovery(result, target, pasteResult.Status switch
        {
            PasteStatus.FocusLost => "focus_lost",
            PasteStatus.BlockedUipi => "uipi_blocked",
            PasteStatus.ClipboardUnavailable => "clipboard_unavailable",
            PasteStatus.InjectionFailed => "injection_failed",
            _ => "paste_failed",
        });

        _pill.Show(PillState.Error, "Paste non riuscito: testo recuperabile", _profile);
        _state = ClientState.Idle;
    }

    private async Task HandleFailureAsync(
        int generation,
        string code,
        string message,
        bool offerRecovery)
    {
        if (IsStale(generation)) return;

        await StopCaptureAsync(CancellationToken.None).ConfigureAwait(false);

        IDictationSession? session = _session;
        if (session is not null)
        {
            try
            {
                await session.AbortAsync(CancellationToken.None).ConfigureAwait(false);
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
        _state = ClientState.Idle;
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
        _state = ClientState.Idle;
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

    private async Task DisposeSessionAsync(IDictationSession session)
    {
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _session, null, session), session))
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
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    private void FlushPendingAudioToSession()
    {
        IDictationSession? session = _session;
        if (session is null) return;

        lock (_audioGate)
        {
            _connected = true;
            foreach (byte[] chunk in _pendingAudio)
            {
                if (!session.TryEnqueueAudio(chunk))
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

        IDictationSession? session = _session;
        bool enqueueNow;
        lock (_audioGate)
        {
            if (!_connected)
            {
                _pendingAudio.Add(owned);
                return;
            }

            enqueueNow = session is not null;
        }

        if (enqueueNow && session is not null && !session.TryEnqueueAudio(owned))
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
