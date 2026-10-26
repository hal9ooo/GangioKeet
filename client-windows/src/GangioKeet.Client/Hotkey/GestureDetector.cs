using GangioKeet.Client.Configuration;

namespace GangioKeet.Client.Hotkey;

public sealed class GestureDetector
{
    private enum State
    {
        Idle,
        Holding,
        AwaitingSecondTap,
        HandsFreeHolding,
        HandsFreeReady,
        ToggleHolding,
        ToggleReleased,
    }

    private static readonly IReadOnlyList<GestureKind> NoGesture = [];

    private readonly HotkeyConfig _config;
    private readonly TimeProvider _time;
    private readonly bool _isToggleMode;

    private State _state = State.Idle;
    private bool _physicallyDown;
    private long _downTimestamp;
    private long _releaseTimestamp;

    public GestureDetector(HotkeyConfig config, TimeProvider time)
    {
        _config = config;
        _time = time;
        _isToggleMode = string.Equals(config.Mode?.Trim(), "toggle", StringComparison.OrdinalIgnoreCase);
    }

    public bool AwaitingSecondTap => _state == State.AwaitingSecondTap;

    public IReadOnlyList<GestureKind> OnKeyDown()
    {
        // Keydowns arriving while the physical key is already down are autorepeats
        if (_physicallyDown)
        {
            return NoGesture;
        }

        _physicallyDown = true;
        return _state switch
        {
            State.Idle => Start(),
            State.AwaitingSecondTap => EnterHandsFree(),
            State.HandsFreeReady or State.ToggleReleased => Stop(),
            State.Holding or State.HandsFreeHolding or State.ToggleHolding => NoGesture,
            _ => NoGesture,
        };
    }

    public IReadOnlyList<GestureKind> OnKeyUp()
    {
        if (!_physicallyDown)
        {
            return NoGesture;
        }

        _physicallyDown = false;
        switch (_state)
        {
            case State.Holding:
                return IsHeldLongEnough() ? Stop() : EnterGap();
            case State.HandsFreeHolding:
                _state = State.HandsFreeReady;
                return NoGesture;
            case State.ToggleHolding:
                _state = State.ToggleReleased;
                return NoGesture;
            default:
                return NoGesture;
        }
    }

    public IReadOnlyList<GestureKind> OnTick()
    {
        if (_state != State.AwaitingSecondTap)
        {
            return NoGesture;
        }

        return GapExpired() ? Stop() : NoGesture;
    }

    public void Reset() => _state = State.Idle;

    private IReadOnlyList<GestureKind> Start()
    {
        _state = _isToggleMode ? State.ToggleHolding : State.Holding;
        _downTimestamp = _time.GetTimestamp();
        return [GestureKind.Start];
    }

    private IReadOnlyList<GestureKind> Stop()
    {
        _state = State.Idle;
        return [GestureKind.StopAndTranscribe];
    }

    private IReadOnlyList<GestureKind> EnterGap()
    {
        _releaseTimestamp = _time.GetTimestamp();
        _state = State.AwaitingSecondTap;
        return NoGesture;
    }

    private IReadOnlyList<GestureKind> EnterHandsFree()
    {
        // Gap window elapsed before this press was consumed. Falls back to stop.
        if (GapExpired())
        {
            return Stop();
        }
        _state = State.HandsFreeHolding;
        return [GestureKind.HandsFreeEntered];
    }

    private bool IsHeldLongEnough() => _time.GetElapsedTime(_downTimestamp) >= HoldThreshold;

    private bool GapExpired() => _time.GetElapsedTime(_releaseTimestamp) >= GapThreshold;

    private TimeSpan HoldThreshold => TimeSpan.FromMilliseconds(_config.DoubleTapMaxMs);

    private TimeSpan GapThreshold => TimeSpan.FromMilliseconds(_config.DoubleTapGapMs);
}
