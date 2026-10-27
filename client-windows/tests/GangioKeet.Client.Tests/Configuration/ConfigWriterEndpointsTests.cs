using GangioKeet.Client.Configuration;
using Xunit;

namespace GangioKeet.Client.Tests.Configuration;

public sealed class ConfigWriterEndpointsTests
{
    private static string TempFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gangiokeet-writer-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    private static EndpointConfig Endpoint(string name, string host) => new()
    {
        Name = name,
        Role = "primary",
        Host = host,
        Port = 8000,
    };

    [Fact]
    public void MissingBlock_IsInsertedAfterServerSection()
    {
        string path = TempFile("""
            # commento iniziale
            server:
              host: "127.0.0.1"

            hotkey:
              key: "capslock"
            """);

        Assert.True(ConfigWriter.SetEndpoints(path, [Endpoint("Casa", "192.168.1.167")]));

        string text = File.ReadAllText(path);
        Assert.Contains("# commento iniziale", text);
        Assert.Contains("hotkey:", text);
        Assert.True(
            text.IndexOf("endpoints:", StringComparison.Ordinal)
            > text.IndexOf("server:", StringComparison.Ordinal));
        Assert.True(
            text.IndexOf("endpoints:", StringComparison.Ordinal)
            < text.IndexOf("hotkey:", StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingBlock_IsReplacedWithoutDuplicates()
    {
        string path = TempFile("""
            server:
              host: "127.0.0.1"

            endpoints:
              - name: "Vecchio"
                role: "primary"
                host: "10.0.0.1"
                port: 8000

            hotkey:
              key: "capslock"
            """);

        Assert.True(ConfigWriter.SetEndpoints(path, [Endpoint("Nuovo", "10.0.0.9")]));

        string text = File.ReadAllText(path);
        Assert.DoesNotContain("Vecchio", text);
        Assert.DoesNotContain("10.0.0.1", text);
        Assert.Single(text.Split('\n'), line => line.TrimEnd() == "endpoints:");
        Assert.Contains("hotkey:", text);
    }

    [Fact]
    public void QuotesInName_RoundtripThroughLoader()
    {
        string path = TempFile("server:\n  host: \"127.0.0.1\"\n");

        Assert.True(ConfigWriter.SetEndpoints(path, [Endpoint("Casa \"mia\"", "192.168.1.167")]));

        ClientConfig reloaded = ConfigLoader.Load(path);
        Assert.Equal("Casa \"mia\"", Assert.Single(reloaded.Endpoints!).Name);
    }

    [Fact]
    public void UnwritablePath_ReturnsFalse_AndDoesNotThrow()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"gangiokeet-missing-{Guid.NewGuid():N}",
            "client_config.yaml");

        Assert.False(ConfigWriter.SetEndpoints(path, [Endpoint("Casa", "192.168.1.167")]));
    }
}
