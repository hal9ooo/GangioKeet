using GangioKeet.Client.Configuration;
using Xunit;

namespace GangioKeet.Client.Tests.Configuration;

public sealed class ConfigWriterLocalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetWriterLocal-").FullName;

    [Fact]
    public void SetRecordingPreservesEndpointsAndAllComments()
    {
        string path = Path.Combine(_root, "config.yaml");
        File.WriteAllText(path, "# legacy\nserver:\n  host: old.example # server\nrecording:\n  directory: private\n# recording switch\n  enabled: false # explicit\n");
        Assert.True(ConfigWriter.SetRecordingEnabled(path, true));
        string text = File.ReadAllText(path);
        Assert.Contains("host: old.example # server", text);
        Assert.Contains("# recording switch", text);
        Assert.Contains("enabled: true   # explicit", text);
        Assert.Equal(1, text.Split("enabled:").Length - 1);
        ClientConfig config = ConfigLoader.Load(path);
        Assert.Equal("remote", config.Inference.Mode);
        Assert.True(config.Recording.Enabled);
    }

    [Fact]
    public void ModeCanBeExplicitlyPersistedForNewInstall()
    {
        string path = Path.Combine(_root, "nested", "config.yaml");
        Assert.True(ConfigWriter.SetInferenceMode(path, "local"));
        Assert.Equal("local", ConfigLoader.Load(path).Inference.Mode);
        string before = File.ReadAllText(path);
        Assert.False(ConfigWriter.SetInferenceMode(path, "wrong"));
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public void UnsupportedFlowMappingIsNotCorrupted()
    {
        string path = Path.Combine(_root, "config.yaml");
        const string yaml = "recording: {enabled: false} # keep\n";
        File.WriteAllText(path, yaml);
        Assert.False(ConfigWriter.SetRecordingEnabled(path, true));
        Assert.Equal(yaml, File.ReadAllText(path));
    }

    public void Dispose() => Directory.Delete(_root, true);
}
