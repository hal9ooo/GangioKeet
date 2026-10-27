using GangioKeet.Client.Configuration;
using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class EndpointFormatPolicyTests
{
    [Theory]
    [InlineData("192.168.1.167", true)]
    [InlineData("mac.local", true)]
    [InlineData("fd7a:115c::1", true)]
    [InlineData("192.168.1.167:8000", false)]
    [InlineData("http://mac.local", false)]
    [InlineData("casa#1", false)]
    [InlineData("", false)]
    public void Host_Validation(string host, bool expected)
        => Assert.Equal(expected, EndpointFormatPolicy.IsValidHost(host));

    [Theory]
    [InlineData("Casa", true)]
    [InlineData("Mobilità", true)]
    [InlineData("  Casa  ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a\tb", false)]
    public void Name_Validation(string name, bool expected)
        => Assert.Equal(expected, EndpointFormatPolicy.IsValidName(name));

    [Fact]
    public void Latency_FormatsMillisecondsOrDash()
    {
        Assert.Equal("8 ms", EndpointFormatPolicy.Latency(8.4));
        Assert.Equal("120 ms", EndpointFormatPolicy.Latency(120));
        Assert.Equal("—", EndpointFormatPolicy.Latency(null));
    }

    [Fact]
    public void Ready_FormatsTheTriState()
    {
        Assert.Equal("—", EndpointFormatPolicy.Ready(null, null, null));
        Assert.Equal("ready", EndpointFormatPolicy.Ready(true, false, false));
        Assert.Equal("busy", EndpointFormatPolicy.Ready(false, true, false));
        Assert.Equal("draining", EndpointFormatPolicy.Ready(false, false, true));
        Assert.Equal("non pronto", EndpointFormatPolicy.Ready(false, false, false));
    }

    [Fact]
    public void Status_TellsUnmeasuredFromOffline()
    {
        Assert.Equal("mai verificato", EndpointFormatPolicy.Status(false, measured: false));
        Assert.Equal("online", EndpointFormatPolicy.Status(true, measured: true));
        Assert.Equal("offline", EndpointFormatPolicy.Status(false, measured: true));
    }

    [Fact]
    public void DuplicateHostPort_IsDetected()
    {
        EndpointConfig a = new() { Name = "A", Role = "primary", Host = "10.0.0.1", Port = 8000 };
        EndpointConfig b = new() { Name = "B", Role = "secondary", Host = "10.0.0.1", Port = 8000 };

        Assert.True(EndpointFormatPolicy.HasDuplicate([a, b]));
        Assert.False(EndpointFormatPolicy.HasDuplicate([a]));
    }

    [Theory]
    [InlineData("8000", 8000)]
    [InlineData("1", 1)]
    [InlineData("65535", 65535)]
    [InlineData("0", null)]
    [InlineData("65536", null)]
    [InlineData("", null)]
    [InlineData("abc", null)]
    public void Port_Parsing(string text, int? expected)
        => Assert.Equal(expected, EndpointFormatPolicy.ParsePort(text));

    [Fact]
    public void SetPrimary_DemotesThePreviousOne()
    {
        EndpointConfig a = new() { Name = "A", Role = "primary", Host = "10.0.0.1" };
        EndpointConfig b = new() { Name = "B", Role = "secondary", Host = "10.0.0.2" };

        IReadOnlyList<EndpointConfig> result = EndpointFormatPolicy.SetPrimary([a, b], 1);

        Assert.Equal("secondary", result[0].Role);
        Assert.Equal("primary", result[1].Role);
    }
}
