namespace GangioKeet.Client.Protocol;

/// <summary>Esito di un singolo probe di un endpoint.</summary>
public readonly record struct ProbeResult(
    bool Reachable,
    double? LatencyMs,
    bool? Ready,
    bool? Busy,
    bool? Draining,
    string? ErrorCode);
