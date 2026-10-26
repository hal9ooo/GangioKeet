namespace GangioKeet.Client.Hotkey;

/// <summary>
/// Traduce i gesti in azioni del controller. Un <c>Start</c> che arriva mentre un
/// abort è in corso (tap accidentale seguito da una nuova pressione, o Esc appena
/// premuto) non viene perso: viene rimandato a cleanup concluso.
/// </summary>
public sealed class GestureRouter
{
    private readonly Action _start;
    private readonly Func<Task> _stop;
    private readonly Func<Task> _abort;
    private readonly Action<bool> _setAbortActive;

    private int _abortInFlight;
    private int _restartRequested;

    public GestureRouter(Action start, Func<Task> stop, Func<Task> abort, Action<bool> setAbortActive)
    {
        _start = start;
        _stop = stop;
        _abort = abort;
        _setAbortActive = setAbortActive;
    }

    public void OnGesture(GestureKind kind)
    {
        switch (kind)
        {
            case GestureKind.Start:
                if (Volatile.Read(ref _abortInFlight) != 0)
                {
                    Interlocked.Exchange(ref _restartRequested, 1);
                    return;
                }

                _setAbortActive(true);
                _start();
                break;

            case GestureKind.StopAndTranscribe:
                _ = StopAsync();
                break;

            case GestureKind.Abort:
                Interlocked.Exchange(ref _restartRequested, 0);
                _ = AbortAsync();
                break;

            case GestureKind.HandsFreeEntered:
                break;
        }
    }

    private async Task AbortAsync()
    {
        Interlocked.Exchange(ref _abortInFlight, 1);
        try
        {
            await _abort().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // best effort: lo stato del controller resta la fonte di verità
        }
        finally
        {
            Interlocked.Exchange(ref _abortInFlight, 0);
            _setAbortActive(false);

            if (Interlocked.Exchange(ref _restartRequested, 0) != 0)
            {
                _setAbortActive(true);
                _start();
            }
        }
    }

    private async Task StopAsync()
    {
        try
        {
            await _stop().ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
        finally
        {
            _setAbortActive(false);
        }
    }
}
