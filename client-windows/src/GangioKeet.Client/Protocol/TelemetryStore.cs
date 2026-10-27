namespace GangioKeet.Client.Protocol;

/// <summary>Metriche dell'ultima dettatura e contatori, senza contenuto dettato.</summary>
public readonly record struct TelemetrySnapshot(
    int Successes,
    int Failures,
    int Retries,
    double? LastDictationMs,
    double? LastConnectMs,
    string? LastEndpoint,
    string? LastError);

/// <summary>Telemetria in RAM, thread-safe: i codici restano codici.</summary>
public sealed class TelemetryStore
{
    private readonly object _gate = new();

    private int _successes;
    private int _failures;
    private int _retries;
    private double? _lastDictationMs;
    private double? _lastConnectMs;
    private string? _lastEndpoint;
    private string? _lastError;

    public void RecordDictation(
        bool success,
        TimeSpan total,
        TimeSpan? connect,
        string endpointName,
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
            _lastConnectMs = connect?.TotalMilliseconds;
            _lastEndpoint = endpointName;
        }
    }

    public void RecordRetry()
    {
        lock (_gate)
        {
            _retries++;
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
                _lastConnectMs,
                _lastEndpoint,
                _lastError);
        }
    }
}
