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
            Assert.InRange((bar.Top + bar.Bottom) / 2, 9.99, 10.01);
        });
    }

    [Fact]
    public void SilenceBarsStayThinAndCentered()
    {
        IReadOnlyList<Rect> bars = WaveformView.BuildBars(
            new byte[WaveformHistory.BarCount], 100, 20);

        Assert.All(bars, bar =>
        {
            Assert.InRange(bar.Height, 1.5, 2.1);
            Assert.InRange((bar.Top + bar.Bottom) / 2, 9.99, 10.01);
        });
    }

    [Fact]
    public void IdleLineIsFlatAndCentered()
    {
        Rect line = WaveformView.BuildIdleLine(100, 20);

        Assert.Equal(0, line.Left);
        Assert.Equal(100, line.Width);
        Assert.InRange(line.Height, 4.0, 7.0);
        Assert.InRange((line.Top + line.Bottom) / 2, 9.99, 10.01);
    }

    [Fact]
    public void IdleDotsMoveAndPulse()
    {
        IReadOnlyList<Rect> start = WaveformView.BuildIdleDots(100, 20, phase: 0);
        IReadOnlyList<Rect> later = WaveformView.BuildIdleDots(100, 20, phase: 1.1);

        Assert.Equal(3, start.Count);
        Assert.All(start, dot =>
        {
            Assert.InRange(dot.Width, 2.39, 6.41);
            Assert.InRange((dot.Top + dot.Bottom) / 2, 9.99, 10.01);
        });
        Assert.True(start.Zip(later)
            .Count(pair => Math.Abs(pair.First.Left - pair.Second.Left) > 1.0) >= 2);
        Assert.True(start.Zip(later)
            .Count(pair => Math.Abs(pair.First.Width - pair.Second.Width) > 0.2) >= 2);
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
