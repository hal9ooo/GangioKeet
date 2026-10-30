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
using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
using Hardcodet.Wpf.TaskbarNotification;

namespace GangioKeet.Client;

/// <summary>
/// Composition root: crea e collega tutti i servizi all'avvio, gestisce il
/// ciclo di vita (tray-only startup, shutdown pulito) e il reload della config.
/// </summary>
public partial class App : Application, IModelSelectionHost
{
    private SingleInstanceGuard? _guard;
    private IDisposable? _instanceHandle;
    private FileLogger? _logger;

    private ClientConfig? _config;
    private DictationController? _controller;
    private TelemetryStore? _telemetry;
    private RecoveryStore? _recovery;
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
    private AppRuntime? _runtime;
    private string? _localError;
    private RecordingStore? _recordingStore;
    private RecordingQueue? _recordingQueue;
    private readonly IModelProvisioner _modelProvisioner;
    private readonly INativeRuntimeProvisioner _runtimeProvisioner;

    public App() : this(null) { }

    internal App(IModelProvisioner? modelProvisioner = null,
        INativeRuntimeProvisioner? runtimeProvisioner = null)
    {
        _modelProvisioner = modelProvisioner ?? new ModelProvisioner();
        _runtimeProvisioner = runtimeProvisioner ?? new NeMoSpeechRuntimeProvisioner();
    }
    private string? _localSttId;
    private string? _localCleanerId;
    private string? _localBackend;
    private ModelSelection? _modelSelection;
    private ModelSelectionService? _modelSelectionService;
    private LocalModelResolver? _modelResolver;
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
            _splash = new SplashWindow(TimeSpan.FromSeconds(5));
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
        LocalInferenceSetup setup;
        try
        {
            string selectionPath = ModelSelectionPaths.Prepare(config);
            if (!ConfigWriter.SetModelSelectionPath(ConfigLoader.DefaultPath, selectionPath))
                _logger?.Write("model_selection_config_write_failed", selectionPath);
            setup = LocalInferenceFactory.Create(config,
                modelRoot: ModelSelectionPaths.InstallModelsRoot,
                runtimeProvisioner: _runtimeProvisioner);
        }
        catch (Exception error)
        {
            _localError = error.Message;
            _logger?.Write("local_model_setup_failed", error.Message);
            setup = LocalInferenceSetup.Failed(config, error);
        }
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

        WindowsMediaPlaybackPause media = new(new WindowsMediaSessionManager(), _logger);

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
            media,
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
            config,
            new AudioDevices(),
            ConfigLoader.DefaultPath,
            ShowSettings,
            _logger,
            exit: RequestShutdownAsync);

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
            ClientConfig config = _config ?? throw new InvalidOperationException("Configuration is not loaded.");
            string selectionPath = ResolveSelectionPath(config);
            ModelSelection selection = ModelSelectionFile.Load(selectionPath);

            LocalModelResolver resolver = CreateModelResolver(config, selectionPath);
            ResolvedModelSelection resolved = resolver.Resolve(selection);
            List<ModelArtifact> artifacts = [resolved.Stt];
            if (selection.RefinementEnabled && resolved.Cleaner is not null)
                artifacts.Add(resolved.Cleaner);

            Progress<ModelProvisionProgress> progress = new(LogModelProgress);
            await _modelProvisioner.EnsureModelsAsync(artifacts, resolver.ModelRoot,
                progress: progress).ConfigureAwait(false);
            await resolver.EnsureRuntimeAsync(resolved.Stt, progress, CancellationToken.None)
                .ConfigureAwait(false);

            await runtime.StartAsync(CancellationToken.None).ConfigureAwait(true);
            if (!runtime.IsReady)
            {
                _localError = runtime.Failure?.Message ?? "Local models are not ready.";
                _logger?.Write("local_runtime_failed", _localError);
                return;
            }

            _modelResolver = resolver;
            _modelSelection = selection;
            _modelSelectionService = new ModelSelectionService(
                (InferencePipeline)runtime.Pipeline, resolver, _modelProvisioner, selectionPath, selection);
            _localSttId = selection.SttId;
            _localCleanerId = selection.RefinementEnabled ? selection.CleanerId : "nessuno";
            _localBackend = config.Inference.Backend;
            _localError = null;
            _logger?.Write("local_runtime", runtime.State.ToString());
        }
        catch (Exception error)
        {
            _localError = error.Message;
            runtime.Fail(error);
            _logger?.Write("local_runtime_failed", error.Message);
        }
    }

    private void LogModelProgress(ModelProvisionProgress p)
    {
        if (p.Status == ModelProvisionStatus.Downloading && p.TotalBytes is > 0)
        {
            double pct = (double)p.BytesTransferred / p.TotalBytes.Value;
            _logger?.Write("model_download", $"{p.ModelId} ({p.RelativePath}): {pct:P0}");
        }
        else
        {
            _logger?.Write("model_download", $"{p.ModelId} ({p.RelativePath}): {p.Status}");
        }
    }

    private static string ResolveSelectionPath(ClientConfig config)
    {
        string rawSelection = config.Inference.ModelSelectionPath;
        return Path.IsPathRooted(rawSelection)
            ? rawSelection
            : (File.Exists(Path.Combine(AppContext.BaseDirectory, rawSelection))
                ? Path.Combine(AppContext.BaseDirectory, rawSelection)
                : Path.GetFullPath(rawSelection));
    }

    private LocalModelResolver CreateModelResolver(ClientConfig config, string selectionPath)
    {
        string modelsRoot = ModelSelectionPaths.InstallModelsRoot;
        string install = AppContext.BaseDirectory;
        string native = Environment.GetEnvironmentVariable("GANGIOKEET_TRANSCRIBE_DIR")
            ?? Path.Combine(install, "native", "transcribe");
        string llama = Environment.GetEnvironmentVariable("GANGIOKEET_LLAMA_DIR")
            ?? Path.Combine(install, "native", "llama");
        string lockPath = Environment.GetEnvironmentVariable("GANGIOKEET_RUNTIME_LOCK")
            ?? Path.Combine(install, "native", "runtime-lock.json");
        CleanupPolicy policy = CleanupPolicy.Load(config.Inference.CleanupPolicyPath);
        return new LocalModelResolver(modelsRoot, Path.Combine(modelsRoot, "candidates.json"),
            Path.Combine(modelsRoot, "integrity.json"), lockPath, config.Inference.CleanupPolicyPath,
            native, llama, config.Inference.Backend, policy, _runtimeProvisioner);
    }

    internal async Task ApplyModelSelectionAsync(ModelSelection desired,
        IProgress<ModelProvisionProgress>? progress, CancellationToken ct)
    {
        if (_modelSelectionService is null)
            throw new InvalidOperationException("The local model selection service is not ready.");
        await _modelSelectionService.ApplyAsync(desired, progress, ct).ConfigureAwait(false);
        _modelSelection = desired;
        _localError = null;
        _localSttId = desired.SttId;
        _localCleanerId = desired.RefinementEnabled ? desired.CleanerId : "nessuno";
        _localBackend = _config?.Inference.Backend;
        _logger?.Write("model_selection_applied", LocalSettingsInfo.FormatPair(_localSttId, _localCleanerId));
    }

    IReadOnlyList<ModelArtifact> IModelSelectionHost.AsrCandidates =>
        _modelResolver is null ? [] : ModelSelectionChoices.Asr(_modelResolver.Candidates);

    IReadOnlyList<ModelArtifact> IModelSelectionHost.CleanerCandidates =>
        _modelResolver is null ? [] : ModelSelectionChoices.Cleaners(_modelResolver.Candidates);

    ModelSelection IModelSelectionHost.Current =>
        _modelSelectionService?.Current ?? _modelSelection
        ?? new ModelSelection(2, "non disponibile", null, false);

    bool IModelSelectionHost.IsAvailable(ModelArtifact artifact) =>
        _modelResolver is not null
        && artifact.Files.All(file => File.Exists(Path.Combine(_modelResolver.ModelRoot, file.Path)));

    Task IModelSelectionHost.ApplyAsync(ModelSelection selection,
        IProgress<ModelProvisionProgress>? progress, CancellationToken ct) =>
        ApplyModelSelectionAsync(selection, progress, ct);

    Task IModelSelectionHost.RetryAsync(IProgress<ModelProvisionProgress>? progress, CancellationToken ct) =>
        RetryLocalRuntimeAsync(progress, ct);

    private async Task RetryLocalRuntimeAsync(IProgress<ModelProvisionProgress>? progress, CancellationToken ct)
    {
        if (_config is null || _controller is null) return;
        AppRuntime? old = _runtime;
        try
        {
            _localError = null;
            string selectionPath = ModelSelectionPaths.Prepare(_config);
            if (!ConfigWriter.SetModelSelectionPath(ConfigLoader.DefaultPath, selectionPath))
                _logger?.Write("model_selection_config_write_failed", selectionPath);
            LocalInferenceSetup setup = LocalInferenceFactory.Create(_config,
                modelRoot: ModelSelectionPaths.InstallModelsRoot,
                runtimeProvisioner: _runtimeProvisioner);
            AppRuntime runtime = new(setup.Pipeline, _recordingQueue);
            await StartLocalRuntimeAsync(runtime).ConfigureAwait(true);
            if (!runtime.IsReady)
            {
                await runtime.DisposeAsync().ConfigureAwait(false);
                return;
            }

            _controller.SetBackend(new LocalDictationBackend(setup.Pipeline,
                () => runtime.IsReady, _recordingQueue));
            _runtime = runtime;
            if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _localError = error.Message;
            _logger?.Write("local_runtime_retry_failed", error.Message);
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

        SettingsWindow window = new(
            _telemetry,
            ConfigLoader.DefaultPath,
            _logger,
            LocalInfo,
            _recordingStore,
            OnModelPairSaved,
            modelSelection: this);

        window.Closed += (_, _) => _settings = null;
        _settings = window;
        window.Show();
        window.Activate();
    }

    private LocalSettingsInfo LocalInfo() => new(
        _localBackend ?? _config?.Inference.Backend ?? "vulkan",
        LocalSettingsInfo.FormatPair(_localSttId, _localCleanerId),
        _localError is not null ? "Error" : (_runtime?.State.ToString() ?? "Stopped"),
        _localError ?? _runtime?.Failure?.Message,
        _config?.Inference.ModelSelectionPath ?? "models/selected.json");

    private void OnModelPairSaved(string selectionPath)
    {
        if (_config is null) return;
        _logger?.Write("model_pair_saved", selectionPath);
        _ = SwitchLocalModelPairAsync(selectionPath);
    }

    public async Task SwitchLocalModelPairAsync(string selectionPath)
    {
        if (_config is null || _controller is null) return;
        _config.Inference.ModelSelectionPath = selectionPath;

        var oldRuntime = _runtime;
        _runtime = null;
        if (oldRuntime is not null)
        {
            try { await oldRuntime.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { _logger?.Write("local_runtime_stop_error", ex.Message); }
        }

        try
        {
            _localError = null;
            LocalInferenceSetup setup = LocalInferenceFactory.Create(_config,
                runtimeProvisioner: _runtimeProvisioner);
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

            var localBackend = new LocalDictationBackend(_runtime.Pipeline, () => _runtime.IsReady, _recordingQueue);
            _controller.SetBackend(localBackend);
            _ = StartLocalRuntimeAsync(_runtime);
            _logger?.Write("switched_local_pair", $"{setup.SttId} + {setup.CleanerId}");
        }
        catch (Exception ex)
        {
            _localError = ex.Message;
            _logger?.Write("switch_local_pair_failed", ex.Message);
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
        // Prima si ferma chi produce eventi (hook, gesture pump),
        // poi chi li consuma, infine le risorse native. Nessun timeout: il
        // runtime attende il ritorno delle chiamate native prima del dispose.
        _hooks?.Dispose();
        _gestures?.Dispose();

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
        _pasteDispatcher?.Dispose();
        _soundPlayer?.Dispose();
        _tray?.Dispose();
        _settings?.Close();
        _splash?.Close();
        _pill?.Close();
        _recovery?.Discard();
        (_runtimeProvisioner as IDisposable)?.Dispose();
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
