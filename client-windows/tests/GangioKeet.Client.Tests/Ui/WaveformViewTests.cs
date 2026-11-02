using System.Windows;
using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class WaveformViewTests
{
    [Fact]
    public void WaveHasTwentyThreeBars()
    {
        IReadOnlyList<Rect> bars = WaveformView.BuildWaveBars(166, 34, time: 0);

        Assert.Equal(23, bars.Count);
    }

    [Fact]
    public void WaveBarsStayCenteredAndInsideTheView()
    {
        IReadOnlyList<Rect> bars = WaveformView.BuildWaveBars(166, 34, time: 0);

        Assert.All(bars, bar =>
        {
            Assert.InRange((bar.Top + bar.Bottom) / 2, 16.99, 17.01);
            Assert.InRange(bar.Height, 2.0, 30.3);
            Assert.True(bar.Left >= 0);
            Assert.True(bar.Right <= 166);
            Assert.True(bar.Top >= 0);
            Assert.True(bar.Bottom <= 34);
        });
    }

    [Fact]
    public void WaveMovesSidewaysOverTime()
    {
        IReadOnlyList<Rect> start = WaveformView.BuildWaveBars(166, 34, time: 0);
        IReadOnlyList<Rect> later = WaveformView.BuildWaveBars(166, 34, time: 0.25);

        Assert.True(start.Zip(later)
            .Count(pair => Math.Abs(pair.First.Height - pair.Second.Height) > 0.5) >= 5);
    }

    [Fact]
    public void WaveNeverCollapses()
    {
        for (double time = 0; time <= 5; time += 0.05)
        {
            IReadOnlyList<Rect> bars = WaveformView.BuildWaveBars(166, 34, time);
            Assert.All(bars, bar => Assert.InRange(bar.Height, 2.0, 30.3));
        }
    }

    [Fact]
    public void PixelBarsHaveUniformWidthAndPitch()
    {
        const double width = 166;
        const double center = 17;
        IReadOnlyList<Rect> logical = WaveformView.BuildWaveBars(width, 34, time: 0.2);
        IReadOnlyList<Rect> rendered = WaveformView.BuildPixelBars(
            logical, width, center, 1.25, 1.25);

        Assert.Equal(23, rendered.Count);
        Assert.All(rendered, bar =>
        {
            Assert.Equal(4 / 1.25, bar.Width, 6);
            Assert.Equal(Math.Round(bar.Left * 1.25), bar.Left * 1.25, 6);
            Assert.Equal(Math.Round(bar.Right * 1.25), bar.Right * 1.25, 6);
            Assert.Equal(center, (bar.Top + bar.Bottom) / 2, 6);
        });

        double pitch = rendered[1].Left - rendered[0].Left;
        for (int i = 1; i < rendered.Count; i++)
        {
            Assert.Equal(pitch, rendered[i].Left - rendered[i - 1].Left, 6);
        }

        IReadOnlyList<Rect> at100 = WaveformView.BuildPixelBars(
            logical, width, center, 1, 1);
        Assert.All(at100, bar => Assert.Equal(3, bar.Width, 6));
    }

    [Fact]
    public void PixelBarsAlignToWindowPixelsWhenTheViewIsOffset()
    {
        const double width = 166;
        const double originXPx = 0.75;
        IReadOnlyList<Rect> logical = WaveformView.BuildWaveBars(width, 34, time: 0.2);
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
}
