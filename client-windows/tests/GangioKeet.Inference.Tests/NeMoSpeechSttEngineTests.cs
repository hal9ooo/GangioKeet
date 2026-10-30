using GangioKeet.Inference;
using GangioKeet.Inference.Stt;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class NeMoSpeechSttEngineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetNeMoEngine-").FullName;

    private static ModelArtifact Artifact(params string[] files) => new(
        "nemotron_transcribe_30b_a4b", "nemotron", "nvidia/model",
        "dbd06c5cad7a2f33c8e06be2fb3ef814dbd9aa59",
        files.Select(file => new ModelFile(file, new string('a', 64))).ToArray(),
        "IQ4_NL", "OpenMDW-1.1", "nemo-speech-vulkan");

    [Fact]
    public async Task RequiresBothDecoderAndEncoder()
    {
        bool called = false;
        NeMoSpeechSttEngine engine = new(Artifact("decoder.gguf"), _root, "vulkan",
            _ => { called = true; return Task.FromResult(_root); });
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.LoadAsync(default));
        Assert.False(called);
    }

    [Fact]
    public async Task ChecksModelFilesBeforeLoadingTheRuntime()
    {
        bool called = false;
        NeMoSpeechSttEngine engine = new(Artifact("decoder.gguf", "encoder.gguf"), _root, "vulkan",
            _ => { called = true; return Task.FromResult(_root); });
        await Assert.ThrowsAsync<FileNotFoundException>(() => engine.LoadAsync(default));
        Assert.False(called);
    }

    [Fact]
    public async Task AsksForTheRuntimeOnceTheFilesArePresent()
    {
        File.WriteAllBytes(Path.Combine(_root, "decoder.gguf"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "encoder.gguf"), [2]);
        InvalidOperationException expected = new("runtime");
        NeMoSpeechSttEngine engine = new(Artifact("decoder.gguf", "encoder.gguf"), _root, "vulkan",
            _ => Task.FromException<string>(expected));

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.LoadAsync(default));
        Assert.Same(expected, actual);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
