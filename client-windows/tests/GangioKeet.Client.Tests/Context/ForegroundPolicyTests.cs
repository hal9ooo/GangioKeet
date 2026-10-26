using GangioKeet.Client.Context;
using Xunit;

namespace GangioKeet.Client.Tests.Context;

public sealed class ForegroundPolicyTests
{
    private const int TargetProcessId = 42;

    [Fact]
    public void DeadTargetOrReusedHandle_IsRejected()
    {
        var target = new ForegroundWindowInfo(0x1234, TargetProcessId, "notepad.exe", "Senza titolo");

        var dead = NewService(pidForHandle: _ => 0);
        Assert.False(dead.IsAlive(target));

        var reused = NewService(pidForHandle: _ => TargetProcessId + 35);
        Assert.False(reused.IsAlive(target));

        var alive = NewService(pidForHandle: _ => TargetProcessId);
        Assert.True(alive.IsAlive(target));

        var service = NewService(
            pidForHandle: _ => TargetProcessId,
            titleForHandle: _ => "server.py - GangioKeet",
            processNameForPid: _ => "Code.exe",
            foreground: () => 0x4321);

        Assert.True(service.TryActivate(0x9999));
        Assert.Equal(0x4321, service.CaptureHandle());
        Assert.Equal(0x4321, service.GetForegroundHandle());

        ForegroundWindowInfo info = service.Read(0x8765);
        Assert.Equal(0x8765, info.Handle);
        Assert.Equal(TargetProcessId, info.ProcessId);
        Assert.Equal("Code.exe", info.ProcessName);
        Assert.Equal("server.py - GangioKeet", info.WindowTitle);
    }

    [Fact]
    public void ElevationCheckedAgainstCurrentProcess()
    {
        bool? selfElevated = false;
        var checker = new Win32ElevationChecker(
            pid => pid switch
            {
                99 => true,
                100 => false,
                _ => (bool?)null,
            },
            () => selfElevated);

        var elevatedTarget = new ForegroundWindowInfo(0x1234, 99, "devenv.exe", "Visual Studio");

        Assert.True(checker.IsInjectionBlocked(elevatedTarget));

        selfElevated = true;
        Assert.False(checker.IsInjectionBlocked(elevatedTarget));
        selfElevated = false;

        var plainTarget = new ForegroundWindowInfo(0x5678, 100, "notepad.exe", "Senza titolo");
        Assert.False(checker.IsInjectionBlocked(plainTarget));

        var opaqueTarget = new ForegroundWindowInfo(0x9abc, 7, "mystery.exe", "");
        Assert.True(checker.IsInjectionBlocked(opaqueTarget));

        selfElevated = null;
        Assert.True(checker.IsInjectionBlocked(plainTarget));
    }

    private static Win32ForegroundWindowService NewService(
        Func<nint>? foreground = null,
        Func<nint, string>? titleForHandle = null,
        Func<nint, int>? pidForHandle = null,
        Func<int, string?>? processNameForPid = null,
        Func<nint, bool>? activate = null)
        => new(
            foreground ?? (() => 0x1234),
            titleForHandle ?? (_ => "Senza titolo"),
            pidForHandle ?? (_ => TargetProcessId),
            processNameForPid ?? (_ => "notepad.exe"),
            activate ?? (_ => true));
}
