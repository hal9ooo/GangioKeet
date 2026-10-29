using GangioKeet.Client.Configuration;
using Xunit;

namespace GangioKeet.Client.Tests.Configuration;

public sealed class ConfigWriterLocalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetWriterLocal-").FullName;

    [Fact]
    public void SetRecordingPreservesLegacyKeysAndAllComments()
    {
        string path = Path.Combine(_root, "config.yaml");
        File.WriteAllText(path, "# legacy\nserver:\n  host: old.example # server\nrecording:\n  directory: private\n# recording switch\n  enabled: false # explicit\n");
        Assert.True(ConfigWriter.SetRecordingEnabled(path, true));
        string text = File.ReadAllText(path);
        Assert.Contains("host: old.example # server", text);
        Assert.Contains("# recording switch", text);
        Assert.Contains("enabled: true   # explicit", text);
        Assert.Equal(1, text.Split("enabled:").Length - 1);
        ClientConfig config = ConfigLoader.Load(path, installationDirectory: _root);
        Assert.True(config.Recording.Enabled);
        Assert.Equal(Path.Combine(_root, "models", "selected.json"), config.Inference.ModelSelectionPath);
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
