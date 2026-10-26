namespace GangioKeet.Client.Hotkey;

/// <summary>
/// Serializza gesti grezzi e tick del double-tap. Il timer da 25 ms esiste solo mentre
/// <see cref="GestureDetector.AwaitingSecondTap"/>. Il lock protegge il detector; il
/// dispatch avviene fuori dal lock, così un handler lento non blocca <see cref="Dispose"/>
/// (niente deadlock con il Dispatcher UI allo shutdown).
/// </summary>
public sealed class GesturePump : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(25);

    private readonly GestureDetector _detector;
    private readonly Action<GestureKind> _dispatch;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    private ITimer? _timer;
    private bool _disposed;

    public GesturePump(GestureDetector detector, Action<GestureKind> dispatch, TimeProvider time)
    {
        _detector = detector;
        _dispatch = dispatch;
        _time = time;
    }

    public void OnRaw(RawKeyEvent raw)
    {
        IReadOnlyList<GestureKind> kinds;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            kinds = raw.IsDown
                ? _detector.OnKeyDown()
                : _detector.OnKeyUp();
            UpdateTimer();
        }

        Dispatch(kinds);
    }

    /// <summary>Riporta il detector in Idle (dopo Esc) e spegne il timer del double-tap.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _detector.Reset();
            DisposeTimer();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeTimer();
        }
    }

    private void Tick()
    {
        IReadOnlyList<GestureKind> kinds;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            kinds = _detector.OnTick();
            UpdateTimer();
        }

        Dispatch(kinds);
    }

    private void Dispatch(IReadOnlyList<GestureKind> kinds)
    {
        foreach (GestureKind kind in kinds)
        {
            // Un handler che lancia non deve uccidere il thread degli hook.
            try
            {
                _dispatch(kind);
            }
            catch (Exception)
            {
            }
        }
    }

    private void UpdateTimer()
    {
        if (_detector.AwaitingSecondTap)
        {
            _timer ??= _time.CreateTimer(
                _ => Tick(),
                state: null,
                TickInterval,
                TickInterval);
            return;
        }

        DisposeTimer();
    }

    private void DisposeTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
