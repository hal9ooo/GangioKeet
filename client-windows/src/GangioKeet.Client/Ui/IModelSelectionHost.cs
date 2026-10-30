using GangioKeet.Client.Platform;
using GangioKeet.Inference;

namespace GangioKeet.Client.Ui;

public interface IModelSelectionHost
{
    IReadOnlyList<ModelArtifact> AsrCandidates { get; }
    IReadOnlyList<ModelArtifact> CleanerCandidates { get; }
    ModelSelection Current { get; }
    bool IsAvailable(ModelArtifact artifact);
    Task ApplyAsync(ModelSelection selection, IProgress<ModelProvisionProgress>? progress, CancellationToken ct);
}
