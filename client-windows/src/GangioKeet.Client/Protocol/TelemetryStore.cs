namespace GangioKeet.Client.Protocol;

/// <summary>Metriche dell'ultima dettatura e contatori, senza contenuto dettato.</summary>
public readonly record struct TelemetrySnapshot(
    int Successes,
    int Failures,
    int Retries,
    double? LastDictationMs,
    double? LastStartMs,
    string? LastBackend,
    string? LastError);

public readonly record struct TurnTimingRecord(
    long TurnKey,
    double? ReleaseToPasteMs,
    string? FailureReason,
    DateTimeOffset RecordedAt);

/// <summary>Telemetria in RAM, thread-safe: i codici restano codici.</summary>
public sealed class TelemetryStore
{
    private readonly object _gate = new();

    private int _successes;
    private int _failures;
    private int _retries;
    private double? _lastDictationMs;
    private double? _lastStartMs;
    private string? _lastBackend;
    private string? _lastError;
    private readonly Dictionary<long, TurnTimingRecord> _timings = [];

    public void RecordDictation(
        bool success,
        TimeSpan total,
        TimeSpan? start,
        string backendName,
        string? errorCode)
    {
        lock (_gate)
        {
            if (success)
            {
                _successes++;
                _lastError = null;
            }
            else
            {
                _failures++;
                _lastError = errorCode;
            }

            _lastDictationMs = total.TotalMilliseconds;
            _lastStartMs = start?.TotalMilliseconds;
            _lastBackend = backendName;
        }
    }

    public void RecordRetry()
    {
        lock (_gate)
        {
            _retries++;
        }
    }

    public void RecordTiming(long turnKey, double? releaseToPasteMs, string? failureReason)
    {
        lock (_gate)
        {
            _timings[turnKey] = new(turnKey, releaseToPasteMs, failureReason, DateTimeOffset.UtcNow);
        }
    }

    public TurnTimingRecord? TimingFor(long turnKey)
    {
        lock (_gate)
        {
            return _timings.TryGetValue(turnKey, out TurnTimingRecord timing) ? timing : null;
        }
    }

    public TelemetrySnapshot Snapshot()
    {
        lock (_gate)
        {
            return new TelemetrySnapshot(
                _successes,
                _failures,
                _retries,
                _lastDictationMs,
                _lastStartMs,
                _lastBackend,
                _lastError);
        }
    }
}
