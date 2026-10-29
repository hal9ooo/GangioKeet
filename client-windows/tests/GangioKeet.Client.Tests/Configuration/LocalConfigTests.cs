using GangioKeet.Client.Configuration;
using Xunit;

namespace GangioKeet.Client.Tests.Configuration;

[CollectionDefinition("WorkingDirectory", DisableParallelization = true)]
public sealed class WorkingDirectoryCollection;

[Collection("WorkingDirectory")]
public sealed class LocalConfigTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetLocalConfig-").FullName;

    private string Yaml(string text)
    {
        string path = Path.Combine(_root, "config.yaml");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void NewInstallIsLocal()
    {
        ClientConfig config = ConfigLoader.Load(appDataBase: _root);
        Assert.Equal("vulkan", config.Inference.Backend);
        Assert.False(config.Recording.Enabled);
        Assert.False(config.Recording.StoreContext);
    }

    [Fact]
    public void LegacyServerEndpointsAndMode_AreIgnoredAndConfigLoadsLocal()
    {
        string path = Yaml("""
inference:
  mode: remote
  model_selection_path: "models/selected.json"
server:
  host: "192.0.2.10"
  port: 8000
endpoints:
  - name: Casa
    host: "192.0.2.10"
    port: 8000
""");

        ClientConfig config = ConfigLoader.Load(path, installationDirectory: _root);

        Assert.Equal(Path.Combine(_root, "models", "selected.json"), config.Inference.ModelSelectionPath);
        Assert.Equal("vulkan", config.Inference.Backend);
    }

    [Fact]
    public void LegacyMalformedValues_AreIgnored()
    {
        string path = Yaml("""
server: "not-a-mapping"
endpoints: 42
inference:
  mode: [remote]
  model_selection_path: "models/selected.json"
""");

        ClientConfig config = ConfigLoader.Load(path, installationDirectory: _root);

        Assert.Equal(Path.Combine(_root, "models", "selected.json"), config.Inference.ModelSelectionPath);
    }

    [Fact]
    public void UnknownKey_IsStillRejected()
    {
        string path = Yaml("inference:\n  model_selection_path: \"models/selected.json\"\nunknown_key: true\n");

        Assert.Throws<ConfigException>(() => ConfigLoader.Load(path));
    }

    [Fact]
    public void LoadingLegacyFile_DoesNotRewriteIt()
    {
        string path = Yaml("server:\n  host: \"192.0.2.10\"\ninference:\n  mode: remote\n");
        string before = File.ReadAllText(path);

        _ = ConfigLoader.Load(path);

        Assert.Equal(before, File.ReadAllText(path));
    }

    [Theory]
    [InlineData("inference:\n  backend: cuda\n")]
    [InlineData("recording:\n  directory: ''\n")]
    public void InvalidLocalOptionsFail(string yaml) =>
        Assert.Throws<ConfigException>(() => ConfigLoader.Load(Yaml(yaml)));

    [Fact]
    public void InstalledArtifactPathsDoNotDependOnWorkingDirectory()
    {
        string before = Environment.CurrentDirectory;
        string install = Path.Combine(_root, "installed");
        Directory.CreateDirectory(install);
        try
        {
            Environment.CurrentDirectory = _root;
            ClientConfig config = ConfigLoader.Load(
                Yaml("inference:\n  backend: vulkan\n"), installationDirectory: install);
            Assert.Equal(Path.Combine(install, "models", "selected.json"), config.Inference.ModelSelectionPath);
            Assert.Equal(Path.Combine(install, "cleanup.yaml"), config.Inference.CleanupPolicyPath);
        }
        finally
        {
            Environment.CurrentDirectory = before;
        }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
