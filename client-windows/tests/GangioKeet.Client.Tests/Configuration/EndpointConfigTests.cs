using GangioKeet.Client.Configuration;
using Xunit;

namespace GangioKeet.Client.Tests.Configuration;

public sealed class EndpointConfigTests
{
    private static string WriteTempYaml(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gangiokeet-endpoints-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void LegacyServerHost_IsMigratedToSingleEndpoint()
    {
        string path = WriteTempYaml("""
            server:
              host: "192.168.1.167"
              port: 8000
            """);

        ClientConfig config = ConfigLoader.Load(path);

        EndpointConfig only = Assert.Single(config.Endpoints!);
        Assert.Equal("Server", only.Name);
        Assert.Equal("primary", only.Role);
        Assert.Equal("192.168.1.167", only.Host);
        Assert.Equal("http://192.168.1.167:8000", only.Key);
    }

    [Fact]
    public void EndpointsBlock_IsParsed()
    {
        string path = WriteTempYaml("""
            endpoints:
              - name: "Casa"
                role: "primary"
                host: "192.168.1.167"
                port: 8000
              - name: "Mobilita"
                role: "secondary"
                scheme: "https"
                host: "100.64.0.12"
                port: 8443
            """);

        ClientConfig config = ConfigLoader.Load(path);

        Assert.Collection(
            config.Endpoints!,
            first =>
            {
                Assert.Equal("Casa", first.Name);
                Assert.Equal("primary", first.Role);
                Assert.Equal("http", first.Scheme);
                Assert.Equal("http://192.168.1.167:8000", first.Key);
            },
            second =>
            {
                Assert.Equal("Mobilita", second.Name);
                Assert.Equal("secondary", second.Role);
                Assert.Equal("https", second.Scheme);
                Assert.Equal("https://100.64.0.12:8443", second.Key);
            });
    }

    [Fact]
    public void NewServerKeys_HaveDefaults()
    {
        string path = WriteTempYaml("server:\n  host: \"127.0.0.1\"\n");

        ClientConfig config = ConfigLoader.Load(path);

        Assert.Equal(2.0, config.Server.HealthCheckTimeoutSeconds);
        Assert.Equal(25, config.Server.SwitchMarginPercent);
    }

    [Theory]
    [InlineData("zai-org/primary")]
    [InlineData("http://192.168.1.167")]
    [InlineData("192.168.1.167:8000")]
    [InlineData("casa#1")]
    public void InvalidHost_IsRejected(string host)
    {
        string path = WriteTempYaml($"""
            endpoints:
              - name: "Casa"
                role: "primary"
                host: "{host}"
                port: 8000
            """);

        Assert.Throws<ConfigException>(() => ConfigLoader.Load(path));
    }

    [Fact]
    public void Ipv6Host_IsAccepted_AndBracketedInTheKey()
    {
        string path = WriteTempYaml("""
            endpoints:
              - name: "Casa"
                role: "primary"
                host: "fd7a:115c::1"
                port: 8000
            """);

        ClientConfig config = ConfigLoader.Load(path);

        Assert.Equal("[fd7a:115c::1]", Assert.Single(config.Endpoints!).HostForUri);
        Assert.Equal("http://[fd7a:115c::1]:8000", config.Endpoints![0].Key);
    }

    [Fact]
    public void TwoPrimaries_AreRejected()
    {
        string path = WriteTempYaml("""
            endpoints:
              - name: "A"
                role: "primary"
                host: "192.168.1.1"
              - name: "B"
                role: "primary"
                host: "192.168.1.2"
            """);

        Assert.Throws<ConfigException>(() => ConfigLoader.Load(path));
    }

    [Fact]
    public void DuplicateHostPort_IsRejected()
    {
        string path = WriteTempYaml("""
            endpoints:
              - name: "A"
                role: "primary"
                host: "192.168.1.1"
              - name: "B"
                role: "secondary"
                host: "192.168.1.1"
            """);

        Assert.Throws<ConfigException>(() => ConfigLoader.Load(path));
    }

    [Fact]
    public void EmptyEndpointsBlock_IsRejected()
    {
        string path = WriteTempYaml("endpoints: []\n");

        Assert.Throws<ConfigException>(() => ConfigLoader.Load(path));
    }

    [Fact]
    public void SwitchMarginPercent_OutOfRange_IsRejected()
    {
        string path = WriteTempYaml("server:\n  host: \"127.0.0.1\"\n  switch_margin_percent: 99\n");

        Assert.Throws<ConfigException>(() => ConfigLoader.Load(path));
    }

    [Fact]
    public void UnknownRole_IsRejected()
    {
        string path = WriteTempYaml("""
            endpoints:
              - name: "A"
                role: "backup"
                host: "192.168.1.1"
            """);

        Assert.Throws<ConfigException>(() => ConfigLoader.Load(path));
    }
}
