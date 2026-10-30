using GangioKeet.Inference;

namespace GangioKeet.Client.Ui;

public static class ModelSelectionChoices
{
    public static IReadOnlyList<ModelArtifact> Asr(IEnumerable<ModelArtifact> candidates) =>
        candidates.Where(candidate => candidate.RuntimeId == "transcribe-vulkan")
            .OrderBy(Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<ModelArtifact> Cleaners(IEnumerable<ModelArtifact> candidates) =>
        candidates.Where(candidate => candidate.RuntimeId == "llama-vulkan")
            .OrderBy(Label, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static string Label(ModelArtifact artifact) =>
        string.IsNullOrWhiteSpace(artifact.DisplayName) ? artifact.Id : artifact.DisplayName;

    public static bool CleanerEnabled(bool refinement) => refinement;
}
