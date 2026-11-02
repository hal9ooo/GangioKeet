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
    public void IdleWaveStaysVisibleAndMoves()
    {
        IReadOnlyList<Rect> start = WaveformView.BuildBars([], 100, 20, idle: 1, idlePhase: 0);
        IReadOnlyList<Rect> later = WaveformView.BuildBars([], 100, 20, idle: 1, idlePhase: 1.2);

        Assert.All(start, bar =>
        {
            Assert.InRange(bar.Height, 3.0, 8.5);
            Assert.InRange((bar.Top + bar.Bottom) / 2, 9.99, 10.01);
        });
        Assert.True(start.Zip(later)
            .Count(pair => Math.Abs(pair.First.Height - pair.Second.Height) > 0.5) >= 5);
    }

    [Fact]
    public void IdleBlendKeepsBarsBetweenLiveAndIdleHeights()
    {
        IReadOnlyList<Rect> live = WaveformView.BuildBars(new byte[WaveformHistory.BarCount], 100, 20);
        IReadOnlyList<Rect> half = WaveformView.BuildBars(
            new byte[WaveformHistory.BarCount], 100, 20, idle: 0.5, idlePhase: 0.7);
        IReadOnlyList<Rect> idle = WaveformView.BuildBars([], 100, 20, idle: 1, idlePhase: 0.7);

        for (int i = 0; i < live.Count; i++)
        {
            Assert.InRange(half[i].Height,
                Math.Min(live[i].Height, idle[i].Height) - 0.01,
                Math.Max(live[i].Height, idle[i].Height) + 0.01);
        }
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
