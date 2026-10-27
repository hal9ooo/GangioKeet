using GangioKeet.Eval;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class LatencySummaryTests
{
    [Theory]
    [InlineData(10, "0-10")]
    [InlineData(10.001, "10-30")]
    [InlineData(30, "10-30")]
    [InlineData(30.001, "30-60")]
    [InlineData(60, "30-60")]
    [InlineData(60.001, "over-60")]
    public void BucketIncludesTheUpperBoundary(double seconds, string expected)
    {
        Assert.Equal(expected, LatencySummary.Bucket(seconds));
    }

    [Fact]
    public void SummaryReportsMedianP95MaximumAndOverFourSeconds()
    {
        LatencySummary summary = LatencySummary.From([100, 200, 300, 4000, 5000]);

        Assert.Equal(300, summary.P50Ms);
        Assert.Equal(5000, summary.P95Ms);
        Assert.Equal(5000, summary.MaxMs);
        Assert.Equal(1, summary.Over4000);
        Assert.Equal(0.2, summary.FractionOver4000);
    }

    [Fact]
    public void EmptyInputHasNoInventedPercentiles()
    {
        LatencySummary summary = LatencySummary.From([]);

        Assert.Equal(0, summary.Count);
        Assert.Null(summary.P50Ms);
        Assert.Null(summary.P95Ms);
        Assert.Null(summary.FractionOver4000);
    }
}
