using GangioKeet.Client.Configuration;
using GangioKeet.Client.Protocol;
using Xunit;

namespace GangioKeet.Client.Tests.Protocol;

public sealed class EndpointSelectorTests
{
    private static EndpointConfig Endpoint(string name, string host = "10.0.0.1", string role = "secondary")
        => new() { Name = name, Role = role, Host = host, Port = 8000 };

    private static ProbeResult Ok(double latencyMs)
        => new(true, latencyMs, true, false, false, null);

    private static ProbeResult Fail(string code = "timeout")
        => new(false, null, null, null, null, code);

    private static EndpointSelector Selector(int margin, params EndpointConfig[] endpoints)
        => new(endpoints, margin);

    [Fact]
    public void LowestMedian_Wins()
    {
        EndpointConfig a = Endpoint("a", "10.0.0.1", "primary");
        EndpointConfig b = Endpoint("b", "10.0.0.2");
        EndpointSelector selector = Selector(25, a, b);

        selector.Apply(a, Ok(40));
        selector.Apply(b, Ok(10));

        Assert.Equal("b", selector.Active.Name);
    }

    [Fact]
    public void Hysteresis_KeepsActiveWithinMargin()
    {
        EndpointConfig a = Endpoint("a", "10.0.0.1", "primary");
        EndpointConfig b = Endpoint("b", "10.0.0.2");
        EndpointSelector selector = Selector(25, a, b);

        selector.Apply(a, Ok(40));
        selector.Apply(b, Ok(35));

        Assert.Equal("a", selector.Active.Name);
    }

    [Fact]
    public void ZeroMargin_AlwaysSwitchesToLower()
    {
        EndpointConfig a = Endpoint("a", "10.0.0.1", "primary");
        EndpointConfig b = Endpoint("b", "10.0.0.2");
        EndpointSelector selector = Selector(0, a, b);

        selector.Apply(a, Ok(40));
        selector.Apply(b, Ok(39));

        Assert.Equal("b", selector.Active.Name);
    }

    [Fact]
    public void UnreachableActive_ImmediatelyYieldsToReachable()
    {
        EndpointConfig a = Endpoint("a", "10.0.0.1", "primary");
        EndpointConfig b = Endpoint("b", "10.0.0.2");
        EndpointSelector selector = Selector(25, a, b);

        selector.Apply(a, Ok(10));
        selector.Apply(b, Ok(400));
        selector.Apply(a, Fail());

        Assert.Equal("b", selector.Active.Name);
    }

    [Fact]
    public void AllUnreachable_KeepsTheLastActive()
    {
        EndpointConfig a = Endpoint("a", "10.0.0.1", "primary");
        EndpointConfig b = Endpoint("b", "10.0.0.2");
        EndpointSelector selector = Selector(25, a, b);

        selector.Apply(a, Ok(10));
        selector.Apply(b, Fail());
        selector.Apply(a, Fail());

        Assert.Equal(a.Key, selector.Active.Key);
    }

    [Fact]
    public void TieWithinOneMillisecond_PrefersPrimary()
    {
        EndpointConfig primary = Endpoint("a", "10.0.0.1", "primary");
        EndpointConfig secondary = Endpoint("b", "10.0.0.2");
        EndpointSelector selector = Selector(0, primary, secondary);

        selector.Apply(primary, Ok(10.0));
        selector.Apply(secondary, Ok(9.6));

        Assert.Equal("a", selector.Active.Name);
    }

    [Fact]
    public void Rename_KeepsMedianForTheSameDestination()
    {
        EndpointConfig a = Endpoint("a", "10.0.0.1", "primary");
        EndpointSelector selector = Selector(25, a);

        selector.Apply(a, Ok(40));
        EndpointConfig renamed = Endpoint("casa", "10.0.0.1", "primary");
        selector.Reset([renamed]);

        EndpointStats stats = selector.StatsFor(renamed);
        Assert.True(stats.Measured);
        Assert.Equal(40d, stats.MedianLatencyMs);
        Assert.Equal("casa", selector.Active.Name);
    }

    [Fact]
    public void HostChange_ResetsMeasurements()
    {
        EndpointConfig a = Endpoint("a", "10.0.0.1", "primary");
        EndpointSelector selector = Selector(25, a);

        selector.Apply(a, Ok(40));
        EndpointConfig moved = Endpoint("a", "10.0.0.99", "primary");
        selector.Reset([moved]);

        Assert.False(selector.StatsFor(moved).Measured);
        Assert.Equal(moved.Key, selector.Active.Key);
    }

    [Fact]
    public void Active_IsSeededWithPrimary_EvenIfItIsNotFirst()
    {
        EndpointConfig secondary = Endpoint("b", "10.0.0.2");
        EndpointConfig primary = Endpoint("a", "10.0.0.1", "primary");

        EndpointSelector selector = Selector(25, secondary, primary);

        Assert.Equal("a", selector.Active.Name);
    }

    [Fact]
    public void Apply_RaisesChanged()
    {
        EndpointConfig a = Endpoint("a", "10.0.0.1", "primary");
        EndpointSelector selector = Selector(25, a);
        int changes = 0;
        selector.Changed += () => changes++;

        selector.Apply(a, Ok(10));

        Assert.Equal(1, changes);
    }
}
