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
    public void NewInstallIsLocalWithoutSyntheticEndpoints()
    {
        ClientConfig config = ConfigLoader.Load(appDataBase: _root);
        Assert.Equal("local", config.Inference.Mode);
        Assert.Empty(config.Endpoints!);
        Assert.False(config.Recording.Enabled);
        Assert.False(config.Recording.StoreContext);
    }

    [Theory]
    [InlineData("server:\n  host: legacy.example\n  port: 8080\n")]
    [InlineData("endpoints:\n  - name: Legacy\n    role: primary\n    host: legacy.example\n    port: 8080\n")]
    public void LegacyRemoteIsPreservedWithoutWritingMigration(string yaml)
    {
        string path = Yaml(yaml);
        ClientConfig config = ConfigLoader.Load(path);
        Assert.Equal("remote", config.Inference.Mode);
        EndpointConfig endpoint = Assert.Single(config.Endpoints!);
        Assert.Equal("legacy.example", endpoint.Host);
        Assert.Equal(8080, endpoint.Port);
        Assert.Equal(yaml, File.ReadAllText(path));
    }

    [Fact]
    public void ExplicitLocalDoesNotReadMissingRemoteCredential()
    {
        ClientConfig config = ConfigLoader.Load(Yaml("inference:\n  mode: local\nserver:\n  api_key_env: GANGIOKEET_TEST_UNSET_47A2\n"));
        Assert.Equal("local", config.Inference.Mode);
        Assert.Empty(config.Endpoints!);
        Assert.Equal("GANGIOKEET_TEST_UNSET_47A2", config.Server.ApiKeyEnv);
    }

    [Fact]
    public void ExplicitRemoteStillRequiresConfiguredCredential() => Assert.Throws<ConfigException>(() =>
        ConfigLoader.Load(Yaml("inference:\n  mode: remote\nserver:\n  api_key_env: GANGIOKEET_TEST_UNSET_47A2\n")));

    [Theory]
    [InlineData("inference:\n  mode: offline\n")]
    [InlineData("inference:\n  backend: cuda\n")]
    [InlineData("recording:\n  directory: ''\n")]
    public void InvalidLocalOptionsFail(string yaml) => Assert.Throws<ConfigException>(() => ConfigLoader.Load(Yaml(yaml)));

    [Fact]
    public void InstalledArtifactPathsDoNotDependOnWorkingDirectory()
    {
        string before = Environment.CurrentDirectory;
        string install = Path.Combine(_root, "installed");
        Directory.CreateDirectory(install);
        try
        {
            Environment.CurrentDirectory = _root;
            ClientConfig config = ConfigLoader.Load(Yaml("inference:\n  mode: local\n"), installationDirectory: install);
            Assert.Equal(Path.Combine(install, "models", "selected.json"), config.Inference.ModelSelectionPath);
            Assert.Equal(Path.Combine(install, "cleanup.yaml"), config.Inference.CleanupPolicyPath);
        }
        finally { Environment.CurrentDirectory = before; }
    }

    public void Dispose() => Directory.Delete(_root, true);
}
