namespace GangioKeet.Client.Platform;

public sealed record LocalSettingsInfo(
    string Backend,
    string Pair,
    string State,
    string? Error,
    string ModelSelectionPath = "models/selected.json");
