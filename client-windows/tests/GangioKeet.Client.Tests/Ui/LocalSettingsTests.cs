using System.Windows;
using GangioKeet.Client.Platform;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class LocalSettingsTests
{
    [Fact]
    public void LocalWindowOpensWithoutEndpointSelectorOrProbe()
    {
        Exception? failure = RunOnStaThread(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"gangio-settings-{Guid.NewGuid():N}.yaml");
            SettingsWindow window = new(
                selector: null,
                probe: null,
                telemetry: new TelemetryStore(),
                configPath: path,
                onSaved: _ => { },
                localInfo: () => new LocalSettingsInfo("local", "vulkan", "qwen3_asr_1_7b + gemma4_e2b", "Ready", null),
                onModeSaved: _ => { });

            Assert.True(window.UsesLocalServices);
            Assert.True(window.IsLocalPanelVisible);
            Assert.Equal("Ready", window.LocalState);
            Assert.Contains("qwen3_asr_1_7b", window.LocalPair);
            window.Close();
        });

        Assert.Null(failure);
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
