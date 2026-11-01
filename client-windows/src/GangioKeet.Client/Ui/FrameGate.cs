namespace GangioKeet.Client.Ui;

/// <summary>
/// Limits rendering to roughly the requested frame rate while following the
/// compositor clock. On high-refresh displays the remainder is carried over so
/// the average stays close to the target instead of dropping to a divisor.
/// </summary>
internal sealed class FrameGate(TimeSpan target)
{
    private TimeSpan? _last;
    private TimeSpan _accumulated;

    public bool ShouldRender(TimeSpan now)
    {
        if (_last is not { } last)
        {
            _last = now;
            return true;
        }

        TimeSpan delta = now - last;
        _last = now;
        if (delta < TimeSpan.Zero)
        {
            _accumulated = TimeSpan.Zero;
            return true;
        }

        _accumulated += delta;
        if (_accumulated < target) return false;
        _accumulated -= target;
        if (_accumulated > target) _accumulated = target;
        return true;
    }

    public void Reset()
    {
        _last = null;
        _accumulated = TimeSpan.Zero;
    }
}
