using System.Text.Json;
using GangioKeet.Client.Platform;
using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

public sealed class ModelSelectionServiceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetSelectionService-").FullName;

    private sealed class Counter
    {
        public int Stt;
        public int Cleaner;
    }

    private sealed class FakeStt(Counter counter, string text = "raw") : ISttEngine
    {
        private static readonly EngineIdentity Identity = new("fake-stt", "r", "h", "v", "vulkan", "F16", "fake");
        public Task LoadAsync(CancellationToken ct) { counter.Stt++; return Task.CompletedTask; }
        public Task<SpeechTranscript> TranscribeAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) =>
            Task.FromResult(new SpeechTranscript(text, Identity));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCleaner(Counter counter) : ITextCleaner
    {
        private static readonly EngineIdentity Identity = new("fake-cleaner", "r", "h", "v", "vulkan", "Q4_0", "fake");
        public Task LoadAsync(CancellationToken ct) { counter.Cleaner++; return Task.CompletedTask; }
        public Task<CleanedText> CleanAsync(string raw, string profile, CancellationToken ct) =>
            Task.FromResult(new CleanedText(raw, Identity));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeResolver(string modelRoot, Counter counter) : ILocalModelResolver
    {
        public string ModelRoot => modelRoot;

        public ResolvedModelSelection Resolve(ModelSelection selection)
        {
            ModelArtifact stt = Artifact(selection.SttId);
            ModelArtifact? cleaner = selection.CleanerId is null ? null : Artifact(selection.CleanerId);
            return new(selection, stt, cleaner);
        }

        public ISttEngine CreateStt(ModelArtifact artifact) => new FakeStt(counter, artifact.Id);

        public ITextCleaner CreateCleaner(ModelArtifact artifact) => new FakeCleaner(counter);

        private static ModelArtifact Artifact(string id) => new(id, "fake", "owner/model",
            "0123456789abcdef0123456789abcdef01234567",
            [new ModelFile($"{id}.gguf", new string('a', 64))],
            "F16", "Apache-2.0", id.Contains("asr") || id == "parakeet_v3" ? "transcribe-vulkan" : "llama-vulkan");
    }

    private sealed class FakeProvisioner : IModelProvisioner
    {
        public bool Fail { get; set; }
        public int Calls { get; set; }

        public Task EnsureModelsAsync(string selectionPath, string? modelRoot = null,
            IProgress<ModelProvisionProgress>? progress = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task EnsureModelsAsync(IReadOnlyList<ModelArtifact> artifacts, string modelRoot,
            IProgress<ModelProvisionProgress>? progress = null, CancellationToken ct = default)
        {
            Calls++;
            if (Fail) throw new InvalidDataException("provisioning failed");
            return Task.CompletedTask;
        }
    }

    private sealed class Engine(Counter counter) : ISttEngine, ITextCleaner
    {
        private static readonly EngineIdentity Identity = new("engine", "r", "h", "v", "vulkan", "F16", "fake");
        public Task LoadAsync(CancellationToken ct) { counter.Stt++; return Task.CompletedTask; }
        public Task<SpeechTranscript> TranscribeAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) =>
            Task.FromResult(new SpeechTranscript("raw", Identity));
        Task ITextCleaner.LoadAsync(CancellationToken ct) { counter.Cleaner++; return Task.CompletedTask; }
        public Task<CleanedText> CleanAsync(string raw, string profile, CancellationToken ct) =>
            Task.FromResult(new CleanedText(raw, Identity));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Fixture
    {
        public required Counter Counter { get; init; }
        public required Engine Engine { get; init; }
        public required InferencePipeline Pipeline { get; init; }
        public required FakeProvisioner Provisioner { get; init; }
        public required ModelSelectionService Service { get; init; }
        public required ModelSelection Selection { get; init; }
        public required string SelectionPath { get; init; }
        public required string OriginalJson { get; init; }
        public Counter Engines => Counter;

        public static Fixture Create(bool refinement)
        {
            string root = Directory.CreateTempSubdirectory("GangioKeetSelectionServiceFixture-").FullName;
            ModelSelection selection = new(2, "parakeet_v3", "gemma4_e2b", refinement);
            string selectionPath = Path.Combine(root, "selection.json");
            ModelSelectionFile.Save(selectionPath, selection);

            Counter counter = new();
            Engine engine = new(counter);
            InferencePipeline pipeline = new(engine, engine, new CleanupPolicy(), TimeProvider.System, refinement);
            FakeProvisioner provisioner = new();
            ModelSelectionService service = new(pipeline, new FakeResolver(root, counter), provisioner,
                selectionPath, selection);
            return new()
            {
                Counter = counter,
                Engine = engine,
                Pipeline = pipeline,
                Provisioner = provisioner,
                Service = service,
                Selection = selection,
                SelectionPath = selectionPath,
                OriginalJson = File.ReadAllText(selectionPath)
            };
        }
    }

    [Fact]
    public async Task ToggleOffDoesNotSwap()
    {
        Fixture f = Fixture.Create(refinement: true);
        int stt = f.Engines.Stt;
        int cleaner = f.Engines.Cleaner;
        await f.Service.ApplyAsync(f.Selection with { RefinementEnabled = false }, null, default);
        Assert.Equal(stt, f.Engines.Stt);
        Assert.Equal(cleaner, f.Engines.Cleaner);
        Assert.False(f.Pipeline.RefinementEnabled);
    }

    [Fact]
    public async Task ToggleOffDoesNotProvision()
    {
        Fixture f = Fixture.Create(refinement: true);
        f.Provisioner.Calls = 0;
        await f.Service.ApplyAsync(f.Selection with { RefinementEnabled = false }, null, default);
        Assert.Equal(0, f.Provisioner.Calls);
    }

    [Fact]
    public async Task ChangeCleanerSwapsOnlyCleaner()
    {
        Fixture f = Fixture.Create(refinement: true);
        int stt = f.Engines.Stt;
        int cleaner = f.Engines.Cleaner;
        await f.Service.ApplyAsync(f.Selection with { CleanerId = "qwen3_1_7b" }, null, default);
        Assert.Equal(stt, f.Engines.Stt);
        Assert.Equal(cleaner + 1, f.Engines.Cleaner);
    }

    [Fact]
    public async Task EnablingAfterCleanerChangeWhileOffLoadsTheSelectedCleaner()
    {
        Fixture f = Fixture.Create(refinement: true);
        int cleanerLoads = f.Engines.Cleaner;

        await f.Service.ApplyAsync(f.Selection with
        {
            RefinementEnabled = false,
            CleanerId = "qwen3_1_7b"
        }, null, default);
        Assert.Equal(cleanerLoads, f.Engines.Cleaner);

        await f.Service.ApplyAsync(f.Selection with
        {
            RefinementEnabled = true,
            CleanerId = "qwen3_1_7b"
        }, null, default);
        Assert.Equal(cleanerLoads + 1, f.Engines.Cleaner);
    }

    [Fact]
    public async Task ChangeSttSwapsOnlyStt()
    {
        Fixture f = Fixture.Create(refinement: true);
        int stt = f.Engines.Stt;
        int cleaner = f.Engines.Cleaner;
        await f.Service.ApplyAsync(f.Selection with { SttId = "qwen3_asr_1_7b" }, null, default);
        Assert.Equal(stt + 1, f.Engines.Stt);
        Assert.Equal(cleaner, f.Engines.Cleaner);
    }

    [Fact]
    public async Task FailedProvisioningLeavesCurrentSelection()
    {
        Fixture f = Fixture.Create(refinement: true);
        f.Provisioner.Fail = true;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            f.Service.ApplyAsync(f.Selection with { SttId = "qwen3_asr_1_7b" }, null, default));
        Assert.Equal(f.Selection, f.Service.Current);
        Assert.Equal(f.OriginalJson, File.ReadAllText(f.SelectionPath));
    }

    [Fact]
    public async Task SuccessWritesSelectionFile()
    {
        Fixture f = Fixture.Create(refinement: true);
        await f.Service.ApplyAsync(f.Selection with { RefinementEnabled = false }, null, default);
        Assert.Contains("\"refinement_enabled\": false", File.ReadAllText(f.SelectionPath));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
