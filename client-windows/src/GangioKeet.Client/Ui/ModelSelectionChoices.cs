using GangioKeet.Inference;

namespace GangioKeet.Client.Ui;

public static class ModelSelectionChoices
{
    public sealed record ModelChoice(ModelArtifact Artifact, string Label, bool Available)
    {
        public string AvailabilityText => Available ? "presente" : "da scaricare";
    }

    public static IReadOnlyList<ModelArtifact> Asr(IEnumerable<ModelArtifact> candidates) =>
        candidates.Where(candidate => IsAsr(candidate))
            .OrderBy(Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool IsAsr(ModelArtifact candidate) =>
        candidate.RuntimeId is "transcribe-vulkan" or "nemo-speech-vulkan";

    public static IReadOnlyList<ModelArtifact> Cleaners(IEnumerable<ModelArtifact> candidates) =>
        candidates.Where(candidate => candidate.RuntimeId == "llama-vulkan")
            .OrderBy(Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<ModelChoice> AsrChoices(IEnumerable<ModelArtifact> candidates,
        Func<ModelArtifact, bool> isAvailable) =>
        Asr(candidates).Select(candidate => new ModelChoice(candidate, Label(candidate), isAvailable(candidate))).ToArray();

    public static IReadOnlyList<ModelChoice> CleanerChoices(IEnumerable<ModelArtifact> candidates,
        Func<ModelArtifact, bool> isAvailable) =>
        Cleaners(candidates).Select(candidate => new ModelChoice(candidate, Label(candidate), isAvailable(candidate))).ToArray();

    public static string Label(ModelArtifact artifact) =>
        string.IsNullOrWhiteSpace(artifact.DisplayName) ? artifact.Id : artifact.DisplayName;

    public static bool CleanerEnabled(bool refinement) => refinement;

    public static string Key(ModelSelection selection) =>
        $"{selection.SchemaVersion}:{selection.SttId}:{selection.CleanerId}:{selection.RefinementEnabled}";

    public static bool ShouldPopulate(bool applying, bool populating, bool timerEnabled,
        bool dropDownOpen, string? lastKey, string currentKey) =>
        !applying && !populating && !timerEnabled && !dropDownOpen
        && !string.Equals(lastKey, currentKey, StringComparison.Ordinal);
}
