using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class InferencePipelineSelectionTests
{
    [Fact]
    public async Task RefinementDisabledReturnsRawText()
    {
        Engines engines = new() { Raw = "come funziona" };
        await using InferencePipeline pipeline = engines.Pipeline(refinement: false);
        await pipeline.WarmUpAsync(default);
        PipelineResult result = await pipeline.ProcessAsync(new(new byte[4], "chat"), default);
        Assert.Equal("come funziona", result.FinalText);
        Assert.Equal(0, engines.CleanerCalls);
        Assert.Equal(0, result.LlmMs);
        Assert.Null(result.Cleaner);
    }

    [Fact]
    public async Task RefinementCannotEnableWithoutCleaner()
    {
        Engines engines = new();
        await using InferencePipeline pipeline = new(engines, null, new CleanupPolicy(), TimeProvider.System, false);
        Assert.Throws<InvalidOperationException>(() => pipeline.RefinementEnabled = true);
    }

    [Fact]
    public async Task DisposeStillDisposesSttWhenCleanerDisposeThrows()
    {
        Engines engines = new();
        FakeCleaner cleaner = new() { DisposeFailure = new InvalidDataException("dispose failed") };
        InferencePipeline pipeline = new(engines, cleaner, new CleanupPolicy(), TimeProvider.System);
        await pipeline.WarmUpAsync(default);

        await Assert.ThrowsAsync<InvalidDataException>(() => pipeline.DisposeAsync().AsTask());

        Assert.True(engines.Disposed);
    }

    [Fact]
    public async Task SwapSttDoesNotReloadCleaner()
    {
        Engines engines = new();
        await using InferencePipeline pipeline = engines.Pipeline();
        await pipeline.WarmUpAsync(default);
        int cleanerLoads = engines.CleanerLoads;
        await pipeline.SwapSttAsync(new FakeStt("nuovo"), default);
        Assert.Equal(cleanerLoads, engines.CleanerLoads);
        Assert.Equal("nuovo", (await pipeline.ProcessAsync(new(new byte[4], "chat"), default)).RawText);
    }

    [Fact]
    public async Task SwapEnginesWhenCleanerLoadFailsLeavesOldEngines()
    {
        Engines engines = new();
        await using InferencePipeline pipeline = engines.Pipeline();
        await pipeline.WarmUpAsync(default);
        engines.CleanerCalls = 0;
        FakeCleaner failing = new() { LoadFailure = new InvalidDataException("bad cleaner") };
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            pipeline.SwapEnginesAsync(new FakeStt("nuovo"), failing, true, default));
        PipelineResult result = await pipeline.ProcessAsync(new(new byte[4], "chat"), default);
        Assert.Equal("Come funziona?", result.FinalText);
        Assert.Equal(1, engines.CleanerCalls);
    }

    [Fact]
    public async Task SwapEnginesWhenCleanerWarmupFailsDisposesNewEngines()
    {
        Engines engines = new();
        await using InferencePipeline pipeline = engines.Pipeline();
        await pipeline.WarmUpAsync(default);
        FakeStt newStt = new("nuovo");
        FakeCleaner failing = new() { WarmupFailure = new InvalidDataException("warm-up failed") };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            pipeline.SwapEnginesAsync(newStt, failing, true, default));

        Assert.True(newStt.Disposed);
        Assert.True(failing.Disposed);
        Assert.Equal("Come funziona?", (await pipeline.ProcessAsync(new(new byte[4], "chat"), default)).FinalText);
    }

    [Fact]
    public async Task SwapEnginesWaitsForActiveTurn()
    {
        Engines engines = new();
        await using InferencePipeline pipeline = engines.Pipeline();
        await pipeline.WarmUpAsync(default);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        engines.AfterStt = () => release.Task.GetAwaiter().GetResult();
        Task<PipelineResult> turn = pipeline.ProcessAsync(new(new byte[4], "chat"), default);
        Task swap = pipeline.SwapSttAsync(new FakeStt("nuovo"), default);
        await Task.Delay(50);
        Assert.False(swap.IsCompleted);
        release.SetResult();
        await turn;
        await swap;
    }

    [Fact]
    public async Task DisposeWaitsForActiveTurnWithoutCancellingIt()
    {
        Engines engines = new();
        InferencePipeline pipeline = engines.Pipeline();
        await pipeline.WarmUpAsync(default);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        engines.AfterStt = () => release.Task.GetAwaiter().GetResult();
        Task<PipelineResult> turn = pipeline.ProcessAsync(new(new byte[4], "chat"), default);
        Task dispose = pipeline.DisposeAsync().AsTask();
        await Task.Delay(50);
        Assert.False(turn.IsCompleted);
        release.SetResult();
        PipelineResult result = await turn;
        await dispose;
        Assert.Equal("Come funziona?", result.FinalText);
    }

    private sealed class FakeStt(string text) : ISttEngine
    {
        private static readonly EngineIdentity Identity = new("fake-stt", "r", "h", "v", "vulkan", "F16", "fake");
        public bool Disposed { get; private set; }

        public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<SpeechTranscript> TranscribeAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct) =>
            Task.FromResult(new SpeechTranscript(text, Identity));

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCleaner : ITextCleaner
    {
        private static readonly EngineIdentity Identity = new("fake-cleaner", "r", "h", "v", "vulkan", "Q4_0", "fake");
        public Exception? LoadFailure { get; init; }
        public Exception? WarmupFailure { get; init; }
        public Exception? DisposeFailure { get; init; }
        public bool Disposed { get; private set; }

        public Task LoadAsync(CancellationToken ct) =>
            LoadFailure is null ? Task.CompletedTask : Task.FromException(LoadFailure);

        public Task<CleanedText> CleanAsync(string raw, string profile, CancellationToken ct) =>
            WarmupFailure is null
                ? Task.FromResult(new CleanedText(raw + "!", Identity))
                : Task.FromException<CleanedText>(WarmupFailure);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return DisposeFailure is null
                ? ValueTask.CompletedTask
                : new ValueTask(Task.FromException(DisposeFailure));
        }
    }

    private sealed class Engines : ISttEngine, ITextCleaner
    {
        public int SttLoads;
        public int CleanerLoads;
        public int CleanerCalls;
        public string Raw = "Come funziona";
        public Action? AfterStt;
        public bool Disposed;
        public Func<string, CancellationToken, Task<string>> Clean = (raw, _) => Task.FromResult(raw + "?");
        private static readonly EngineIdentity Identity = new("fake", "r", "h", "v", "vulkan", "F16", "fake");

        public InferencePipeline Pipeline(bool refinement = true) => new(this, this,
            new CleanupPolicy { CanonicalTerms = ["FastAPI"], LlmTimeoutSeconds = 5 }, TimeProvider.System, refinement);

        Task ISttEngine.LoadAsync(CancellationToken ct) { SttLoads++; return Task.CompletedTask; }

        public async Task<SpeechTranscript> TranscribeAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            await Task.Yield();
            AfterStt?.Invoke();
            return new(Raw, Identity);
        }

        Task ITextCleaner.LoadAsync(CancellationToken ct) { CleanerLoads++; return Task.CompletedTask; }

        public async Task<CleanedText> CleanAsync(string raw, string profile, CancellationToken ct)
        {
            CleanerCalls++;
            await Task.Yield();
            return new(await Clean(raw, ct), Identity);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
