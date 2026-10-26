using System.Windows;
using System.Threading;
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
    private readonly HealthMonitor _health;
    private readonly ClientConfig _config;

    private bool _disposed;

    public TrayController(
        TaskbarIcon tray,
        DictationController controller,
        RecoveryStore recovery,
        HealthMonitor health,
        ClientConfig config)
    {
        _tray = tray;
        _controller = controller;
        _recovery = recovery;
        _health = health;
        _config = config;

        _health.Changed += OnHealthChanged;
        _recovery.Changed += OnRecoveryChanged;

        BuildContextMenu();
        UpdateRecoveryMenu();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _health.Changed -= OnHealthChanged;
        _recovery.Changed -= OnRecoveryChanged;

        _tray.Dispose();
    }

    private void BuildContextMenu()
    {
        _tray.ContextMenu = new System.Windows.Controls.ContextMenu();
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
        _tray.ContextMenu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "Apri impostazioni microfono",
            Command = new RelayCommand(_ => new MicrophonePrivacy().OpenSettings()),
        });
        _tray.ContextMenu.Items.Add(new System.Windows.Controls.Separator());
        _tray.ContextMenu.Items.Add(new System.Windows.Controls.MenuItem
        {
            Header = "Esci",
            Command = new RelayCommand(_ => Application.Current.Shutdown()),
        });
    }

    private void OnHealthChanged(HealthStatus status)
    {
        _tray.ToolTipText = status.Reachable
            ? "GangioKeet — Online"
            : "GangioKeet — Offline";
    }

    private void OnRecoveryChanged(object? sender, EventArgs e)
    {
        UpdateRecoveryMenu();
    }

    private void UpdateRecoveryMenu()
    {
        bool hasRecovery = _recovery.GetCurrent() is not null;
        bool isActive = _controller.State != ClientState.Idle;

        if (_tray.ContextMenu?.Items is not { Count: > 2 }) return;

        if (_tray.ContextMenu.Items[0] is System.Windows.Controls.MenuItem retry)
        {
            retry.IsEnabled = hasRecovery && !isActive;
        }

        if (_tray.ContextMenu.Items[1] is System.Windows.Controls.MenuItem copy)
        {
            copy.IsEnabled = hasRecovery;
        }

        if (_tray.ContextMenu.Items[2] is System.Windows.Controls.MenuItem discard)
        {
            discard.IsEnabled = hasRecovery;
        }
    }
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
