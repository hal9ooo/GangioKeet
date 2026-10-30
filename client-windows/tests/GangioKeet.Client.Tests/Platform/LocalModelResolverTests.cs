using GangioKeet.Client.Platform;
using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
using GangioKeet.Inference.Stt;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

public sealed class LocalModelResolverTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetResolver-").FullName;

    private sealed class FakeRuntimeProvisioner : INativeRuntimeProvisioner
    {
        public int Calls { get; private set; }
        public string? RuntimeId { get; private set; }

        public Task<string> EnsureAsync(string runtimeId, IProgress<ModelProvisionProgress>? progress,
            CancellationToken ct)
        {
            Calls++;
            RuntimeId = runtimeId;
            return Task.FromResult(Path.Combine(Path.GetTempPath(), "nemo-runtime"));
        }
    }

    private LocalModelResolver Create(FakeRuntimeProvisioner? runtime = null) => new(
        _root,
        Path.Combine(_root, "candidates.json"),
        Path.Combine(_root, "integrity.json"),
        Path.Combine(_root, "runtime-lock.json"),
        Path.Combine(_root, "cleanup.yaml"),
        _root,
        _root,
        "vulkan",
        new CleanupPolicy(),
        runtime);

    private static ModelArtifact Artifact(string id, string runtime, params string[] files) => new(
        id, "fake", "owner/model", "0123456789abcdef0123456789abcdef01234567",
        files.Select(file => new ModelFile(file, new string('a', 64))).ToArray(),
        "F16", "Apache-2.0", runtime);

    [Fact]
    public void CreatesNemotronEngineForTheSpeechLmRuntime()
    {
        ISttEngine engine = Create(new FakeRuntimeProvisioner()).CreateStt(Artifact(
            "nemotron_transcribe_30b_a4b", "nemo-speech-vulkan", "decoder.gguf", "encoder.gguf"));
        Assert.IsType<NeMoSpeechSttEngine>(engine);
    }

    [Fact]
    public void CreatesTranscribeEngineForTheDefaultRuntime()
    {
        ISttEngine engine = Create().CreateStt(Artifact("parakeet_v3", "transcribe-vulkan", "model.gguf"));
        Assert.IsType<TranscribeSttEngine>(engine);
    }

    [Fact]
    public async Task EnsuresOnlyTheNemotronNativeRuntime()
    {
        FakeRuntimeProvisioner runtime = new();
        LocalModelResolver resolver = Create(runtime);

        await resolver.EnsureRuntimeAsync(Artifact("parakeet_v3", "transcribe-vulkan", "model.gguf"),
            null, default);
        Assert.Equal(0, runtime.Calls);

        await resolver.EnsureRuntimeAsync(Artifact("nemotron_transcribe_30b_a4b",
            "nemo-speech-vulkan", "decoder.gguf", "encoder.gguf"), null, default);
        Assert.Equal(1, runtime.Calls);
        Assert.Equal("nemo-speech-vulkan", runtime.RuntimeId);
    }

    [Fact]
    public async Task NemotronWithoutProvisionerFailsClearly()
    {
        LocalModelResolver resolver = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.EnsureRuntimeAsync(
            Artifact("nemotron_transcribe_30b_a4b", "nemo-speech-vulkan", "decoder.gguf", "encoder.gguf"),
            null, default));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
