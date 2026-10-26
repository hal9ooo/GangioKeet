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
    private RecoveryStore? _recovery;
    private HealthMonitor? _health;
    private TrayController? _tray;
    private PillWindow? _pill;
    private WasapiAudioCapture? _capture;
    private LowLevelInputHooks? _hooks;
    private GesturePump? _gestures;
    private GestureRouter? _gestureRouter;
    private StaPasteDispatcher? _pasteDispatcher;
    private SynthSoundPlayer? _soundPlayer;
    private HttpClient? _healthHttp;
    private CancellationTokenSource? _healthCts;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        try
        {
            _config = ConfigLoader.Load();
        }
        catch (ConfigException ex)
        {
            _logger.Write("config_error", ex.Message);
            MessageBox.Show(
                $"Configurazione non valida:\n{ex.Message}",
                "GangioKeet",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        try
        {
            Compose();
        }
        catch (Exception ex)
        {
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

        _controller = new DictationController(
            _capture,
            CreateSession,
            paste,
            _recovery,
            new Win32ForegroundWindowService(),
            new Win32ElevationChecker(),
            _pill,
            sound,
            config,
            time);

        _hooks = new LowLevelInputHooks(
            new Win32InputHookBackend(),
            new HookSuppressionPolicy(config.Hotkey.Key, config.Hotkey.AbortKey));

        _gestures = new GesturePump(
            new GestureDetector(config.Hotkey, time),
            CreateGestureRouter().OnGesture,
            time);
        _hooks.RawEvent += (_, raw) => HandleRawEvent(raw, config);

        _healthHttp = new HttpClient();
        _health = new HealthMonitor(
            _healthHttp,
            ServerEndpoint.HttpBase(config.Server),
            ApiCredential.Resolve(config.Server.ApiKeyEnv, Environment.GetEnvironmentVariable),
            time,
            TimeSpan.FromSeconds(config.Server.HealthCheckIntervalSeconds));

        _healthCts = new CancellationTokenSource();
        _ = _health.RunAsync(_healthCts.Token);

        _tray = new TrayController(
            new TaskbarIcon
            {
                IconSource = CreateTrayIcon(),
            },
            _controller,
            _recovery,
            _health,
            config,
            new AudioDevices(),
            ConfigLoader.DefaultPath,
            _logger);

        _hooks.Start();
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

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _logger?.Write("shutdown", "Shutting down.");

            // Prima si ferma chi produce eventi (hook, gesture pump, health), poi chi li
            // consuma, infine le risorse WPF. Mai il contrario: gli handler non devono
            // toccare servizi già disposti.
            _hooks?.Dispose();
            _gestures?.Dispose();
            _healthCts?.Cancel();

            if (_controller is not null)
            {
                try
                {
                    _controller.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException)
                {
                }
            }

            _health?.Dispose();
            _healthCts?.Dispose();
            _healthHttp?.Dispose();
            _pasteDispatcher?.Dispose();
            _soundPlayer?.Dispose();
            _tray?.Dispose();
            _pill?.Close();
            _recovery?.Discard();
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
