namespace GangioKeet.Client.Platform;

public sealed record LocalSettingsInfo(
    string Backend,
    string Pair,
    string State,
    string? Error,
    string ModelSelectionPath = "models/selected.json")
{
    public static string FormatPair(string? sttId, string? cleanerId) =>
        string.IsNullOrWhiteSpace(sttId)
            ? "—"
            : string.IsNullOrWhiteSpace(cleanerId) || string.Equals(cleanerId, "nessuno", StringComparison.Ordinal)
                ? $"{sttId} + refinement off"
                : $"{sttId} + {cleanerId}";
}
