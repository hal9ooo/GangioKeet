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
            Artifact("nemotron_transcribe_30b_a4b", "nemo-speech-vulkan"),
            Artifact("gemma4_e2b", "llama-vulkan")
        ];
        Assert.Equal(2, ModelSelectionChoices.Asr(all).Count);
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

    [Fact]
    public void MarksAvailability()
    {
        ModelArtifact present = Artifact("parakeet_v3", "transcribe-vulkan", "Parakeet");
        ModelArtifact missing = Artifact("qwen3_asr_1_7b", "transcribe-vulkan", "Qwen3-ASR");
        IReadOnlyList<ModelSelectionChoices.ModelChoice> choices =
            ModelSelectionChoices.AsrChoices([present, missing], artifact => artifact.Id == "parakeet_v3");

        Assert.Equal("presente", choices.Single(c => c.Artifact.Id == "parakeet_v3").AvailabilityText);
        Assert.Equal("da scaricare", choices.Single(c => c.Artifact.Id == "qwen3_asr_1_7b").AvailabilityText);
    }

    [Fact]
    public void DebouncePendingSkipsPopulate()
    {
        Assert.False(ModelSelectionChoices.ShouldPopulate(false, false, true, false, null, "k"));
        Assert.True(ModelSelectionChoices.ShouldPopulate(false, false, false, false, null, "k"));
        Assert.False(ModelSelectionChoices.ShouldPopulate(false, false, false, false, "k", "k"));
        Assert.True(ModelSelectionChoices.ShouldPopulate(false, false, false, false, "old", "k"));
        Assert.False(ModelSelectionChoices.ShouldPopulate(true, false, false, false, null, "k"));
    }
}
