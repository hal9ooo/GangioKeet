using GangioKeet.Client.Audio;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class MicrophoneMenuPolicyTests
{
    [Fact]
    public void Default_IsSelectedWhenNoDeviceIsConfigured()
    {
        Assert.True(MicrophoneMenuPolicy.IsDefaultSelected(null));
        Assert.True(MicrophoneMenuPolicy.IsDefaultSelected(""));
        Assert.False(MicrophoneMenuPolicy.IsDefaultSelected("mic-1"));
    }

    [Fact]
    public void Device_IsSelectedOnlyByExactId()
    {
        Assert.True(MicrophoneMenuPolicy.IsDeviceSelected("mic-1", "mic-1"));
        Assert.False(MicrophoneMenuPolicy.IsDeviceSelected("mic-1", "mic-2"));
        Assert.False(MicrophoneMenuPolicy.IsDeviceSelected(null, "mic-1"));
    }

    [Fact]
    public void Order_SortsByName_IgnoringCase()
    {
        AudioDeviceInfo[] devices =
        [
            new("2", "zeta", false),
            new("1", "Alfa", true),
        ];

        Assert.Equal(["1", "2"], MicrophoneMenuPolicy.Order(devices).Select(d => d.Id));
    }

    [Fact]
    public void DefaultPath_EndsWithTheClientConfigFile()
    {
        Assert.EndsWith(Path.Combine("GangioKeet", "client_config.yaml"), ConfigLoader.DefaultPath);
    }
}
