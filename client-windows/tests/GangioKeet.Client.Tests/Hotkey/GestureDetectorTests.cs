using GangioKeet.Client.Configuration;
using GangioKeet.Client.Hotkey;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GangioKeet.Client.Tests.Hotkey;

public sealed class GestureDetectorTests
{
    [Fact]
    public void Held250Ms_StopsOnRelease()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig(), time);

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
        time.Advance(TimeSpan.FromMilliseconds(250));

        Assert.Equal([GestureKind.StopAndTranscribe], detector.OnKeyUp());
        Assert.False(detector.AwaitingSecondTap);
        Assert.Empty(detector.OnKeyUp());
    }

    [Fact]
    public void Held249Ms_WaitsForSecondTap()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig(), time);

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
        time.Advance(TimeSpan.FromMilliseconds(249));

        Assert.Empty(detector.OnKeyUp());
        Assert.True(detector.AwaitingSecondTap);

        time.Advance(TimeSpan.FromMilliseconds(350));
        Assert.Equal([GestureKind.StopAndTranscribe], detector.OnTick());
    }

    [Fact]
    public void ShortTap_Waits350MsThenStops()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig(), time);

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Empty(detector.OnKeyUp());
        Assert.True(detector.AwaitingSecondTap);

        time.Advance(TimeSpan.FromMilliseconds(349));
        Assert.Empty(detector.OnTick());
        Assert.True(detector.AwaitingSecondTap);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal([GestureKind.StopAndTranscribe], detector.OnTick());
        Assert.False(detector.AwaitingSecondTap);
    }

    [Fact]
    public void SecondDownWithinGap_EntersHandsFree()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig(), time);

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Empty(detector.OnKeyUp());
        time.Advance(TimeSpan.FromMilliseconds(200));

        Assert.Equal([GestureKind.HandsFreeEntered], detector.OnKeyDown());
        Assert.False(detector.AwaitingSecondTap);

        time.Advance(TimeSpan.FromMilliseconds(5000));
        Assert.Empty(detector.OnTick());
        Assert.Empty(detector.OnKeyUp());
    }

    [Fact]
    public void HandsFreeNextDown_StopsAndFollowingUpDoesNothing()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig(), time);

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Empty(detector.OnKeyUp());
        time.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal([GestureKind.HandsFreeEntered], detector.OnKeyDown());
        Assert.Empty(detector.OnKeyUp());

        time.Advance(TimeSpan.FromMilliseconds(1000));
        Assert.Equal([GestureKind.StopAndTranscribe], detector.OnKeyDown());
        Assert.Empty(detector.OnKeyUp());

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
    }

    [Fact]
    public void RepeatedDownWhileHeld_IsIgnored()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig(), time);

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
        Assert.Empty(detector.OnKeyDown());
        time.Advance(TimeSpan.FromMilliseconds(1000));
        Assert.Empty(detector.OnKeyDown());

        Assert.Equal([GestureKind.StopAndTranscribe], detector.OnKeyUp());
    }

    [Fact]
    public void GapSecondDown349Ms_EntersHandsFree()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig(), time);

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Empty(detector.OnKeyUp());
        time.Advance(TimeSpan.FromMilliseconds(349));

        Assert.Empty(detector.OnTick());
        Assert.Equal([GestureKind.HandsFreeEntered], detector.OnKeyDown());
    }

    [Fact]
    public void GapExpired350Ms_StoppedByTickAndSecondDownStartsOver()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig(), time);

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
        time.Advance(TimeSpan.FromMilliseconds(100));
        Assert.Empty(detector.OnKeyUp());
        time.Advance(TimeSpan.FromMilliseconds(350));

        Assert.Equal([GestureKind.StopAndTranscribe], detector.OnTick());
        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
    }

    [Fact]
    public void Toggle_FirstDownStarts_KeyUpDoesNothing_SecondDownStops()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig { Mode = "toggle" }, time);

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
        time.Advance(TimeSpan.FromMilliseconds(1000));
        Assert.Empty(detector.OnKeyUp());
        Assert.False(detector.AwaitingSecondTap);

        time.Advance(TimeSpan.FromMilliseconds(1000));
        Assert.Equal([GestureKind.StopAndTranscribe], detector.OnKeyDown());
        Assert.Empty(detector.OnKeyUp());

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
    }

    [Fact]
    public void Toggle_IgnoresDoubleTapSettings()
    {
        var config = new HotkeyConfig
        {
            Mode = "toggle",
            DoubleTapMaxMs = 1,
            DoubleTapGapMs = 1,
        };
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(config, time);

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
        time.Advance(TimeSpan.FromMilliseconds(5));
        Assert.Empty(detector.OnKeyUp());
        Assert.False(detector.AwaitingSecondTap);

        time.Advance(TimeSpan.FromMilliseconds(5));
        Assert.Equal([GestureKind.StopAndTranscribe], detector.OnKeyDown());
        Assert.Empty(detector.OnTick());
        Assert.Empty(detector.OnKeyUp());
    }

    [Fact]
    public void Reset_DiscardsPendingTimer()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig(), time);

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
        time.Advance(TimeSpan.FromMilliseconds(50));
        Assert.Empty(detector.OnKeyUp());
        Assert.True(detector.AwaitingSecondTap);

        detector.Reset();
        Assert.False(detector.AwaitingSecondTap);
        time.Advance(TimeSpan.FromMilliseconds(10_000));

        Assert.Empty(detector.OnTick());
        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
    }

    [Fact]
    public void Reset_DuringHold_SwallowsLaterKeyUp()
    {
        var time = new FakeTimeProvider();
        var detector = new GestureDetector(new HotkeyConfig(), time);

        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
        detector.Reset();
        time.Advance(TimeSpan.FromMilliseconds(10_000));

        Assert.Empty(detector.OnKeyUp());
        Assert.Equal([GestureKind.Start], detector.OnKeyDown());
    }
}
