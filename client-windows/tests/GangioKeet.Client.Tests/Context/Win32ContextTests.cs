using GangioKeet.Client.Context;
using Xunit;
using Xunit.Abstractions;

namespace GangioKeet.Client.Tests.Context;

[Trait("Category", "Integration")]
public sealed class Win32ContextTests
{
    private readonly ITestOutputHelper _output;

    public Win32ContextTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    public void RealApi_CaptureReadAndLiveness()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RUN_DESKTOP_TESTS") == "1", "RUN_DESKTOP_TESTS != 1");

        var service = new Win32ForegroundWindowService();
        nint handle = service.CaptureHandle();
        Skip.IfNot(handle != 0, "nessuna finestra in primo piano");

        ForegroundWindowInfo info = service.Read(handle);
        Assert.Equal(handle, info.Handle);
        Assert.NotEqual(0, info.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(info.ProcessName));
        Assert.True(service.IsAlive(info));
        Assert.False(service.IsAlive(info with { ProcessId = info.ProcessId + 1 }));
        Assert.True(service.TryActivate(info.Handle));
        Assert.NotEqual(0, service.GetForegroundHandle());

        _output.WriteLine($"ProcessName={info.ProcessName}; Title={info.WindowTitle}");
    }

    [SkippableFact]
    public void RealApi_ElevationCheckIsDeterministic()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RUN_DESKTOP_TESTS") == "1", "RUN_DESKTOP_TESTS != 1");

        var service = new Win32ForegroundWindowService();
        nint handle = service.CaptureHandle();
        Skip.IfNot(handle != 0, "nessuna finestra in primo piano");

        ForegroundWindowInfo info = service.Read(handle);
        var elevation = new Win32ElevationChecker();
        bool firstRead = elevation.IsInjectionBlocked(info);
        Assert.Equal(firstRead, elevation.IsInjectionBlocked(info));
        _output.WriteLine($"ProcessName={info.ProcessName}; ElevationBlocked={firstRead}");
    }
}
