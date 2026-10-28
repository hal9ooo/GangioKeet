using System.IO;
using System.Threading;
using GangioKeet.Client.Platform;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class SettingsWindowModeSwitchTests
{
    [Fact]
    public void ApplyModeView_TogglesPanelVisibilityLive()
    {
        Exception? failure = RunOnStaThread(() =>
        {
            string path = Path.Combine(Path.GetTempPath(), $"gangio-modeswitch-{Guid.NewGuid():N}.yaml");
            string? savedMode = null;
            SettingsWindow window = new(
                selector: null,
                probe: null,
                telemetry: new TelemetryStore(),
                configPath: path,
                onSaved: _ => { },
                localInfo: () => new LocalSettingsInfo("local", "vulkan", "stt+cleaner", "Ready", null),
                onModeSaved: mode => savedMode = mode,
                recordingStore: null);

            // Initially local mode
            window.ApplyModeView("local");
            Assert.True(window.IsLocalPanelVisible);

            // Switch to remote
            window.ApplyModeView("remote");
            Assert.False(window.IsLocalPanelVisible);

            // Switch back to local
            window.ApplyModeView("local");
            Assert.True(window.IsLocalPanelVisible);

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
