using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class PillPlacementTests
{
    [Fact]
    public void CenterBottom_CentersHorizontally_AndSitsAboveTheMargin()
    {
        PillWorkArea work = new(0, 0, 1920, 1040);

        (double left, double top) = PillPlacement.CenterBottom(work, 136, 28, 24);

        Assert.Equal((1920 - 136) / 2.0, left);
        Assert.Equal(1040 - 28 - 24, top);
    }

    [Fact]
    public void CenterBottom_ClampsOnNarrowWorkAreas()
    {
        PillWorkArea work = new(0, 0, 100, 100);

        (double left, double top) = PillPlacement.CenterBottom(work, 136, 28, 24);

        Assert.Equal(0, left);
        Assert.Equal(100 - 28 - 24, top);
    }

    [Fact]
    public void CenterBottom_HandlesNegativeOrigins()
    {
        PillWorkArea work = new(-1920, -100, 1920, 1040);

        (double left, double top) = PillPlacement.CenterBottom(work, 136, 28, 24);

        Assert.Equal(-1920 + (1920 - 136) / 2.0, left);
        Assert.Equal(-100 + 1040 - 28 - 24, top);
    }

    [Fact]
    public void NearCursor_ClampsToTheWorkArea()
    {
        PillWorkArea work = new(0, 0, 1920, 1040);

        (double left, double top) = PillPlacement.NearCursor(work, 1919, 1039, 136, 28, 16);

        Assert.Equal(1920 - 136, left);
        Assert.Equal(1040 - 28, top);
    }
}
