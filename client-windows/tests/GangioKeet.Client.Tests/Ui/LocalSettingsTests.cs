using System.Windows;
using GangioKeet.Client.Platform;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Ui;
using GangioKeet.Inference;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class LocalSettingsTests
{
    [Fact]
    public void LocalWindowOpensWithLocalPanel()
    {
        Exception? failure = RunOnStaThread(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"gangio-settings-{Guid.NewGuid():N}.yaml");
            SettingsWindow window = new(
                telemetry: new TelemetryStore(),
                configPath: path,
                logger: null,
                localInfo: () => new LocalSettingsInfo(
                    "vulkan", "qwen3_asr_1_7b + gemma4_e2b", "Ready", null),
                recordingStore: null,
                onModelPairSaved: null);

            Assert.True(window.UsesLocalServices);
            Assert.True(window.IsLocalPanelVisible);
            Assert.Equal("Ready", window.LocalState);
            Assert.Contains("qwen3_asr_1_7b", window.LocalPair);
            window.Close();
        });

        Assert.Null(failure);
    }

    [Fact]
    public void ModelSelectorsAppearWhenHostIsProvided()
    {
        Exception? failure = RunOnStaThread(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"gangio-settings-{Guid.NewGuid():N}.yaml");
            FakeSelectionHost host = new();
            SettingsWindow window = new(
                telemetry: new TelemetryStore(),
                configPath: path,
                logger: null,
                localInfo: () => new LocalSettingsInfo(
                    "vulkan", "parakeet_v3 + gemma4_e2b", "Ready", null),
                recordingStore: null,
                onModelPairSaved: null,
                modelSelection: host);

            Assert.True(window.HasModelSelection);
            Assert.Single(window.AsrChoiceLabels);
            Assert.Contains("Parakeet", window.AsrChoiceLabels[0]);
            window.Close();
        });

        Assert.Null(failure);
    }

    private sealed class FakeSelectionHost : IModelSelectionHost
    {
        public IReadOnlyList<ModelArtifact> AsrCandidates { get; } =
            [Artifact("parakeet_v3", "Parakeet TDT 0.6B v3", "transcribe-vulkan")];
        public IReadOnlyList<ModelArtifact> CleanerCandidates { get; } =
            [Artifact("gemma4_e2b", "Gemma 4 E2B", "llama-vulkan")];
        public ModelSelection Current { get; } = new(2, "parakeet_v3", "gemma4_e2b", true);
        public bool IsAvailable(ModelArtifact artifact) => true;
        public Task ApplyAsync(ModelSelection selection, IProgress<ModelProvisionProgress>? progress,
            CancellationToken ct) => Task.CompletedTask;

        private static ModelArtifact Artifact(string id, string displayName, string runtime) => new(
            id, "fake", "owner/model", "0123456789abcdef0123456789abcdef01234567",
            [new ModelFile($"{id}.gguf", new string('a', 64))],
            "F16", "Apache-2.0", runtime, DisplayName: displayName);
    }

    private static Exception? RunOnStaThread(Action action)
    {
        Exception? captured = null;
        Thread thread = new(() =>
        {
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return captured;
    }
}
