using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class FrameGateTests
{
    [Fact]
    public void SixtyHertzFramesRenderOncePerFrame()
    {
        FrameGate gate = new(TimeSpan.FromMilliseconds(16));
        Assert.True(gate.ShouldRender(TimeSpan.Zero));
        Assert.False(gate.ShouldRender(TimeSpan.FromMilliseconds(8)));
        Assert.True(gate.ShouldRender(TimeSpan.FromMilliseconds(16.7)));
        Assert.True(gate.ShouldRender(TimeSpan.FromMilliseconds(33.4)));
    }

    [Fact]
    public void HighRefreshRateIsCappedNearSixty()
    {
        FrameGate gate = new(TimeSpan.FromMilliseconds(16));
        int rendered = 0;
        for (int i = 0; i < 144; i++)
        {
            if (gate.ShouldRender(TimeSpan.FromMilliseconds(i * 1000.0 / 144.0)))
                rendered++;
        }

        Assert.InRange(rendered, 55, 65);
    }

    [Fact]
    public void ResetAllowsTheNextFrame()
    {
        FrameGate gate = new(TimeSpan.FromMilliseconds(16));
        Assert.True(gate.ShouldRender(TimeSpan.Zero));
        Assert.False(gate.ShouldRender(TimeSpan.FromMilliseconds(8)));
        gate.Reset();
        Assert.True(gate.ShouldRender(TimeSpan.FromMilliseconds(9)));
    }
}
