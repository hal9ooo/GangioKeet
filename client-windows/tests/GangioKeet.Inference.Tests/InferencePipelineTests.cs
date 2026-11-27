using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class InferencePipelineTests
{
    [Fact]
    public async Task AppliesGlossaryEvenWhenTheCleanerSucceeds()
    {
        Engines engines = new() { Raw = "uso ciper inference" };
        engines.Clean = (raw, _) => Task.FromResult(raw);
        await using InferencePipeline pipeline = new(engines, engines,
            new CleanupPolicy { PhoneticOverrides = new() { ["ciper inference"] = "Cheaper Inference" } },
            TimeProvider.System);
        await pipeline.WarmUpAsync(default);
        PipelineResult result = await pipeline.ProcessAsync(new(new byte[4], "chat"), default);
        Assert.Equal("uso Cheaper Inference", result.FinalText);
        Assert.Equal("uso ciper inference", result.CleanerText);
    }
    [Fact]
    public async Task WarmupIsIdempotentAndConcurrentTurnsNeverOverlap()
    {
        Engines engines = new();
        await using InferencePipeline pipeline = engines.Pipeline();
        await Task.WhenAll(pipeline.WarmUpAsync(default), pipeline.WarmUpAsync(default));
        Assert.Equal(1, engines.SttLoads);
        Assert.Equal(1, engines.CleanerLoads);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => pipeline.ProcessAsync(new(new byte[4], "chat"), default)));
        Assert.Equal(1, engines.MaximumActive);
    }

    [Fact]
    public async Task ShortQuestionCallsCleanerButSilenceDoesNot()
    {
        Engines engines = new();
        await using InferencePipeline pipeline = engines.Pipeline();
        await pipeline.WarmUpAsync(default);
        engines.CleanerCalls = 0;
        PipelineResult result = await pipeline.ProcessAsync(new(new byte[4], "chat"), default);
        Assert.Equal("Come funziona?", result.FinalText);
        Assert.Equal("Come funziona?", result.CleanerText);
        Assert.Equal(1, engines.CleanerCalls);
        engines.Raw = " ";
        result = await pipeline.ProcessAsync(new(new byte[4], "chat"), default);
        Assert.Equal("", result.FinalText);
        Assert.Null(result.CleanerText);
        Assert.Equal(1, engines.CleanerCalls);
    }

    [Fact]
    public async Task CancellationBetweenStagesNeverStartsCleaner()
    {
        Engines engines = new();
        await using InferencePipeline pipeline = engines.Pipeline();
        await pipeline.WarmUpAsync(default);
        engines.CleanerCalls = 0;
        using CancellationTokenSource cancel = new();
        engines.AfterStt = cancel.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pipeline.ProcessAsync(new(new byte[4], "chat"), cancel.Token));
        Assert.Equal(0, engines.CleanerCalls);
    }

    [Fact]
    public async Task FailureFallsBackToCanonicalRawWithWarning()
    {
        Engines engines = new() { Raw = "fastapi funziona" };
        await using InferencePipeline pipeline = engines.Pipeline();
        await pipeline.WarmUpAsync(default);
        engines.Clean = (_, _) => throw new InvalidDataException("bad output");
        PipelineResult result = await pipeline.ProcessAsync(new(new byte[4], "chat"), default);
        Assert.Equal("FastAPI funziona", result.FinalText);
        Assert.Contains("llm_failed", result.Warnings);
        Assert.Null(result.Cleaner);
    }

    [Fact]
    public async Task TimeoutRequestsAbortAndWaitsForActualNativeReturn()
    {
        Engines engines = new();
        await using InferencePipeline pipeline = engines.Pipeline(timeout: .025);
        await pipeline.WarmUpAsync(default);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        engines.Clean = async (_, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
            await release.Task;
            token.ThrowIfCancellationRequested();
            return "late output";
        };
        Task<PipelineResult> turn = pipeline.ProcessAsync(new(new byte[4], "chat"), default);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(turn.IsCompleted);
        release.SetResult();
        PipelineResult result = await turn;
        Assert.Contains("llm_timeout", result.Warnings);
        Assert.Equal("come funziona", result.FinalText);
    }

    [Fact]
    public async Task UserCancellationNeverReturnsFallbackText()
    {
        Engines engines = new();
        await using InferencePipeline pipeline = engines.Pipeline();
        await pipeline.WarmUpAsync(default);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        engines.Clean = async (_, token) => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return "late"; };
        using CancellationTokenSource cancel = new();
        Task<PipelineResult> turn = pipeline.ProcessAsync(new(new byte[4], "chat"), cancel.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => turn);
    }

    [Fact]
    public async Task TranslationFromCleanerFallsBackToRawWithWarning()
    {
        Engines engines = new() { Raw = "呃，这些湖南。" };
        engines.Clean = (_, _) => Task.FromResult("Questi sono.");
        await using InferencePipeline pipeline = engines.Pipeline();
        await pipeline.WarmUpAsync(default);

        PipelineResult result = await pipeline.ProcessAsync(new(new byte[4], "default"), default);

        Assert.Equal("呃，这些湖南。", result.FinalText);
        Assert.Equal("Questi sono.", result.CleanerText);
        Assert.Contains("cleaner_language_switch", result.Warnings);
    }

    [Fact]
    public async Task EmptyCleanerOutputKeepsSubstantiveRawWithWarning()
    {
        Engines engines = new() { Raw = "scrivimi una funzione" };
        engines.Clean = (_, _) => Task.FromResult("");
        await using InferencePipeline pipeline = engines.Pipeline();
        await pipeline.WarmUpAsync(default);

        PipelineResult result = await pipeline.ProcessAsync(new(new byte[4], "default"), default);

        Assert.Equal("scrivimi una funzione", result.FinalText);
        Assert.Contains("cleaner_empty_output", result.Warnings);
    }

    [Fact]
    public async Task HallucinationOnMutedAudioIsDroppedBeforeTheCleaner()
    {
        // "Yeah." su un file 100% a zero: misurato su una dettatura reale.
        Engines engines = new() { Raw = "Yeah." };
        await using InferencePipeline pipeline = engines.Pipeline();
        await pipeline.WarmUpAsync(default);
        engines.CleanerCalls = 0;

        PipelineResult result = await pipeline.ProcessAsync(new(new byte[4], "default"), default);

        Assert.Equal("", result.RawText);
        Assert.Equal("", result.FinalText);
        Assert.Null(result.CleanerText);
        Assert.Equal(0, engines.CleanerCalls);
    }

    [Fact]
    public async Task SameWordOnAudibleSpeechReachesTheCleaner()
    {
        Engines engines = new() { Raw = "Yeah." };
        engines.Clean = (raw, _) => Task.FromResult(raw);
        await using InferencePipeline pipeline = engines.Pipeline();
        await pipeline.WarmUpAsync(default);
        engines.CleanerCalls = 0;
        // 0x1F40 = 8000, cioè 24% del fondo scala: voce piena.
        byte[] audible = [0x40, 0x1F, 0x00, 0x00];

        PipelineResult result = await pipeline.ProcessAsync(new(audible, "default"), default);

        Assert.Equal("Yeah.", result.RawText);
        Assert.Equal("Yeah.", result.FinalText);
        Assert.Equal(1, engines.CleanerCalls);
    }

    private sealed class Engines : ISttEngine, ITextCleaner
    {
        public int SttLoads, CleanerLoads, CleanerCalls, MaximumActive;
        private int _active;
        public string Raw = "come funziona";
        public Action? AfterStt;
        public Func<string, CancellationToken, Task<string>> Clean = (_, _) => Task.FromResult("Come funziona?");
        private static readonly EngineIdentity Identity = new("fake", "r", "h", "v", "vulkan", "F16", "fake");
        public InferencePipeline Pipeline(double timeout = 5) => new(this, this,
            new CleanupPolicy { CanonicalTerms = ["FastAPI"], LlmTimeoutSeconds = timeout }, TimeProvider.System);
        Task ISttEngine.LoadAsync(CancellationToken ct) { SttLoads++; return Task.CompletedTask; }
        Task ITextCleaner.LoadAsync(CancellationToken ct) { CleanerLoads++; return Task.CompletedTask; }
        public async Task<SpeechTranscript> TranscribeAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
        {
            MaximumActive = Math.Max(MaximumActive, Interlocked.Increment(ref _active));
            try { await Task.Yield(); AfterStt?.Invoke(); return new(Raw, Identity); }
            finally { Interlocked.Decrement(ref _active); }
        }
        public async Task<CleanedText> CleanAsync(string raw, string profile, CancellationToken ct)
        {
            CleanerCalls++;
            MaximumActive = Math.Max(MaximumActive, Interlocked.Increment(ref _active));
            try { await Task.Yield(); return new(await Clean(raw, ct), Identity); }
            finally { Interlocked.Decrement(ref _active); }
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
