using System.Collections.Generic;
using System.Windows;
using System.Threading;
using GangioKeet.Client.Audio;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Controller;
using GangioKeet.Client.Platform;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recovery;
using Hardcodet.Wpf.TaskbarNotification;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Controller della tray icon: mostra lo stato del server, azioni su recovery,
/// selezione microfono, apertura privacy e configurazione.
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly TaskbarIcon _tray;
    private readonly DictationController _controller;
    private readonly RecoveryStore _recovery;
    private readonly ClientConfig _config;
    private readonly IAudioDeviceProvider _devices;
    private readonly string _configPath;
    private readonly Action _openSettings;
    private readonly FileLogger? _logger;
    private readonly Action<Action> _post;
    private readonly Func<Task>? _exit;

    private string? _selectedDeviceId;
    private System.Windows.Controls.MenuItem? _microphoneMenu;
    private bool _disposed;

    public TrayController(
        TaskbarIcon tray,
        DictationController controller,
        RecoveryStore recovery,
        ClientConfig config,
        IAudioDeviceProvider devices,
        string configPath,
        Action openSettings,
        FileLogger? logger = null,
        Action<Action>? post = null,
        Func<Task>? exit = null)
    {
        _tray = tray;
        _controller = controller;
        _recovery = recovery;
        _exit = exit;
        _config = config;
        _devices = devices;
        _configPath = configPath;
        _openSettings = openSettings;
        _logger = logger;
        _selectedDeviceId = config.Audio.InputDevice;

        // Gli eventi del monitor e del recovery arrivano da thread di background:
        // ogni modifica di oggetti WPF deve passare dal thread UI.
        _post = post ?? (action =>
        {
            if (_tray.Dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                _tray.Dispatcher.BeginInvoke(action);
            }
        });

        _recovery.Changed += OnRecoveryChanged;

        BuildContextMenu();
        UpdateRecoveryMenu();
        UpdateTooltip();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _recovery.Changed -= OnRecoveryChanged;

        _tray.Dispose();
    }

    private void BuildContextMenu()
    {
        _tray.ContextMenu = new System.Windows.Controls.ContextMenu();
        _tray.ContextMenu.Opened += (_, _) => UpdateRecoveryMenu();
        _tray.ToolTipText = "GangioKeet";

        var retryItem = new System.Windows.Controls.MenuItem
        {
            Header = "Riprova ultima dettatura",
            IsEnabled = false,
        };
        retryItem.Click += async (_, _) =>
        {
            await _controller.RetryLastAsync(CancellationToken.None);
        };

        var copyItem = new System.Windows.Controls.MenuItem
        {
            Header = "Copia ultimo testo",
            IsEnabled = false,
        };
        copyItem.Click += async (_, _) =>
        {
            await _controller.CopyLastTextAsync(CancellationToken.None);
        };

        var discardItem = new System.Windows.Controls.MenuItem
        {
            Header = "Scarta recupero",
            IsEnabled = false,
        };
        discardItem.Click += (_, _) => _controller.DiscardLast();

        _tray.ContextMenu.Items.Add(retryItem);
        _tray.ContextMenu.Items.Add(copyItem);
        _tray.ContextMenu.Items.Add(discardItem);
        _tray.ContextMenu.Items.Add(new System.Windows.Controls.Separator());

        _microphoneMenu = new System.Windows.Controls.MenuItem { Header = "Microfono" };
        _microphoneMenu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "Caricamento…",
            IsEnabled = false,
        });
        _microphoneMenu.SubmenuOpened += (_, _) => _ = RefreshMicrophonesAsync();
        _tray.ContextMenu.Items.Add(_microphoneMenu);

        _tray.ContextMenu.Items.Add(new System.Windows.Controls.Separator());
        _tray.ContextMenu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "Apri impostazioni microfono",
            Command = new RelayCommand(_ => new MicrophonePrivacy().OpenSettings()),
        });
        _tray.ContextMenu.Items.Add(new System.Windows.Controls.Separator());
        _tray.ContextMenu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "Impostazioni…",
            Command = new RelayCommand(_ => _openSettings()),
        });
        var exitItem = new System.Windows.Controls.MenuItem { Header = "Esci" };
        exitItem.Click += async (_, _) =>
        {
            try
            {
                if (_exit is not null) await _exit();
                else Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                _logger?.Write("shutdown_request_failed", ex.Message);
            }
        };
        _tray.ContextMenu.Items.Add(exitItem);

        _tray.TrayMouseDoubleClick += (_, _) => _openSettings();
    }

    private async Task RefreshMicrophonesAsync()
    {
        if (_microphoneMenu is null)
        {
            return;
        }

        try
        {
            IReadOnlyList<AudioDeviceInfo> devices = await _devices.ListAsync(CancellationToken.None);
            _post(() => PopulateMicrophoneMenu(devices));
        }
        catch (Exception ex)
        {
            _logger?.Write("microphone_enumeration_failed", ex.Message);
            _post(() => PopulateMicrophoneMenu([]));
        }
    }

    private void PopulateMicrophoneMenu(IReadOnlyList<AudioDeviceInfo> devices)
    {
        if (_microphoneMenu is null)
        {
            return;
        }

        System.Windows.Controls.MenuItem menu = _microphoneMenu;
        menu.Items.Clear();

        var defaultItem = new System.Windows.Controls.MenuItem
        {
            Header = "Predefinito di Windows",
            IsCheckable = true,
            IsChecked = MicrophoneMenuPolicy.IsDefaultSelected(_selectedDeviceId),
        };
        defaultItem.Click += (_, _) => ApplyMicrophone(null);
        menu.Items.Add(defaultItem);
        menu.Items.Add(new System.Windows.Controls.Separator());

        foreach (AudioDeviceInfo device in MicrophoneMenuPolicy.Order(devices))
        {
            AudioDeviceInfo selected = device;
            var item = new System.Windows.Controls.MenuItem
            {
                Header = selected.Name,
                IsCheckable = true,
                IsChecked = MicrophoneMenuPolicy.IsDeviceSelected(_selectedDeviceId, selected.Id),
                ToolTip = selected.Id,
            };
            item.Click += (_, _) => ApplyMicrophone(selected.Id);
            menu.Items.Add(item);
        }

        if (devices.Count == 0)
        {
            menu.Items.Add(new System.Windows.Controls.MenuItem
            {
                Header = "Nessun microfono attivo",
                IsEnabled = false,
            });
        }
    }

    private void ApplyMicrophone(string? deviceId)
    {
        _selectedDeviceId = deviceId;
        _controller.SetInputDevice(deviceId);

        if (!ConfigWriter.SetAudioInputDevice(_configPath, deviceId))
        {
            _logger?.Write(
                "microphone_persist_failed",
                $"Could not persist audio.input_device to {_configPath}; the choice stays active in memory.");
        }
    }

    private void UpdateTooltip()
    {
        _tray.ToolTipText = "GangioKeet";
    }

    private void OnRecoveryChanged(object? sender, EventArgs e)
    {
        _post(UpdateRecoveryMenu);
    }

    private void UpdateRecoveryMenu()
    {
        RecoveryItem? item = _recovery.GetCurrent();
        bool hasRecovery = item is not null;
        bool hasText = item?.Result is not null || _controller.HasLastText;
        bool isActive = _controller.State != ClientState.Idle;

        if (_tray.ContextMenu?.Items is not { Count: > 2 }) return;

        if (_tray.ContextMenu.Items[0] is System.Windows.Controls.MenuItem retry)
        {
            retry.IsEnabled = RecoveryMenuPolicy.CanRetry(hasRecovery, isActive);
        }

        if (_tray.ContextMenu.Items[1] is System.Windows.Controls.MenuItem copy)
        {
            copy.IsEnabled = RecoveryMenuPolicy.CanCopy(hasText);
        }

        if (_tray.ContextMenu.Items[2] is System.Windows.Controls.MenuItem discard)
        {
            discard.IsEnabled = RecoveryMenuPolicy.CanDiscard(hasRecovery);
        }
    }
}

/// <summary>
/// Policy pura di abilitazione delle voci tray: testabile senza WPF.
/// Il retry è disabilitato durante una sessione attiva; copia resta disponibile
/// finché c'è un testo (recupero o ultima dettatura riuscita), scarta richiede
/// un recupero.
/// </summary>
public static class RecoveryMenuPolicy
{
    public static bool CanRetry(bool hasRecovery, bool isActive) => hasRecovery && !isActive;

    public static bool CanCopy(bool hasText) => hasText;

    public static bool CanDiscard(bool hasRecovery) => hasRecovery;
}

/// <summary>Semplice implementazione di ICommand per binding ai menu item.</summary>
public sealed class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action<object?> _execute;

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public RelayCommand(Action<object?> execute) => _execute = execute;

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute(parameter);
}
