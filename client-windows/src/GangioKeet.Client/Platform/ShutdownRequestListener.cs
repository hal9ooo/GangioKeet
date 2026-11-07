using System.Threading;

namespace GangioKeet.Client.Platform;

/// <summary>
/// Canale di chiusura per l'installer. L'app è tray-only e non ha una finestra
/// principale: il Restart Manager di Windows non riesce a chiuderla, quindi
/// Setup mostrerebbe "impossibile chiudere le applicazioni". Qui l'app aspetta
/// un evento con nome; l'installer lo segnala e la chiusura avviene pulita.
/// </summary>
public sealed class ShutdownRequestListener : IDisposable
{
    /// <summary>Nome dell'evento condiviso con l'installer.</summary>
    public const string EventName = @"Local\ChattyNeko.ExitRequested";

    private readonly EventWaitHandle _event;
    private readonly Thread _thread;
    private readonly Action _onRequested;
    private volatile bool _disposed;

    public ShutdownRequestListener(Action onRequested, string? eventName = null)
    {
        _onRequested = onRequested;

        // Se un'istanza precedente ha lasciato l'evento segnalato, ripartire
        // da zero: l'app non deve chiudersi appena avviata.
        _event = new EventWaitHandle(false, EventResetMode.ManualReset, eventName ?? EventName);
        _event.Reset();

        _thread = new Thread(WaitForRequest)
        {
            IsBackground = true,
            Name = "shutdown-request-listener",
        };
        _thread.Start();
    }

    private void WaitForRequest()
    {
        try
        {
            _event.WaitOne();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        if (!_disposed)
        {
            _onRequested();
        }
    }

    public void Dispose()
    {
        _disposed = true;

        try
        {
            _event.Set();
        }
        catch (ObjectDisposedException)
        {
        }

        _event.Dispose();
    }
}
