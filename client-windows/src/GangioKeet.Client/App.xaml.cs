using System.IO;
using System.Net.Http;
using System.Windows;
using GangioKeet.Client.Audio;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Context;
using GangioKeet.Client.Controller;
using GangioKeet.Client.Hotkey;
using GangioKeet.Client.Paste;
using GangioKeet.Client.Platform;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recovery;
using GangioKeet.Client.Recording;
using GangioKeet.Client.Ui;
using Hardcodet.Wpf.TaskbarNotification;

namespace GangioKeet.Client;

/// <summary>
/// Composition root: crea e collega tutti i servizi all'avvio, gestisce il
/// ciclo di vita (tray-only startup, shutdown pulito) e il reload della config.
/// </summary>
public partial class App : Application
{
    private SingleInstanceGuard? _guard;
    private IDisposable? _instanceHandle;
    private FileLogger? _logger;

    private ClientConfig? _config;
    private DictationController? _controller;
    private TelemetryStore? _telemetry;
    private RecoveryStore? _recovery;
    private EndpointSelector? _selector;
    private EndpointProbe? _probe;
    private EndpointMonitor? _monitor;
    private SettingsWindow? _settings;
    private SplashWindow? _splash;
    private TrayController? _tray;
    private PillWindow? _pill;
    private WasapiAudioCapture? _capture;
    private LowLevelInputHooks? _hooks;
    private GesturePump? _gestures;
    private GestureRouter? _gestureRouter;
    private StaPasteDispatcher? _pasteDispatcher;
    private SynthSoundPlayer? _soundPlayer;
    private HttpClient? _monitorHttp;
    private CancellationTokenSource? _monitorCts;
    private AppRuntime? _runtime;
    private RecordingStore? _recordingStore;
    private RecordingQueue? _recordingQueue;
    private readonly IModelProvisioner _modelProvisioner;

    public App() : this(null) { }

    internal App(IModelProvisioner? modelProvisioner = null)
    {
        _modelProvisioner = modelProvisioner ?? new ModelProvisioner();
    }
    private string? _localSttId;
    private string? _localCleanerId;
    private string? _localBackend;
    private readonly object _shutdownGate = new();
    private Task? _shutdownTask;
    private int _shutdownRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        SessionEnding += OnSessionEnding;

        _guard = new SingleInstanceGuard("GangioKeet");
        _instanceHandle = _guard.TryAcquire();
        if (_instanceHandle is null)
        {
            MessageBox.Show(
                "GangioKeet è già in esecuzione.",
                "GangioKeet",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        _logger = new FileLogger();

        // Su un'installazione pulita la cartella di configurazione può non
        // esistere: la finestra impostazioni deve poter scrivere il primo file.
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigLoader.DefaultPath)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Write("config_directory_error", ex.Message);
        }

        // Lo splash è l'unica finestra all'avvio: senza OnExplicitShutdown la
        // chiusura della splash (3 s) spegnerebbe l'app tray-only.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Lo splash è decorativo: un errore nel XAML non deve impedire
        // l'avvio dell'app (resterebbe un processo morto senza log né UI).
        try
        {
            _splash = new SplashWindow(TimeSpan.FromSeconds(3));
            _splash.Show();
        }
        catch (Exception ex)
        {
            _logger.Write("splash_error", ex.Message);
            _splash = null;
        }

        try
        {
            _config = ConfigLoader.Load();
        }
        catch (ConfigException ex)
        {
            _splash?.Dismiss();
            _logger.Write("config_error", ex.Message);
            MessageBox.Show(
                $"Configurazione non valida:\n{ex.Message}",
                "GangioKeet",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
            Compose();
        }
        catch (Exception ex)
        {
            _splash?.Dismiss();
            _logger.Write("startup_error", ex.Message);
            MessageBox.Show(
                $"Impossibile avviare GangioKeet:\n{ex.Message}",
                "GangioKeet",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void Compose()
    {
        ClientConfig config = _config!;
        TimeProvider time = TimeProvider.System;

        _recovery = new RecoveryStore(time, TimeSpan.FromSeconds(config.Recovery.RetentionSeconds));

        _pasteDispatcher = new StaPasteDispatcher();
        PasteService paste = new(
            new Win32ClipboardService(),
            new SendInputSimulator(),
            new Win32ForegroundWindowService(),
            new Win32ElevationChecker(),
            _pasteDispatcher,
            config.Paste,
            time);

        _pill = new PillWindow();
        _pill.PositionMode = config.Ui.PillPosition;
        SynthSoundPlayer sound = new() { Enabled = config.Ui.SoundFeedback };
        _soundPlayer = sound;

        _capture = new WasapiAudioCapture(config.Audio);
        _telemetry = new TelemetryStore();

        IDictationBackend backend;
        if (config.Inference.Mode == "local")
        {
            LocalInferenceSetup setup = LocalInferenceFactory.Create(config);
            _localSttId = setup.SttId;
            _localCleanerId = setup.CleanerId;
            _localBackend = setup.Backend;
            _recordingStore = new RecordingStore(config.Recording,
                provenance: new RecordingProvenance(setup.PromptVersion, setup.PromptSha256, setup.PolicySha256));
            _recordingQueue = new RecordingQueue(_recordingStore, warning: warning =>
                _logger?.Write("recording", warning),
                enabled: () => _recordingStore?.Enabled == true);
            _runtime = new AppRuntime(setup.Pipeline, _recordingQueue);
            backend = new LocalDictationBackend(setup.Pipeline, () => _runtime.IsReady, _recordingQueue);
            _ = StartLocalRuntimeAsync(_runtime);
        }
        else
        {
            _recordingStore = new RecordingStore(config.Recording, provenance: null);
            _monitorHttp = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _probe = new EndpointProbe(
                _monitorHttp,
                ApiCredential.Resolve(config.Server.ApiKeyEnv, Environment.GetEnvironmentVariable),
                time);
            EndpointSelector selector = new(config.Endpoints!, config.Server.SwitchMarginPercent);
            _selector = selector;
            _monitor = new EndpointMonitor(
                _probe,
                selector,
                time,
                TimeSpan.FromSeconds(config.Server.HealthCheckIntervalSeconds),
                TimeSpan.FromSeconds(config.Server.HealthCheckTimeoutSeconds));
            backend = new RemoteDictationBackend(CreateSession, selector, config, time);
        }

        _controller = new DictationController(
            _capture,
            backend,
            paste,
            _recovery,
            new Win32ForegroundWindowService(),
            new Win32ElevationChecker(),
            _pill,
            sound,
            config,
            _telemetry,
            time);

        _hooks = new LowLevelInputHooks(
            new Win32InputHookBackend(),
            new HookSuppressionPolicy(config.Hotkey.Key, config.Hotkey.AbortKey));

        _gestures = new GesturePump(
            new GestureDetector(config.Hotkey, time),
            CreateGestureRouter().OnGesture,
            time);
        _hooks.RawEvent += (_, raw) => HandleRawEvent(raw, config);

        _tray = new TrayController(
            new TaskbarIcon
            {
                IconSource = CreateTrayIcon(),
            },
            _controller,
            _recovery,
            _selector,
            config,
            new AudioDevices(),
            ConfigLoader.DefaultPath,
            ShowSettings,
            _logger,
            exit: RequestShutdownAsync);

        if (_monitor is not null)
        {
            _monitorCts = new CancellationTokenSource();
            _ = _monitor.RunAsync(_monitorCts.Token);
        }

        var capsReset = new CapsLockResetService();
        if (capsReset.ResetIfActive())
        {
            _logger?.Write("capslock_cleared_on_startup", "Caps Lock was active and has been turned off");
        }

        _hooks.Start();
    }

    private async Task StartLocalRuntimeAsync(AppRuntime runtime)
    {
        try
        {
            _logger?.Write("local_runtime", "Ensuring local models are present and valid...");
            string selectionPath = _config?.Inference.ModelSelectionPath ?? "models/selected.json";
            await _modelProvisioner.EnsureModelsAsync(selectionPath, progress: new Progress<ModelProvisionProgress>(p =>
            {
                if (p.Status == ModelProvisionStatus.Downloading && p.TotalBytes.HasValue && p.TotalBytes > 0)
                {
                    double pct = (double)p.BytesTransferred / p.TotalBytes.Value;
                    _logger?.Write("model_download", $"{p.ModelId} ({p.RelativePath}): {pct:P0}");
                }
                else
                {
                    _logger?.Write("model_download", $"{p.ModelId} ({p.RelativePath}): {p.Status}");
                }
            })).ConfigureAwait(false);

            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(true);
            _logger?.Write("local_runtime", runtime.State.ToString());
        }
        catch (Exception error)
        {
            _logger?.Write("local_runtime_failed", error.Message);
        }
    }

    private void ShowSettings()
    {
        if (_settings is not null)
        {
            _settings.Activate();
            return;
        }

        if (_telemetry is null)
        {
            return;
        }

        SettingsWindow window = _selector is null || _probe is null
            ? new(null, null, _telemetry, ConfigLoader.DefaultPath, _ => { }, _logger,
                LocalInfo, OnModeSaved, _recordingStore)
            : new(_selector, _probe, _telemetry, ConfigLoader.DefaultPath, OnEndpointsSaved, _logger,
                LocalInfo, OnModeSaved, _recordingStore);

        window.Closed += (_, _) => _settings = null;
        _settings = window;
        window.Show();
        window.Activate();
    }

    private LocalSettingsInfo LocalInfo() => new(
        _config?.Inference.Mode ?? "local",
        _localBackend ?? _config?.Inference.Backend ?? "vulkan",
        _localSttId is null || _localCleanerId is null ? "—" : $"{_localSttId} + {_localCleanerId}",
        _runtime?.State.ToString() ?? "Stopped",
        _runtime?.Failure?.Message);

    private void OnModeSaved(string mode)
    {
        if (_config is null) return;
        _logger?.Write("inference_mode_saved", mode);
        _ = SwitchInferenceModeAsync(mode);
    }

    public async Task SwitchInferenceModeAsync(string targetMode)
    {
        if (_config is null || _controller is null) return;
        _config.Inference.Mode = targetMode;

        if (string.Equals(targetMode, "local", StringComparison.OrdinalIgnoreCase))
        {
            if (_monitorCts is not null)
            {
                _monitorCts.Cancel();
                _monitorCts.Dispose();
                _monitorCts = null;
            }
            _monitor = null;

            if (_runtime is null)
            {
                LocalInferenceSetup setup = LocalInferenceFactory.Create(_config);
                _localSttId = setup.SttId;
                _localCleanerId = setup.CleanerId;
                _localBackend = setup.Backend;
                _recordingStore = new RecordingStore(_config.Recording,
                    provenance: new RecordingProvenance(setup.PromptVersion, setup.PromptSha256, setup.PolicySha256));
                _settings?.SetRecordingStore(_recordingStore);
                _recordingQueue = new RecordingQueue(_recordingStore, warning: warning =>
                    _logger?.Write("recording", warning),
                    enabled: () => _recordingStore?.Enabled == true);
                _runtime = new AppRuntime(setup.Pipeline, _recordingQueue);
            }

            var localBackend = new LocalDictationBackend(_runtime.Pipeline, () => _runtime.IsReady, _recordingQueue);
            _controller.SetBackend(localBackend);
            _ = StartLocalRuntimeAsync(_runtime);
            _settings?.SetEndpointServices(null, null);
            _logger?.Write("switched_to_local", "Local runtime active and controller backend swapped");
        }
        else // remote
        {
            var oldRuntime = _runtime;
            _runtime = null;
            _recordingQueue = null;

            if (oldRuntime is not null)
            {
                _ = Task.Run(async () =>
                {
                    while (_controller.State != ClientState.Idle)
                    {
                        await Task.Delay(50).ConfigureAwait(false);
                    }
                    try
                    {
                        await oldRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.Write("local_runtime_stop_error", ex.Message);
                    }
                });
            }

            if (_monitorCts is not null)
            {
                _monitorCts.Cancel();
                _monitorCts.Dispose();
                _monitorCts = null;
            }
            _monitor = null;

            TimeProvider time = TimeProvider.System;
            _monitorHttp ??= new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _probe ??= new EndpointProbe(
                _monitorHttp,
                ApiCredential.Resolve(_config.Server.ApiKeyEnv, Environment.GetEnvironmentVariable),
                time);

            if (_config.Endpoints is null || _config.Endpoints.Count == 0)
            {
                ConfigLoader.EnsureEndpoints(_config);
            }
            if (_config.Endpoints is null || _config.Endpoints.Count == 0)
            {
                _config.Endpoints = [new EndpointConfig { Host = _config.Server.Host, Port = _config.Server.Port, Role = "primary", Name = "Mac Server" }];
            }

            if (_selector is null)
            {
                _selector = new EndpointSelector(_config.Endpoints, _config.Server.SwitchMarginPercent);
            }
            else
            {
                _selector.Reset(_config.Endpoints);
            }

            _monitor = new EndpointMonitor(
                _probe,
                _selector,
                time,
                TimeSpan.FromSeconds(_config.Server.HealthCheckIntervalSeconds),
                TimeSpan.FromSeconds(_config.Server.HealthCheckTimeoutSeconds));

            _monitorCts = new CancellationTokenSource();
            _ = _monitor.RunAsync(_monitorCts.Token);

            var remoteBackend = new RemoteDictationBackend(CreateSession, _selector, _config, time);
            _controller.SetBackend(remoteBackend);
            _settings?.SetEndpointServices(_selector, _probe, OnEndpointsSaved);
            _logger?.Write("switched_to_remote", "Remote monitor active and controller backend swapped");
        }
    }

    private void OnEndpointsSaved(IReadOnlyList<EndpointConfig> endpoints)
    {
        if (_config is null || _selector is null)
        {
            return;
        }

        _config.Endpoints = [.. endpoints];
        _selector.Reset(endpoints);
        _ = ProbeSavedEndpointsAsync();
        _logger?.Write("endpoints_saved", $"{endpoints.Count} endpoint");
    }

    private async Task ProbeSavedEndpointsAsync()
    {
        try
        {
            if (_monitor is not null)
            {
                await _monitor.ProbeOnceAsync(CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown mentre il round era in volo
        }
        catch (Exception ex)
        {
            _logger?.Write("probe_once_failed", ex.Message);
        }
    }

    private static System.Windows.Media.ImageSource CreateTrayIcon()
    {
        var image = new System.Windows.Media.Imaging.BitmapImage(
            new Uri("pack://application:,,,/Assets/gangio.ico", UriKind.Absolute));
        image.Freeze();
        return image;
    }

    private async Task AbortAndClearAsync()
    {
        try
        {
            if (_controller is not null)
            {
                await _controller.AbortAsync(CancellationToken.None);
            }
        }
        finally
        {
            _gestures?.Reset();
            if (_hooks is not null)
            {
                _hooks.AbortActive = false;
            }
        }
    }

    private GestureRouter CreateGestureRouter()
    {
        _gestureRouter = new GestureRouter(
            () => _controller?.Start(),
            () => _controller?.StopAsync(CancellationToken.None) ?? Task.CompletedTask,
            AbortAndClearAsync,
            active =>
            {
                if (_hooks is not null)
                {
                    _hooks.AbortActive = active;
                }
            });

        return _gestureRouter;
    }

    private DictationSession CreateSession()
    {
        ClientConfig config = _config!;
        TimeProvider time = TimeProvider.System;
        return new DictationSession(
            new WebSocketDictationTransport(),
            time,
            TimeSpan.FromSeconds(config.Server.ResponseTimeoutSeconds));
    }

    private void HandleRawEvent(RawKeyEvent raw, ClientConfig config)
    {
        KeyCode abortKey = KeyCodes.Parse(config.Hotkey.AbortKey);
        if (!raw.FromMouse && raw.VirtualKey == abortKey.VirtualKey)
        {
            if (raw.IsDown)
            {
                _gestureRouter?.OnGesture(GestureKind.Abort);
            }

            return;
        }

        _gestures?.OnRaw(raw);
    }

    public async Task RequestShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownRequested, 1) != 0)
        {
            Task? existing;
            lock (_shutdownGate) existing = _shutdownTask;
            if (existing is not null) await existing.ConfigureAwait(true);
            return;
        }

        Task stopping;
        lock (_shutdownGate) stopping = _shutdownTask ??= ShutdownCoreAsync();
        await stopping.ConfigureAwait(true);
        Shutdown();
    }

    private async Task StopOwnedAsync()
    {
        // Prima si ferma chi produce eventi (hook, gesture pump, monitor),
        // poi chi li consuma, infine le risorse native. Nessun timeout: il
        // runtime attende il ritorno delle chiamate native prima del dispose.
        _hooks?.Dispose();
        _gestures?.Dispose();
        _monitorCts?.Cancel();
        _monitor?.Dispose();

        if (_controller is not null)
        {
            await _controller.AbortAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (_runtime is not null)
        {
            await _runtime.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        if (_recordingQueue is not null)
        {
            await _recordingQueue.DisposeAsync().ConfigureAwait(false);
        }

        if (_controller is not null)
        {
            await _controller.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void DisposeDesktop()
    {
        _monitorCts?.Dispose();
        _monitorHttp?.Dispose();
        _pasteDispatcher?.Dispose();
        _soundPlayer?.Dispose();
        _tray?.Dispose();
        _settings?.Close();
        _splash?.Close();
        _pill?.Close();
        _recovery?.Discard();
    }

    private async Task ShutdownCoreAsync()
    {
        try
        {
            _logger?.Write("shutdown", "Shutting down.");
            await StopOwnedAsync().ConfigureAwait(false);
            await Dispatcher.InvokeAsync(DisposeDesktop);
        }
        catch (Exception ex)
        {
            _logger?.Write("shutdown_error", ex.Message);
        }
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        // WPF calls this on the UI thread. Blocking here would deadlock the
        // continuations that eventually call Shutdown(); let the normal
        // asynchronous path finish while Windows is ending the session.
        _ = RequestShutdownAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (_shutdownTask is null)
            {
                // Solo il percorso di emergenza (chiusura non passata da
                // RequestShutdownAsync) blocca qui. Il percorso normale ha già
                // atteso il ritorno nativo prima di Shutdown().
                StopOwnedAsync().GetAwaiter().GetResult();
                DisposeDesktop();
            }
            else
            {
                _shutdownTask.GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            _logger?.Write("shutdown_error", ex.Message);
        }
        finally
        {
            _instanceHandle?.Dispose();
            base.OnExit(e);
        }
    }
}
