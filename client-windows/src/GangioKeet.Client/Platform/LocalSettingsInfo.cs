namespace GangioKeet.Client.Platform;

public sealed record LocalSettingsInfo(
    string Mode,
    string Backend,
    string Pair,
    string State,
    string? Error);
