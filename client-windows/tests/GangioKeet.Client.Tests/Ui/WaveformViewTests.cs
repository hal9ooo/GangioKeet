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
            Enumerable.Repeat((byte)3, WaveformHistory.BarCount).ToArray(), 100, 34);

        Assert.NotEmpty(bars);
        Assert.All(bars, bar =>
        {
            Assert.True(bar.Top < 17);
            Assert.True(bar.Bottom > 17);
            Assert.True(bar.Height > 10);
            Assert.InRange((bar.Top + bar.Bottom) / 2, 16.99, 17.01);
        });
    }

    [Fact]
    public void SilentBarsRestOnTheLine()
    {
        IReadOnlyList<Rect> bars = WaveformView.BuildBars(
            new byte[WaveformHistory.BarCount], 100, 34);

        Assert.All(bars, bar =>
        {
            Assert.InRange(bar.Height, 1.7, 1.9);
            Assert.InRange((bar.Top + bar.Bottom) / 2, 16.99, 17.01);
        });
    }

    [Fact]
    public void IdleLineIsFlatAndCentered()
    {
        Rect line = WaveformView.BuildIdleLine(100, 34);

        Assert.Equal(0, line.Left);
        Assert.Equal(100, line.Width);
        Assert.InRange(line.Height, 1.7, 1.9);
        Assert.InRange((line.Top + line.Bottom) / 2, 16.99, 17.01);
    }

    [Fact]
    public void IdleDotsAreSubtleAndTravelRightToLeft()
    {
        IReadOnlyList<Rect> start = WaveformView.BuildIdleDots(100, 34, travelSlots: 0);
        IReadOnlyList<Rect> later = WaveformView.BuildIdleDots(100, 34, travelSlots: 8);

        Assert.Equal(3, start.Count);
        Assert.All(start, dot =>
        {
            Assert.InRange(dot.Width, 0.99, 2.41);
            Assert.InRange((dot.Top + dot.Bottom) / 2, 16.99, 17.01);
        });
        Assert.All(start.Zip(later), pair =>
            Assert.True(pair.Second.Left < pair.First.Left));
        Assert.True(start.Zip(later)
            .Count(pair => Math.Abs(pair.First.Width - pair.Second.Width) > 0.2) >= 2);
    }

    [Fact]
    public void IdleDotsAdvanceOneSlotPerHistorySample()
    {
        IReadOnlyList<Rect> start = WaveformView.BuildIdleDots(100, 34, travelSlots: 0);
        IReadOnlyList<Rect> next = WaveformView.BuildIdleDots(100, 34, travelSlots: 1);
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
        IReadOnlyList<Rect> start = WaveformView.BuildIdleDots(100, 34, travelSlots: 0);
        IReadOnlyList<Rect> wrapped = WaveformView.BuildIdleDots(
            100, 34, travelSlots: WaveformHistory.BarCount);

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
            Enumerable.Repeat((byte)3, WaveformHistory.BarCount).ToArray(), 100, 34);

        Assert.All(bars, bar =>
        {
            Assert.InRange(bar.Height, 27.5, 33.01);
            Assert.True(bar.Top >= 0.4);
            Assert.True(bar.Bottom <= 33.6);
        });
    }

    [Fact]
    public void BarsSnapToThePhysicalPixelGridAroundTheCenter()
    {
        Rect snapped = WaveformView.SnapAroundCenter(
            new Rect(1.13, 2.27, 2.19, 3.33), center: 17, 1.25, 1.25);

        Assert.Equal(Math.Round(snapped.Left * 1.25), snapped.Left * 1.25, 6);
        Assert.Equal(Math.Round(snapped.Right * 1.25), snapped.Right * 1.25, 6);
        Assert.Equal(17, (snapped.Top + snapped.Bottom) / 2, 6);
    }

    [Fact]
    public void SnappingKeepsAtLeastOnePhysicalPixel()
    {
        Rect snapped = WaveformView.SnapAroundCenter(
            new Rect(0, 0, 0.1, 0.1), center: 17, 1.25, 1.25);

        Assert.True(snapped.Width * 1.25 >= 1 - 1e-9);
        Assert.True(snapped.Height * 1.25 >= 1 - 1e-9);
    }

    [Fact]
    public void PixelBarsHaveUniformWidthAndPitch()
    {
        const double width = 166;
        const double center = 17;
        IReadOnlyList<Rect> logical = WaveformView.BuildBars(
            Enumerable.Repeat((byte)3, WaveformHistory.BarCount).ToArray(), width, 34);
        IReadOnlyList<Rect> rendered = WaveformView.BuildPixelBars(
            logical, width, center, 1.25, 1.25);

        Assert.All(rendered, bar =>
        {
            Assert.Equal(3 / 1.25, bar.Width, 6);
            Assert.Equal(Math.Round(bar.Left * 1.25), bar.Left * 1.25, 6);
            Assert.Equal(Math.Round(bar.Right * 1.25), bar.Right * 1.25, 6);
            Assert.Equal(center, (bar.Top + bar.Bottom) / 2, 6);
            Assert.Equal(rendered[0].Height, bar.Height, 6);
        });

        double pitch = rendered[1].Left - rendered[0].Left;
        for (int i = 1; i < rendered.Count; i++)
        {
            Assert.Equal(pitch, rendered[i].Left - rendered[i - 1].Left, 6);
        }

        IReadOnlyList<Rect> at100 = WaveformView.BuildPixelBars(
            logical, width, center, 1, 1);
        Assert.All(at100, bar => Assert.Equal(2, bar.Width, 6));
    }

    [Fact]
    public void PixelBarsAlignToWindowPixelsWhenTheViewIsOffset()
    {
        const double width = 166;
        const double originXPx = 0.75;
        IReadOnlyList<Rect> logical = WaveformView.BuildBars(
            Enumerable.Repeat((byte)3, WaveformHistory.BarCount).ToArray(), width, 34);
        IReadOnlyList<Rect> rendered = WaveformView.BuildPixelBars(
            logical, width, center: 17, 1.25, 1.25, originXPx);

        Assert.All(rendered, bar =>
        {
            double absoluteLeft = originXPx + bar.Left * 1.25;
            double absoluteRight = originXPx + bar.Right * 1.25;
            Assert.Equal(Math.Round(absoluteLeft), absoluteLeft, 6);
            Assert.Equal(Math.Round(absoluteRight), absoluteRight, 6);
        });
    }

    [Fact]
    public void BarsStayInsideTheViewport()
    {
        byte[] levels = [0, 1, 2, 3, 3, 2, 1, 0, 3, 1, 2, 0, 3, 2, 1, 0,
            2, 3, 1, 0, 1, 2, 3, 2, 1, 0, 3, 2, 1, 0, 2, 3];
        IReadOnlyList<Rect> bars = WaveformView.BuildBars(levels, 100, 34);

        Assert.All(bars, bar =>
        {
            Assert.True(bar.Left >= 0);
            Assert.True(bar.Right <= 100);
            Assert.True(bar.Top >= 0);
            Assert.True(bar.Bottom <= 34);
        });
    }
}
