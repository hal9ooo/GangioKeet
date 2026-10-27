using GangioKeet.Client.Configuration;

namespace GangioKeet.Client.Protocol;

/// <summary>
/// Fotografia immutabile dello stato misurato di un endpoint. La mediana è
/// calcolata sulle ultime cinque misure riuscite; un fallimento non cancella
/// la storia.
/// </summary>
public sealed record EndpointStats(
    EndpointConfig Endpoint,
    bool Measured,
    bool Reachable,
    double? LastLatencyMs,
    double? MedianLatencyMs,
    int ConsecutiveFailures,
    bool? Ready,
    bool? Busy,
    bool? Draining,
    string? LastError,
    DateTimeOffset? LastCheckedAt);
