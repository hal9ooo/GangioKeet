using GangioKeet.Client.Configuration;
using GangioKeet.Client.Hotkey;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GangioKeet.Client.Tests.Hotkey;

public sealed class GesturePumpTests
{
    [Fact]
    public void CapsLockShortTap_DispatchesAbortOnlyAfterTheGap()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig { Key = "capslock" }, time);
        List<GestureKind> gestures = [];
        using var pump = new GesturePump(detector, gestures.Add, time);

        pump.OnRaw(new RawKeyEvent(0x14, true, false, 0));
        pump.OnRaw(new RawKeyEvent(0x14, false, false, 0));
        Assert.Equal([GestureKind.Start], gestures);

        time.Advance(TimeSpan.FromMilliseconds(349));
        Assert.Equal([GestureKind.Start], gestures);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal([GestureKind.Start, GestureKind.Abort], gestures);
    }

    [Fact]
    public void Hold_StopsOnRelease_WithoutTimerInterference()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig { Key = "capslock" }, time);
        List<GestureKind> gestures = [];
        using var pump = new GesturePump(detector, gestures.Add, time);

        pump.OnRaw(new RawKeyEvent(0x14, true, false, 0));
        time.Advance(TimeSpan.FromMilliseconds(300));
        pump.OnRaw(new RawKeyEvent(0x14, false, false, 0));

        Assert.Equal([GestureKind.Start, GestureKind.StopAndTranscribe], gestures);
    }

    [Fact]
    public void DoubleTap_EntersHandsFree_AndStopsTheTimer()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig { Key = "capslock" }, time);
        List<GestureKind> gestures = [];
        using var pump = new GesturePump(detector, gestures.Add, time);

        pump.OnRaw(new RawKeyEvent(0x14, true, false, 0));
        time.Advance(TimeSpan.FromMilliseconds(100));
        pump.OnRaw(new RawKeyEvent(0x14, false, false, 0));
        time.Advance(TimeSpan.FromMilliseconds(100));
        pump.OnRaw(new RawKeyEvent(0x14, true, false, 0));

        Assert.Equal([GestureKind.Start, GestureKind.HandsFreeEntered], gestures);

        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal([GestureKind.Start, GestureKind.HandsFreeEntered], gestures);
    }

    [Fact]
    public void Reset_DiscardsPendingTapAndStopsTheTimer()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig { Key = "capslock" }, time);
        List<GestureKind> gestures = [];
        using var pump = new GesturePump(detector, gestures.Add, time);

        pump.OnRaw(new RawKeyEvent(0x14, true, false, 0));
        pump.OnRaw(new RawKeyEvent(0x14, false, false, 0));
        pump.Reset();

        time.Advance(TimeSpan.FromMilliseconds(500));
        Assert.Equal([GestureKind.Start], gestures);

        pump.OnRaw(new RawKeyEvent(0x14, true, false, 0));
        Assert.Equal([GestureKind.Start, GestureKind.Start], gestures);
    }

    [Fact]
    public async Task Dispose_DoesNotWaitForASlowDispatch()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig { Key = "f8" }, time);
        using ManualResetEventSlim entered = new(false);
        using ManualResetEventSlim release = new(false);

        var pump = new GesturePump(
            detector,
            _ =>
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
            },
            time);

        Task dispatch = Task.Run(() => pump.OnRaw(new RawKeyEvent(0x70, true, false, 0)));
        Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(2))));

        Task dispose = Task.Run(pump.Dispose);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));

        release.Set();
        await dispatch.WaitAsync(TimeSpan.FromSeconds(2));
    }
}
