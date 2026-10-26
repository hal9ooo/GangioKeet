using GangioKeet.Client.Configuration;
using Xunit;

namespace GangioKeet.Client.Tests.Configuration;

public sealed class ServerEndpointTests
{
    [Fact]
    public void DefaultScheme_BuildsHttpAndWs()
    {
        var server = new ServerConfig { Host = "127.0.0.1", Port = 8000 };

        Assert.Equal("http", server.Scheme);
        Assert.Equal(
            "http://127.0.0.1:8000/",
            ServerEndpoint.HttpBase(server).AbsoluteUri);
        Assert.Equal(
            "ws://127.0.0.1:8000/ws/dictate",
            ServerEndpoint.WebSocket(server).AbsoluteUri);
    }

    [Fact]
    public void HttpsScheme_BuildsHttpsAndWss()
    {
        var server = new ServerConfig
        {
            Scheme = "https",
            Host = "dictation.example",
            Port = 8443,
        };

        Assert.Equal(
            "https://dictation.example:8443/",
            ServerEndpoint.HttpBase(server).AbsoluteUri);
        Assert.Equal(
            "wss://dictation.example:8443/ws/dictate",
            ServerEndpoint.WebSocket(server).AbsoluteUri);
    }
}
