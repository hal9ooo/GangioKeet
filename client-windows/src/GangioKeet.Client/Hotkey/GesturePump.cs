namespace GangioKeet.Client.Hotkey;

/// <summary>
/// Serializza gesti grezzi e tick del double-tap. Il timer da 25 ms esiste solo mentre
/// <see cref="GestureDetector.AwaitingSecondTap"/>; il lock interno garantisce che
/// keydown, keyup e tick non entrino mai contemporaneamente nel detector.
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
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            IReadOnlyList<GestureKind> kinds = raw.IsDown
                ? _detector.OnKeyDown()
                : _detector.OnKeyUp();

            Dispatch(kinds);
            UpdateTimer();
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
            _timer?.Dispose();
            _timer = null;
        }
    }

    private void Tick()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Dispatch(_detector.OnTick());
            UpdateTimer();
        }
    }

    private void Dispatch(IReadOnlyList<GestureKind> kinds)
    {
        foreach (GestureKind kind in kinds)
        {
            _dispatch(kind);
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

        _timer?.Dispose();
        _timer = null;
    }
}
