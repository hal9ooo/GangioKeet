using GangioKeet.Client.Ui;
using GangioKeet.Inference;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class ModelSelectionChoicesTests
{
    private static ModelArtifact Artifact(string id, string runtime, string? displayName = null) => new(
        id, "fake", "owner/model", "0123456789abcdef0123456789abcdef01234567",
        [new ModelFile($"{id}.gguf", new string('a', 64))],
        "F16", "Apache-2.0", runtime, DisplayName: displayName);

    [Fact]
    public void FiltersAsrAndCleanerCandidates()
    {
        ModelArtifact[] all =
        [
            Artifact("parakeet_v3", "transcribe-vulkan"),
            Artifact("gemma4_e2b", "llama-vulkan")
        ];
        Assert.Single(ModelSelectionChoices.Asr(all));
        Assert.Single(ModelSelectionChoices.Cleaners(all));
    }

    [Fact]
    public void LabelFallsBackToId()
    {
        Assert.Equal("parakeet_v3", ModelSelectionChoices.Label(Artifact("parakeet_v3", "transcribe-vulkan")));
    }

    [Fact]
    public void LabelUsesDisplayName()
    {
        Assert.Equal("Parakeet TDT 0.6B v3",
            ModelSelectionChoices.Label(Artifact("parakeet_v3", "transcribe-vulkan", "Parakeet TDT 0.6B v3")));
    }

    [Fact]
    public void RefinementCheckDisablesCleanerCombo()
    {
        Assert.False(ModelSelectionChoices.CleanerEnabled(false));
        Assert.True(ModelSelectionChoices.CleanerEnabled(true));
    }
}
