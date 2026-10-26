namespace GangioKeet.Client.Protocol;

/// <summary>
/// Stato di salute del server remoto. <see cref="Reachable"/> indica se il server
/// risponde su /health. I campi nullable <see cref="Ready"/>, <see cref="Busy"/> e
/// <see cref="Draining"/> sono valorizzati solo se il server espone l'endpoint /ready.
/// </summary>
public readonly record struct HealthStatus(bool Reachable, bool? Ready, bool? Busy, bool? Draining);
