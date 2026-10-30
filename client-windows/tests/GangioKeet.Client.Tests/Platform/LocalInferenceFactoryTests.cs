using System.Security.Cryptography;
using System.Text.Json;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Platform;
using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

public sealed class LocalInferenceFactoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetFactory-").FullName;
    private readonly FakeStt _stt = new();
    private readonly FakeCleaner _cleaner = new();
    private ClientConfig _config = new();

    private string Hash(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private string WriteFixture(string sttId = "parakeet_v3", string? cleanerId = "gemma4_e2b",
        bool refinement = true, bool deleteCatalog = false, string? catalogHashOverride = null)
    {
        string cleanup = Path.Combine(_root, "cleanup.yaml");
        File.WriteAllText(cleanup, """
            llm_timeout_seconds: 5
            canonical_terms:
            - FastAPI
            phonetic_overrides:
              fast a pi: FastAPI
            """);
        string runtime = Path.Combine(_root, "runtime-lock.json");
        File.WriteAllText(runtime, "runtime");

        string[] ids = ["parakeet_v3", "qwen3_asr_1_7b", "gemma4_e2b", "qwen3_1_7b", "whisper_large_v3", "qwen3_4b"];
        string catalog = Path.Combine(_root, "candidates.json");
        File.WriteAllText(catalog, JsonSerializer.Serialize(new
        {
            schema_version = 1,
            candidates = ids.Select(id => new
            {
                id,
                family = "fake",
                source_checkpoint = $"owner/{id}",
                revision = "0123456789abcdef0123456789abcdef01234567",
                files = new[] { new { path = $"{id}.gguf", sha256 = new string('a', 64) } },
                precision = "F16",
                license = "Apache-2.0",
                runtime_id = id is "gemma4_e2b" or "qwen3_1_7b" or "qwen3_4b"
                    ? "llama-vulkan"
                    : "transcribe-vulkan",
                display_name = id
            })
        }));

        string selection = Path.Combine(_root, "selection.json");
        File.WriteAllText(selection, JsonSerializer.Serialize(new
        {
            schema_version = 2,
            stt_id = sttId,
            cleaner_id = cleanerId,
            refinement_enabled = refinement
        }));

        string integrity = Path.Combine(_root, "integrity.json");
        File.WriteAllText(integrity, JsonSerializer.Serialize(new
        {
            schema_version = 1,
            catalog_sha256 = catalogHashOverride ?? Hash(catalog),
            runtime_lock_sha256 = Hash(runtime),
            cleanup_policy_sha256 = Hash(cleanup)
        }));

        if (deleteCatalog) File.Delete(catalog);

        _config = new ClientConfig
        {
            Inference = new InferenceConfig
            {
                ModelSelectionPath = selection,
                CleanupPolicyPath = cleanup,
                Backend = "vulkan"
            }
        };
        return selection;
    }

    private LocalInferenceSetup Create() => LocalInferenceFactory.Create(
        _config,
        nativeDirectory: _root,
        llamaDirectory: _root,
        runtimeLockPath: Path.Combine(_root, "runtime-lock.json"),
        modelRoot: _root,
        sttFactory: _ => _stt,
        cleanerFactory: _ => _cleaner);

    [Fact]
    public void CreateV2UsesSelectedArtifacts()
    {
        WriteFixture(sttId: "parakeet_v3", cleanerId: "gemma4_e2b", refinement: true);
        LocalInferenceSetup setup = Create();
        Assert.Equal("parakeet_v3", setup.SttId);
        Assert.Equal("gemma4_e2b", setup.CleanerId);
        Assert.True(setup.RefinementEnabled);
        Assert.Equal("parakeet_v3", setup.SttArtifact.Id);
        Assert.Equal("gemma4_e2b", setup.CleanerArtifact!.Id);
    }

    [Fact]
    public void CreateV2RefinementOffSkipsCleaner()
    {
        WriteFixture(sttId: "parakeet_v3", cleanerId: null, refinement: false);
        LocalInferenceSetup setup = Create();
        Assert.Null(setup.CleanerArtifact);
        Assert.Equal("nessuno", setup.CleanerId);
        Assert.False(setup.RefinementEnabled);
    }

    [Fact]
    public void CreateV2IntegrityMismatchThrows()
    {
        WriteFixture(catalogHashOverride: new string('0', 64));
        Assert.Throws<InvalidDataException>(() => Create());
    }

    [Fact]
    public void CreateV2FallsBackToInstallCatalog()
    {
        WriteFixture(sttId: "parakeet_v3", cleanerId: "gemma4_e2b", refinement: true);
        string userDirectory = Path.Combine(_root, "user");
        Directory.CreateDirectory(userDirectory);
        string userSelection = Path.Combine(userDirectory, "selection.json");
        File.Move(Path.Combine(_root, "selection.json"), userSelection);
        _config.Inference.ModelSelectionPath = userSelection;

        LocalInferenceSetup setup = Create();

        Assert.Equal("parakeet_v3", setup.SttId);
        Assert.Equal("gemma4_e2b", setup.CleanerId);
    }

    [Fact]
    public void CreateSelectionWithoutCatalogThrows()
    {
        WriteFixture(deleteCatalog: true);
        Assert.Throws<FileNotFoundException>(() => Create());
    }

    private sealed class FakeStt : ISttEngine
    {
        private static readonly EngineIdentity Identity = new("fake-stt", "r", "h", "v", "vulkan", "F16", "fake");
        public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<SpeechTranscript> TranscribeAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) =>
            Task.FromResult(new SpeechTranscript("test", Identity));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCleaner : ITextCleaner
    {
        private static readonly EngineIdentity Identity = new("fake-cleaner", "r", "h", "v", "vulkan", "Q4_0", "fake");
        public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<CleanedText> CleanAsync(string raw, string profile, CancellationToken ct) =>
            Task.FromResult(new CleanedText(raw, Identity));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
