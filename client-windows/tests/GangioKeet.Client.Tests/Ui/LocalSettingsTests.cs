using System.Windows;
using System.Windows.Controls;
using GangioKeet.Client.Platform;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Ui;
using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
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
            Assert.Equal("Pronto", window.LocalState);
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
            Assert.True(window.HasRetryAction);
            Assert.Single(window.AsrChoiceLabels);
            Assert.Contains("Parakeet", window.AsrChoiceLabels[0]);
            Assert.Equal("Pulizia leggera", window.CleanerChoiceLabels[0]);
            Assert.Contains("Gemma 4 E2B", window.CleanerChoiceLabels);
            window.Close();
        });

        Assert.Null(failure);
    }

    [Fact]
    public void RetryButtonInvokesTheHost()
    {
        Exception? failure = RunOnStaThread(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"gangio-settings-{Guid.NewGuid():N}.yaml");
            FakeSelectionHost host = new();
            SettingsWindow window = new(
                telemetry: new TelemetryStore(),
                configPath: path,
                logger: null,
                localInfo: () => new LocalSettingsInfo("vulkan", "non disponibile", "Error", "missing"),
                recordingStore: null,
                onModelPairSaved: null,
                modelSelection: host);

            Button? retry = window.FindName("RetryModelsButton") as Button;
            Assert.NotNull(retry);
            for (int attempt = 0; attempt < 100 && window.Status != "Combinazione applicata a caldo."; attempt++)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                    System.Windows.Threading.DispatcherPriority.Background);
                Thread.Sleep(10);
            }
            retry!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int attempt = 0; attempt < 100 && host.RetryCalls == 0; attempt++)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                    System.Windows.Threading.DispatcherPriority.Background);
                Thread.Sleep(10);
            }
            Assert.Equal(1, host.RetryCalls);
            for (int attempt = 0; attempt < 100 && window.Status != "Riprova completata."; attempt++)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { },
                    System.Windows.Threading.DispatcherPriority.Background);
                Thread.Sleep(10);
            }
            Assert.Equal("Riprova completata.", window.Status);
            window.Close();
        });

        Assert.Null(failure);
    }

    private sealed class FakeSelectionHost : IModelSelectionHost
    {
        public IReadOnlyList<ModelArtifact> AsrCandidates { get; } =
            [Artifact("parakeet_v3", "Parakeet TDT 0.6B v3", "transcribe-vulkan")];
        public IReadOnlyList<ModelArtifact> CleanerCandidates { get; } =
            [LightTextCleaner.Artifact, Artifact("gemma4_e2b", "Gemma 4 E2B", "llama-vulkan")];
        public ModelSelection Current { get; } = new(2, "parakeet_v3", "gemma4_e2b", true);
        public bool IsAvailable(ModelArtifact artifact) => true;
        public int RetryCalls { get; private set; }
        public Task ApplyAsync(ModelSelection selection, IProgress<ModelProvisionProgress>? progress,
            CancellationToken ct) => Task.CompletedTask;
        public Task RetryAsync(IProgress<ModelProvisionProgress>? progress, CancellationToken ct)
        {
            RetryCalls++;
            progress?.Report(new ModelProvisionProgress("parakeet_v3", "parakeet.gguf",
                ModelProvisionStatus.Downloading, 50, 100));
            return Task.CompletedTask;
        }

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
            // Come una finestra WPF: le continuazioni dopo un await devono
            // tornare sul thread che possiede gli oggetti dell'interfaccia.
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(
                    System.Windows.Threading.Dispatcher.CurrentDispatcher));
            try { action(); }
            catch (Exception ex) { captured = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return captured;
    }
}
