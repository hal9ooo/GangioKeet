using GangioKeet.Client.Configuration;
using GangioKeet.Client.Platform;
using GangioKeet.Inference;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

public sealed class ModelSelectionPathsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetSelectionPaths-").FullName;
    private string User => Path.Combine(_root, "user", "selection.json");
    private string Install => Path.Combine(_root, "install");
    private string Legacy => Path.Combine(_root, "install", "selected.json");

    private ClientConfig Config(string path) => new()
    {
        Inference = new InferenceConfig { ModelSelectionPath = path }
    };

    [Fact]
    public void MigratesLegacySelectionToUserPath()
    {
        Directory.CreateDirectory(Install);
        File.WriteAllText(Legacy, """
            {"schema_version":1,"stt":{"candidate_id":"parakeet_v3"},"cleaner":{"candidate_id":"gemma4_e2b"}}
            """);
        ClientConfig config = Config(Legacy);

        string path = ModelSelectionPaths.Prepare(config, User, Install);

        Assert.Equal(User, path);
        Assert.Equal(User, config.Inference.ModelSelectionPath);
        ModelSelection migrated = ModelSelectionFile.Load(User);
        Assert.Equal("parakeet_v3", migrated.SttId);
        Assert.Equal("gemma4_e2b", migrated.CleanerId);
        Assert.True(File.Exists(Legacy));
    }

    [Fact]
    public void KeepsExistingUserSelection()
    {
        Directory.CreateDirectory(Install);
        File.WriteAllText(Legacy, """
            {"schema_version":1,"stt":{"candidate_id":"parakeet_v3"},"cleaner":{"candidate_id":"qwen3_1_7b"}}
            """);
        Directory.CreateDirectory(Path.GetDirectoryName(User)!);
        ModelSelectionFile.Save(User, new(2, "qwen3_asr_1_7b", "gemma4_e2b", true));
        ClientConfig config = Config(Legacy);

        ModelSelectionPaths.Prepare(config, User, Install);

        ModelSelection current = ModelSelectionFile.Load(User);
        Assert.Equal("qwen3_asr_1_7b", current.SttId);
        Assert.Equal("gemma4_e2b", current.CleanerId);
    }

    [Fact]
    public void SeedsFromInstallSelection()
    {
        Directory.CreateDirectory(Install);
        ModelSelectionFile.Save(Path.Combine(Install, "selection.json"), new(2, "parakeet_v3", null, false));
        ClientConfig config = Config(Path.Combine(Install, "missing.json"));

        ModelSelectionPaths.Prepare(config, User, Install);

        ModelSelection seeded = ModelSelectionFile.Load(User);
        Assert.Equal("parakeet_v3", seeded.SttId);
        Assert.False(seeded.RefinementEnabled);
    }

    [Fact]
    public void MissingSeedThrows()
    {
        ClientConfig config = Config(Path.Combine(Install, "missing.json"));
        Assert.Throws<FileNotFoundException>(() => ModelSelectionPaths.Prepare(config, User, Install));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
