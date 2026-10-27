using Microsoft.Extensions.Time.Testing;
using GangioKeet.Client.Controller;
using Xunit;

namespace GangioKeet.Client.Tests.Controller;

public sealed class TurnTimingTests
{
    [Fact]
    public void ReleaseToPasteMeasuresFromReleaseNotFromRecordingStart()
    {
        FakeTimeProvider time = new();
        TurnTiming timing = new(time);
        time.Advance(TimeSpan.FromSeconds(60));
        timing.MarkReleased(time.GetTimestamp());
        time.Advance(TimeSpan.FromMilliseconds(3500));

        timing.MarkPasted(time.GetTimestamp());

        Assert.Equal(3500, timing.ReleaseToPasteMs);
        Assert.Null(timing.FailureReason);
    }

    [Fact]
    public void SecondReleaseDoesNotResetTheTimestamp()
    {
        FakeTimeProvider time = new();
        TurnTiming timing = new(time);
        timing.MarkReleased(time.GetTimestamp());
        time.Advance(TimeSpan.FromSeconds(1));
        timing.MarkReleased(time.GetTimestamp());
        time.Advance(TimeSpan.FromSeconds(3));

        timing.MarkPasted(time.GetTimestamp());

        Assert.Equal(4000, timing.ReleaseToPasteMs);
    }

    [Fact]
    public void FailedPasteIsNotReportedAsSuccessfulLatency()
    {
        FakeTimeProvider time = new();
        TurnTiming timing = new(time);
        timing.MarkReleased(time.GetTimestamp());
        time.Advance(TimeSpan.FromSeconds(1));

        timing.MarkPasteFailed("focus_lost");

        Assert.Null(timing.ReleaseToPasteMs);
        Assert.Equal("focus_lost", timing.FailureReason);
    }

    [Fact]
    public void WithoutReleaseAndPasteThereIsNoLatency()
    {
        TurnTiming timing = new(TimeProvider.System);
        Assert.Null(timing.ReleaseToPasteMs);
        Assert.Equal("not_released", timing.FailureReason);
    }
}
