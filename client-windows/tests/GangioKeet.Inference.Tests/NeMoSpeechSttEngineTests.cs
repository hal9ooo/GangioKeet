using System.Collections.Concurrent;
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

        [Fact]
    public async Task LoadDoesNotBlockTheThreadThatAwaitsIt()
    {
        // Create() carica e mappa il modello in modo sincrono. Se girasse sul thread
        // che fa l'await (la UI della finestra impostazioni), la finestra resterebbe
        // congelata per tutti i secondi del caricamento.
        File.WriteAllBytes(Path.Combine(_root, "decoder.gguf"), [1]);
        File.WriteAllBytes(Path.Combine(_root, "encoder.gguf"), [2]);

        NeMoSpeechSttEngine engine = new(Artifact("decoder.gguf", "encoder.gguf"), _root, "vulkan",
            _ => Task.FromResult(_root));

        // Con il fix, Task.Run cede subito il controllo e questa attesa non si
        // blocca: il caricamento prosegue su un altro thread.
        Task load = engine.LoadAsync(default);
        bool returned = await Task.WhenAny(load, Task.Delay(TimeSpan.FromSeconds(10))) == load;

        Assert.True(returned || load.Status == TaskStatus.RanToCompletion,
            "LoadAsync non ha ceduto il controllo: il caricamento gira sul thread chiamante");
        Assert.NotNull(await Record.ExceptionAsync(async () => await load));
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        // Come il dispatcher WPF: la continuazione resta in coda finche' il
        // thread che possiede il contesto non la esegue.
        public override void Post(SendOrPostCallback d, object? state) => _queue.Enqueue((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public override SynchronizationContext CreateCopy() => this;

        public void Drain()
        {
            while (_queue.TryDequeue(out (SendOrPostCallback Callback, object? State) item))
                item.Callback(item.State);
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
