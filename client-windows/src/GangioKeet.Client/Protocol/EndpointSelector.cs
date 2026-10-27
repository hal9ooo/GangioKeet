using GangioKeet.Client.Configuration;

namespace GangioKeet.Client.Protocol;

/// <summary>
/// Decide quale endpoint usare. Vince la mediana di latenza più bassa fra i
/// raggiungibili, con isteresi: l'attivo resta al suo posto finché un
/// concorrente non è più veloce del margine configurato, oppure finché l'attivo
/// non smette di rispondere. La storia delle misure è chiavata sulla
/// destinazione (<c>schema://host:porta</c>), quindi rinominare una riga non la
/// azzera.
/// </summary>
public sealed class EndpointSelector : IEndpointResolver
{
    private const int Window = 5;

    private readonly object _gate = new();
    private readonly int _switchMarginPercent;
    private Dictionary<string, State> _states;

    private EndpointConfig _active;

    public EndpointSelector(IReadOnlyList<EndpointConfig> endpoints, int switchMarginPercent)
    {
        if (endpoints.Count == 0)
        {
            throw new ArgumentException("At least one endpoint is required.", nameof(endpoints));
        }

        _switchMarginPercent = Math.Clamp(switchMarginPercent, 0, 90);
        _active = endpoints.FirstOrDefault(e => e.IsPrimary) ?? endpoints[0];
        _states = endpoints.ToDictionary(e => e.Key, e => new State(e), StringComparer.OrdinalIgnoreCase);
    }

    public event Action? Changed;

    public EndpointConfig Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    public void Apply(EndpointConfig endpoint, ProbeResult result)
    {
        lock (_gate)
        {
            // Un round partito prima di un Reset può riferirsi a una
            // destinazione appena rimossa: non va ricreata.
            if (!_states.TryGetValue(endpoint.Key, out State? state))
            {
                return;
            }

            state.Apply(result);
            _active = SelectLocked();
        }

        Changed?.Invoke();
    }

    public EndpointStats StatsFor(EndpointConfig endpoint)
    {
        lock (_gate)
        {
            return _states.TryGetValue(endpoint.Key, out State? state)
                ? state.ToStats()
                : new EndpointStats(endpoint, false, false, null, null, 0, null, null, null, null, null);
        }
    }

    public IReadOnlyList<EndpointStats> Snapshot()
    {
        lock (_gate)
        {
            return _states.Values.Select(state => state.ToStats()).ToList();
        }
    }

    /// <summary>
    /// Sostituisce l'elenco dopo un salvataggio. Conserva le misure delle
    /// destinazioni rimaste, elimina le altre e riallinea l'attivo.
    /// </summary>
    public void Reset(IReadOnlyList<EndpointConfig> endpoints)
    {
        if (endpoints.Count == 0)
        {
            throw new ArgumentException("At least one endpoint is required.", nameof(endpoints));
        }

        lock (_gate)
        {
            var next = new Dictionary<string, State>(StringComparer.OrdinalIgnoreCase);
            foreach (EndpointConfig endpoint in endpoints)
            {
                if (_states.TryGetValue(endpoint.Key, out State? existing))
                {
                    existing.Endpoint = endpoint;
                    next[endpoint.Key] = existing;
                }
                else
                {
                    next[endpoint.Key] = new State(endpoint);
                }
            }

            _states = next;

            _active = _states.TryGetValue(_active.Key, out State? activeState)
                ? activeState.Endpoint
                : endpoints.FirstOrDefault(e => e.IsPrimary) ?? endpoints[0];
        }

        Changed?.Invoke();
    }

    private EndpointConfig SelectLocked()
    {
        List<State> reachable = _states.Values
            .Where(state => state.Reachable && state.MedianLatency is not null)
            .OrderBy(state => state.MedianLatency)
            .ToList();

        if (reachable.Count == 0)
        {
            return _active;
        }

        State best = reachable[0];
        State? primary = reachable.FirstOrDefault(state => state.Endpoint.IsPrimary);

        // Parità entro 1 ms: il primario vince, così il ruolo conta ancora.
        if (primary is not null
            && primary.MedianLatency is not null
            && best.MedianLatency is not null
            && primary.MedianLatency.Value - best.MedianLatency.Value < 1)
        {
            best = primary;
        }

        if (!_states.TryGetValue(_active.Key, out State? activeState)
            || !activeState.Reachable
            || activeState.MedianLatency is null)
        {
            return best.Endpoint;
        }

        if (best.MedianLatency is null)
        {
            return _active;
        }

        double threshold = activeState.MedianLatency.Value * (1 - _switchMarginPercent / 100.0);
        return best.MedianLatency.Value < threshold ? best.Endpoint : _active;
    }

    private sealed class State
    {
        private readonly Queue<double> _latencies = new();

        private bool _reachable;
        private double? _lastLatency;
        private int _failures;
        private bool? _ready;
        private bool? _busy;
        private bool? _draining;
        private string? _lastError;
        private DateTimeOffset? _checkedAt;

        public State(EndpointConfig endpoint)
        {
            Endpoint = endpoint;
        }

        public EndpointConfig Endpoint { get; set; }

        public bool Reachable => _reachable;

        public double? MedianLatency => _latencies.Count == 0 ? null : Median();

        public void Apply(ProbeResult result)
        {
            _checkedAt = DateTimeOffset.UtcNow;

            if (result.Reachable)
            {
                _reachable = true;
                _failures = 0;
                _lastError = null;
                _lastLatency = result.LatencyMs ?? 0;
                _latencies.Enqueue(_lastLatency.Value);
                while (_latencies.Count > Window)
                {
                    _latencies.Dequeue();
                }

                _ready = result.Ready;
                _busy = result.Busy;
                _draining = result.Draining;
            }
            else
            {
                _reachable = false;
                _failures++;
                _lastError = result.ErrorCode;
            }
        }

        public EndpointStats ToStats() => new(
            Endpoint,
            Measured: _checkedAt is not null,
            Reachable: _reachable,
            LastLatencyMs: _lastLatency,
            MedianLatencyMs: MedianLatency,
            ConsecutiveFailures: _failures,
            Ready: _ready,
            Busy: _busy,
            Draining: _draining,
            LastError: _lastError,
            LastCheckedAt: _checkedAt);

        private double Median()
        {
            double[] sorted = [.. _latencies.OrderBy(value => value)];
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 0
                ? (sorted[middle - 1] + sorted[middle]) / 2
                : sorted[middle];
        }
    }
}
