using System.Runtime.InteropServices;
using GangioKeet.Inference.Native;
using GangioKeet.Inference.Stt;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class SttEngineTests
{
    [Fact]
    public async Task DecodesBorrowedUtf8AndNormalizesPcm()
    {
        using SttFixture fixture = new();
        await using TranscribeSttEngine engine = fixture.CreateEngine();
        await engine.LoadAsync(CancellationToken.None);
        SpeechTranscript result = await engine.TranscribeAsync(new byte[] { 0, 0, 255, 127, 0, 128 }, CancellationToken.None);
        Assert.Equal("È già pronto?", result.Text);
        Assert.Equal(new[] { 0f, 32767f / 32768, -1f }, fixture.Api.Samples);
        Assert.Equal("vulkan", result.Engine.Backend);
    }

    [Fact]
    public async Task OddPcmFailsBeforeNativeDecode()
    {
        using SttFixture fixture = new();
        await using TranscribeSttEngine engine = fixture.CreateEngine();
        await engine.LoadAsync(CancellationToken.None);
        await Assert.ThrowsAsync<ArgumentException>(() => engine.TranscribeAsync(new byte[] { 1 }, CancellationToken.None));
        Assert.Null(fixture.Api.Samples);
    }

    [Fact]
    public async Task PrecancelledDecodeDoesNotEnterNativeCode()
    {
        using SttFixture fixture = new();
        await using TranscribeSttEngine engine = fixture.CreateEngine();
        await engine.LoadAsync(CancellationToken.None);
        using CancellationTokenSource cts = new();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.TranscribeAsync(new byte[4], cts.Token));
        Assert.Null(fixture.Api.Samples);
    }

    [Fact]
    public async Task TruncatedNativeOutputIsNotPublishedAsComplete()
    {
        using SttFixture fixture = new();
        fixture.Api.Status = 18;
        await using TranscribeSttEngine engine = fixture.CreateEngine();
        await engine.LoadAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => engine.TranscribeAsync(new byte[4], CancellationToken.None));
    }

    [Fact]
    public async Task RequestedGpuDoesNotSilentlyUseCpu()
    {
        using SttFixture fixture = new();
        fixture.Api.Backend = "cpu";
        await using TranscribeSttEngine engine = fixture.CreateEngine();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.LoadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancellationDuringBackendInitializationDoesNotLoadWeights()
    {
        using SttFixture fixture = new();
        using CancellationTokenSource cts = new();
        fixture.Api.OnInitialize = cts.Cancel;
        await using TranscribeSttEngine engine = fixture.CreateEngine();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.LoadAsync(cts.Token));
        Assert.Empty(fixture.Api.Freed);
    }
}

internal sealed class SttFixture : IDisposable
{
    internal readonly string Root = Directory.CreateTempSubdirectory("GangioKeetStt-").FullName;
    internal readonly FakeTranscribeApi Api = new();
    internal TranscribeSttEngine CreateEngine()
    {
        File.WriteAllText(Path.Combine(Root, "model.gguf"), "abc");
        ModelArtifact artifact = new("test", "parakeet-tdt", "owner/model", new string('a', 40),
            [new("model.gguf", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")],
            "F16", "Apache-2.0", "transcribe-vulkan");
        return new TranscribeSttEngine(artifact, Root, Root, "vulkan", Api);
    }
    public void Dispose()
    {
        Api.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}

internal sealed class FakeTranscribeApi : ITranscribeApi, IDisposable
{
    internal float[]? Samples;
    internal string Backend = "vulkan";
    internal int Status;
    internal Action? OnInitialize;
    internal bool Hold;
    internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly ManualResetEventSlim Release = new(false);
    internal readonly List<string> Freed = [];
    private readonly nint _text = Marshal.StringToCoTaskMemUTF8("È già pronto?");
    public string RuntimeVersion => "test-runtime";
    public void Initialize(string directory) => OnInitialize?.Invoke();
    public SafeTranscribeModel Load(string path, string backend) => new(1, _ => Freed.Add("model"));
    public SafeTranscribeSession CreateSession(SafeTranscribeModel model) => new(2, _ => Freed.Add("session"));
    public NativeDevice GetDevice(SafeTranscribeModel model) => new(Backend, "Radeon test", 1, 1);
    public int Run(SafeTranscribeSession session, float[] samples, Func<bool> abort)
    {
        Samples = samples;
        Started.TrySetResult();
        if (Hold) Release.Wait(TimeSpan.FromSeconds(10));
        return abort() ? 13 : Status;
    }
    public nint GetText(SafeTranscribeSession session) => _text;
    public void Dispose()
    {
        Marshal.FreeCoTaskMem(_text);
        Release.Dispose();
    }
}
