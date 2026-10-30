using System.Text.Json;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class ModelSelectionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetSelection-").FullName;

    private string Write(string json)
    {
        string path = Path.Combine(_root, $"selection-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    private ModelCatalog CatalogWith(params string[] ids)
    {
        string path = Path.Combine(_root, "catalog.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schema_version = 1,
            candidates = ids.Select(id => new
            {
                id,
                family = "fake",
                source_checkpoint = "owner/model",
                revision = "0123456789abcdef0123456789abcdef01234567",
                files = new[] { new { path = $"{id}.gguf", sha256 = new string('a', 64) } },
                precision = "F16",
                license = "Apache-2.0",
                runtime_id = id is "gemma4_e2b" or "qwen3_1_7b" or "qwen3_4b"
                    ? "llama-vulkan"
                    : "transcribe-vulkan"
            })
        }));
        return ModelCatalog.Load(path);
    }

    [Fact]
    public void LoadV2ReadsIdsAndRefinement()
    {
        string path = Write("""
            {"schema_version":2,"stt_id":"parakeet_v3","cleaner_id":"gemma4_e2b","refinement_enabled":true}
            """);
        ModelSelection selection = ModelSelectionFile.Load(path);
        Assert.Equal(2, selection.SchemaVersion);
        Assert.Equal("parakeet_v3", selection.SttId);
        Assert.Equal("gemma4_e2b", selection.CleanerId);
        Assert.True(selection.RefinementEnabled);
    }

    [Fact]
    public void LoadV1MapsCandidateIds()
    {
        string path = Write("""
            {"schema_version":1,"stt":{"candidate_id":"parakeet_v3"},"cleaner":{"candidate_id":"qwen3_1_7b"}}
            """);
        ModelSelection selection = ModelSelectionFile.Load(path);
        Assert.Equal(1, selection.SchemaVersion);
        Assert.Equal("parakeet_v3", selection.SttId);
        Assert.Equal("qwen3_1_7b", selection.CleanerId);
        Assert.True(selection.RefinementEnabled);
    }

    [Fact]
    public void LoadV2RefinementWithoutCleanerThrows()
    {
        string path = Write("""
            {"schema_version":2,"stt_id":"parakeet_v3","cleaner_id":null,"refinement_enabled":true}
            """);
        Assert.Throws<InvalidDataException>(() => ModelSelectionFile.Load(path));
    }

    [Fact]
    public void SaveWritesV2Atomically()
    {
        string path = Path.Combine(_root, "selection.json");
        ModelSelectionFile.Save(path, new(2, "parakeet_v3", null, false));
        string json = File.ReadAllText(path);
        Assert.Contains("\"schema_version\": 2", json);
        Assert.DoesNotContain(".tmp", string.Join(",", Directory.GetFiles(_root)));
    }

    [Fact]
    public void ResolveRefinementWithoutCleanerThrows()
    {
        ModelCatalog catalog = CatalogWith("parakeet_v3");
        Assert.Throws<InvalidDataException>(() =>
            ModelSelectionResolver.Resolve(new(2, "parakeet_v3", null, true), catalog));
    }

    [Fact]
    public void ResolveUnknownSttThrowsInvalidData()
    {
        ModelCatalog catalog = CatalogWith("parakeet_v3");
        Assert.Throws<InvalidDataException>(() =>
            ModelSelectionResolver.Resolve(new(2, "missing", null, false), catalog));
    }

    [Fact]
    public void ResolveUnknownCleanerThrowsInvalidData()
    {
        ModelCatalog catalog = CatalogWith("parakeet_v3", "gemma4_e2b");
        Assert.Throws<InvalidDataException>(() =>
            ModelSelectionResolver.Resolve(new(2, "parakeet_v3", "missing", true), catalog));
    }

    [Fact]
    public void ResolveWrongRuntimeRolesThrows()
    {
        ModelCatalog catalog = CatalogWith("parakeet_v3", "gemma4_e2b");
        Assert.Throws<InvalidDataException>(() =>
            ModelSelectionResolver.Resolve(new(2, "gemma4_e2b", "parakeet_v3", true), catalog));
    }

    [Fact]
    public void LegacySelectionMigratesToV2()
    {
        string legacy = Write("""
            {"schema_version":1,"stt":{"candidate_id":"parakeet_v3"},"cleaner":{"candidate_id":"qwen3_1_7b"}}
            """);
        string v2 = Path.Combine(_root, "selection.json");
        ModelSelectionFile.Migrate(legacy, v2);
        ModelSelection selection = ModelSelectionFile.Load(v2);
        Assert.Equal(2, selection.SchemaVersion);
        Assert.Equal("parakeet_v3", selection.SttId);
        Assert.Equal("qwen3_1_7b", selection.CleanerId);
        Assert.True(File.Exists(legacy));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
