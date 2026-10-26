using GangioKeet.Client.Platform;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

[Trait("Category", "Integration")]
public sealed class AutostartTests
{
    [SkippableFact]
    public void EnableDisable_RoundTripsInCurrentUserRun()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RUN_DESKTOP_TESTS") == "1", "RUN_DESKTOP_TESTS != 1");

        AutostartService service = new(Path.Combine(Path.GetTempPath(), "GangioKeet Test", "GangioKeet.Client.exe"));
        bool previous = service.IsEnabled();

        try
        {
            service.SetEnabled(true);
            Assert.True(service.IsEnabled());

            service.SetEnabled(false);
            Assert.False(service.IsEnabled());
        }
        finally
        {
            service.SetEnabled(previous);
        }
    }
}
