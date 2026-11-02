using System.Windows;
using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class WaveformViewTests
{
    [Fact]
    public void LoudBarsExtendAboveAndBelowTheCenterline()
    {
        IReadOnlyList<Rect> bars = WaveformView.BuildBars(
            Enumerable.Repeat((byte)3, WaveformHistory.BarCount).ToArray(), 100, 20);

        Assert.NotEmpty(bars);
        Assert.All(bars, bar =>
        {
            Assert.True(bar.Top < 10);
            Assert.True(bar.Bottom > 10);
            Assert.True(bar.Height > 10);
            Assert.InRange((bar.Top + bar.Bottom) / 2, 9.99, 10.01);
        });
    }

    [Fact]
    public void SilentBarsRestOnTheLine()
    {
        IReadOnlyList<Rect> bars = WaveformView.BuildBars(
            new byte[WaveformHistory.BarCount], 100, 24);

        Assert.All(bars, bar =>
        {
            Assert.InRange(bar.Height, 3.1, 3.3);
            Assert.InRange((bar.Top + bar.Bottom) / 2, 11.99, 12.01);
        });
    }

    [Fact]
    public void IdleLineIsFlatAndCentered()
    {
        Rect line = WaveformView.BuildIdleLine(100, 24);

        Assert.Equal(0, line.Left);
        Assert.Equal(100, line.Width);
        Assert.InRange(line.Height, 3.1, 3.3);
        Assert.InRange((line.Top + line.Bottom) / 2, 11.99, 12.01);
    }

    [Fact]
    public void IdleDotsAreSubtleAndTravelRightToLeft()
    {
        IReadOnlyList<Rect> start = WaveformView.BuildIdleDots(100, 24, travelSlots: 0);
        IReadOnlyList<Rect> later = WaveformView.BuildIdleDots(100, 24, travelSlots: 8);

        Assert.Equal(3, start.Count);
        Assert.All(start, dot =>
        {
            Assert.InRange(dot.Width, 1.39, 3.21);
            Assert.InRange((dot.Top + dot.Bottom) / 2, 11.99, 12.01);
        });
        Assert.All(start.Zip(later), pair =>
            Assert.True(pair.Second.Left < pair.First.Left));
        Assert.True(start.Zip(later)
            .Count(pair => Math.Abs(pair.First.Width - pair.Second.Width) > 0.2) >= 2);
    }

    [Fact]
    public void IdleDotsAdvanceOneSlotPerHistorySample()
    {
        IReadOnlyList<Rect> start = WaveformView.BuildIdleDots(100, 24, travelSlots: 0);
        IReadOnlyList<Rect> next = WaveformView.BuildIdleDots(100, 24, travelSlots: 1);
        double slot = 100.0 / WaveformHistory.BarCount;

        for (int i = 0; i < start.Count; i++)
        {
            double startX = start[i].Left + start[i].Width / 2;
            double nextX = next[i].Left + next[i].Width / 2;
            Assert.InRange(startX - nextX, slot - 0.01, slot + 0.01);
        }
    }

    [Fact]
    public void IdleDotsWrapAfterOneHistoryCycle()
    {
        IReadOnlyList<Rect> start = WaveformView.BuildIdleDots(100, 24, travelSlots: 0);
        IReadOnlyList<Rect> wrapped = WaveformView.BuildIdleDots(
            100, 24, travelSlots: WaveformHistory.BarCount);

        for (int i = 0; i < start.Count; i++)
        {
            double startX = start[i].Left + start[i].Width / 2;
            double wrappedX = wrapped[i].Left + wrapped[i].Width / 2;
            Assert.InRange(wrappedX, startX - 0.01, startX + 0.01);
        }
    }

    [Fact]
    public void LoudBarsStayBelowThePillEdges()
    {
        IReadOnlyList<Rect> bars = WaveformView.BuildBars(
            Enumerable.Repeat((byte)3, WaveformHistory.BarCount).ToArray(), 100, 24);

        Assert.All(bars, bar =>
        {
            Assert.InRange(bar.Height, 15.0, 18.01);
            Assert.True(bar.Top >= 3);
            Assert.True(bar.Bottom <= 21);
        });
    }

    [Fact]
    public void BarsStayInsideTheViewport()
    {
        byte[] levels = [0, 1, 2, 3, 3, 2, 1, 0, 3, 1, 2, 0, 3, 2, 1, 0,
            2, 3, 1, 0, 1, 2, 3, 2, 1, 0, 3, 2, 1, 0, 2, 3];
        IReadOnlyList<Rect> bars = WaveformView.BuildBars(levels, 100, 20);

        Assert.All(bars, bar =>
        {
            Assert.True(bar.Left >= 0);
            Assert.True(bar.Right <= 100);
            Assert.True(bar.Top >= 0);
            Assert.True(bar.Bottom <= 20);
        });
    }
}
