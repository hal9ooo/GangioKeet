using System.Diagnostics;
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
    private readonly Func<DictationSession> _sessionFactory;
    private readonly PasteService _paste;
    private readonly RecoveryStore _recovery;
    private readonly IForegroundWindowService _foreground;
    private readonly IElevationChecker _elevation;
    private readonly IPillView _pill;
    private readonly ISoundPlayer _sound;
    private readonly ClientConfig _config;
    private readonly TimeProvider _time;
    private readonly RamAudioBuffer _audioBuffer;

    private int _generation;
    private int _disposed;

    private DictationSession? _session;
    private ForegroundWindowInfo? _target;
    private DictationProfile _profile;
    private string _wireProfile = "default";
    private ClientState _state = ClientState.Idle;

    public DictationController(
        IAudioCapture capture,
        Func<DictationSession> sessionFactory,
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

    public void Start()
    {
        if (_state != ClientState.Idle) return;

        nint handle = _foreground.CaptureHandle();
        if (handle == 0) return;

        ForegroundWindowInfo target = _foreground.Read(handle);
        if (!_foreground.IsAlive(target)) return;

        if (_elevation.IsInjectionBlocked(target))
        {
            _state = ClientState.Error;
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

    public async Task RetryLastAsync(CancellationToken ct)
    {
        RecoveryItem? item = _recovery.GetCurrent();
        if (item is null) return;

        _state = ClientState.Processing;
        _pill.Show(PillState.Processing, "Ritentativo…", _target is not null
            ? ProfileResolver.Resolve(_target.ProcessName, _target.WindowTitle, _config)
            : DictationProfile.Default);

        try
        {
            using HttpClient http = new();
            RestDictationClient rest = new(http, _time);
            Uri baseUri = new($"http://{_config.Server.Host}:{_config.Server.Port}");

            DictationOutcome outcome = await rest.TranscribeAsync(
                baseUri,
                item.Pcm ?? [],
                _wireProfile,
                ApiCredential.Resolve(_config.Server.ApiKeyEnv, Environment.GetEnvironmentVariable),
                TimeSpan.FromSeconds(_config.Server.UploadTimeoutSeconds),
                TimeSpan.FromSeconds(_config.Server.RestResponseTimeoutSeconds),
                ct).ConfigureAwait(false);

            await HandleOutcomeAsync(outcome, ct).ConfigureAwait(false);

            if (outcome.Status == DictationStatus.Result)
            {
                _recovery.RemoveIfCurrent(item.Id);
            }
        }
        catch (OperationCanceledException)
        {
            _state = ClientState.Idle;
            _pill.Hide();
        }
        catch (Exception ex)
        {
            _state = ClientState.Error;
            _pill.Show(PillState.Error, $"Retry fallito: {ex.Message}", _profile);
        }
    }

    public async Task CopyLastTextAsync(CancellationToken ct)
    {
        RecoveryItem? item = _recovery.GetCurrent();
        if (item?.Result is null) return;

        ForegroundWindowInfo? target = _target;
        if (target is null)
        {
            nint handle = _foreground.CaptureHandle();
            if (handle != 0) target = _foreground.Read(handle);
        }

        if (target is not null && !_elevation.IsInjectionBlocked(target))
        {
            PasteOutcome outcome = await _paste.PasteAsync(item.Result.FinalText, target, ct).ConfigureAwait(false);
            if (outcome.Status == PasteStatus.Pasted)
            {
                _recovery.RemoveIfCurrent(item.Id);
            }
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

        if (_session is not null)
        {
            await _session.AbortAsync(CancellationToken.None).ConfigureAwait(false);
            await _session.DisposeAsync().ConfigureAwait(false);
        }

        await _capture.DisposeAsync().ConfigureAwait(false);
    }

    private async Task RunDictationAsync(int generation, ForegroundWindowInfo target)
    {
        _pill.Show(PillState.Arming, "Connessione…", _profile);
        _audioBuffer.Clear();

        DictationSession? session = null;
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

            Task connectTask = session.ConnectAndStartAsync(
                uri, start, token,
                TimeSpan.FromSeconds(_config.Server.ConnectTimeoutSeconds),
                CancellationToken.None);

            Task captureTask = _capture.StartAsync(_config.Audio.InputDevice, CancellationToken.None);

            await Task.WhenAll(connectTask, captureTask).ConfigureAwait(false);

            if (IsStale(generation)) return;

            _state = ClientState.Recording;
            _pill.Show(PillState.Recording, "Registrazione…", _profile);

            DictationOutcome outcome = await session.Completion.ConfigureAwait(false);

            if (IsStale(generation)) return;

            await HandleOutcomeAsync(outcome, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!IsStale(generation))
            {
                _state = ClientState.Error;
                _pill.Show(PillState.Error, $"Errore: {ex.Message}", _profile);

                byte[] pcm = _audioBuffer.Snapshot();
                if (pcm.Length > 0 && _target is not null)
                {
                    _recovery.Offer(new RecoveryItem(
                        Guid.NewGuid(), pcm, null,
                        new StartMessage("GangioKeet.Client", _target.WindowTitle, _wireProfile),
                        _target,
                        "error",
                        _time.GetUtcNow().DateTime.AddSeconds(_config.Recovery.RetentionSeconds)));
                }
            }
        }
        finally
        {
            if (session is not null && session != _session)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task HandleOutcomeAsync(DictationOutcome outcome, CancellationToken ct)
    {
        if (outcome.Status == DictationStatus.Aborted)
        {
            _state = ClientState.Idle;
            _pill.Hide();
            return;
        }

        if (outcome.Status == DictationStatus.Error)
        {
            _state = ClientState.Error;
            _pill.Show(PillState.Error, outcome.Error?.Message ?? "Errore sconosciuto", _profile);

            if (_target is not null)
            {
                byte[] pcm = _audioBuffer.Snapshot();
                _recovery.Offer(new RecoveryItem(
                    Guid.NewGuid(), pcm, null,
                    new StartMessage("GangioKeet.Client", _target.WindowTitle, _wireProfile),
                    _target,
                    outcome.Error?.Code ?? "error",
                    _time.GetUtcNow().DateTime.AddSeconds(_config.Recovery.RetentionSeconds)));
            }

            return;
        }

        ResultMessage result = outcome.Result!;

        if (string.IsNullOrWhiteSpace(result.FinalText))
        {
            _state = ClientState.Idle;
            _pill.Show(PillState.EmptyResult, "Testo vuoto", _profile);
            await Task.Delay(TimeSpan.FromMilliseconds(800), _time, CancellationToken.None).ConfigureAwait(false);
            _pill.Hide();
            _state = ClientState.Idle;
            return;
        }

        if (result.Warnings.Count > 0)
        {
            _state = ClientState.Processing;
            _pill.Show(PillState.Warning, "Testo non rifinito", _profile);
        }
        else
        {
            _state = ClientState.Processing;
            _pill.Show(PillState.Processing, "Incollaggio…", _profile);
        }

        ForegroundWindowInfo? target = _target;
        if (target is null)
        {
            nint handle = _foreground.CaptureHandle();
            if (handle != 0) target = _foreground.Read(handle);
        }

        if (target is not null && !_elevation.IsInjectionBlocked(target))
        {
            _state = ClientState.Pasting;
            PasteOutcome pasteResult = await _paste.PasteAsync(result.FinalText, target, ct).ConfigureAwait(false);

            if (pasteResult.Status == PasteStatus.Pasted)
            {
                _state = ClientState.Idle;
                _pill.Hide();
                _sound.Play(SoundKind.Stop);
                _audioBuffer.Clear();
                return;
            }

            _recovery.Offer(new RecoveryItem(
                Guid.NewGuid(), null, result,
                new StartMessage("GangioKeet.Client", target.WindowTitle, _wireProfile),
                target,
                pasteResult.Status switch
                {
                    PasteStatus.FocusLost => "focus_lost",
                    PasteStatus.BlockedUipi => "uipi_blocked",
                    PasteStatus.ClipboardUnavailable => "clipboard_unavailable",
                    PasteStatus.InjectionFailed => "injection_failed",
                    _ => "paste_failed",
                },
                _time.GetUtcNow().DateTime.AddSeconds(_config.Recovery.RetentionSeconds)));
        }

        _state = ClientState.Idle;
        _pill.Hide();
    }

    private bool IsStale(int generation) => generation != Volatile.Read(ref _generation);

    private void OnCaptureReady(object? sender, EventArgs e) { }

    private void OnChunkReady(object? sender, ReadOnlyMemory<byte> chunk)
    {
        if (_state != ClientState.Recording) return;

        byte[] owned = chunk.ToArray();
        _audioBuffer.TryAppend(owned);

        if (_session is not null && !_session.TryEnqueueAudio(owned))
        {
            Interlocked.Increment(ref _generation);
            _state = ClientState.Error;
            _pill.Show(PillState.Error, "Buffer saturo", _profile);

            if (_target is not null)
            {
                byte[] pcm = _audioBuffer.Snapshot();
                _recovery.Offer(new RecoveryItem(
                    Guid.NewGuid(), pcm, null,
                    new StartMessage("GangioKeet.Client", _target.WindowTitle, _wireProfile),
                    _target,
                    "backpressure",
                    _time.GetUtcNow().DateTime.AddSeconds(_config.Recovery.RetentionSeconds)));
            }
        }
    }

    private void OnLevelChanged(object? sender, double level)
    {
        _pill.SetLevel(level);
    }

    private void OnCaptureFailed(object? sender, CaptureFailure failure)
    {
        Interlocked.Increment(ref _generation);

        _state = ClientState.Error;
        _pill.Show(PillState.Error, failure.Message, _profile);

        if (_target is not null)
        {
            byte[] pcm = _audioBuffer.Snapshot();
            _recovery.Offer(new RecoveryItem(
                Guid.NewGuid(), pcm, null,
                new StartMessage("GangioKeet.Client", _target.WindowTitle, _wireProfile),
                _target,
                failure.Code,
                _time.GetUtcNow().DateTime.AddSeconds(_config.Recovery.RetentionSeconds)));
        }
    }
}
