namespace GangioKeet.Client.Controller;

/// <summary>
/// Release-to-paste latency for one turn. The release timestamp is captured
/// on the first Stop (a repeated Stop never moves it); a failed paste leaves
/// <see cref="ReleaseToPasteMs"/> null and records the reason separately.
/// </summary>
public sealed class TurnTiming(TimeProvider time)
{
    private readonly object _gate = new();
    private long? _released;
    private long? _pasted;
    private string? _failure = "not_released";

    public double? ReleaseToPasteMs
    {
        get
        {
            lock (_gate)
            {
                return _released is long released && _pasted is long pasted
                    ? time.GetElapsedTime(released, pasted).TotalMilliseconds
                    : null;
            }
        }
    }

    public string? FailureReason
    {
        get { lock (_gate) return _failure; }
    }

    public void MarkReleased(long timestamp)
    {
        lock (_gate)
        {
            _released ??= timestamp;
            if (_failure == "not_released") _failure = "not_pasted";
        }
    }

    public void MarkPasted(long timestamp)
    {
        lock (_gate)
        {
            if (_released is null) return;
            _pasted = timestamp;
            _failure = null;
        }
    }

    public void MarkPasteFailed(string reason)
    {
        lock (_gate)
        {
            _pasted = null;
            _failure = reason;
        }
    }
}
