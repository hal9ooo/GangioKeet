using GangioKeet.Client.Controller;
using GangioKeet.Client.Recording;
using GangioKeet.Client.Protocol;
using GangioKeet.Inference;
using Xunit;

namespace GangioKeet.Client.Tests.Controller;

public sealed class LocalTurnTests
{
    private static readonly StartMessage Start = new("test", "controlled target", "chat");

    [Fact]
    public async Task ChunksAreCopiedInOrderAndRepeatedFinishRunsOnce()
    {
        Pipeline pipeline = new();
        await using LocalDictationTurn turn = new(pipeline, Start);
        Assert.True((await turn.StartAsync(default)).Ready);
        byte[] first = [1, 2];
        Assert.True(turn.TryEnqueueAudio(first));
        first[0] = 99;
        Assert.True(turn.TryEnqueueAudio(new byte[] { 3, 4 }));
        DictationOutcome[] outcomes = await Task.WhenAll(turn.FinishAsync(default), turn.FinishAsync(default));
        Assert.Equal(1, pipeline.Calls);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, pipeline.Seen);
        Assert.All(outcomes, x => Assert.Equal("Come funziona?", x.Result!.FinalText));
        Assert.False(turn.TryEnqueueAudio(new byte[] { 5, 6 }));
    }

    [Theory]
    [InlineData(1, "invalid_audio")]
    [InlineData(6, "audio_too_large")]
    public async Task InvalidChunksHaveExplicitTerminalError(int size, string code)
    {
        Pipeline pipeline = new();
        await using LocalDictationTurn turn = new(pipeline, Start, maxBytes: 4);
        await turn.StartAsync(default);
        Assert.False(turn.TryEnqueueAudio(new byte[size]));
        Assert.Equal(code, (await turn.Completion).Error!.Code);
        Assert.Equal(0, pipeline.Calls);
    }

    [Fact]
    public async Task AbortAndDisposeWaitForRealPipelineReturnWithoutPublishingText()
    {
        Pipeline pipeline = new() { Hold = true };
        LocalDictationTurn turn = new(pipeline, Start);
        await turn.StartAsync(default);
        turn.TryEnqueueAudio(new byte[4]);
        Task<DictationOutcome> finish = turn.FinishAsync(default);
        await pipeline.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task abort = turn.AbortAsync(default);
        Assert.False(abort.IsCompleted);
        pipeline.Release.SetResult();
        await abort;
        Assert.Equal(DictationStatus.Aborted, (await finish).Status);
        await turn.DisposeAsync();
        await turn.DisposeAsync();
        Assert.Equal(0, pipeline.DisposeCalls); // app runtime owns the shared pipeline
    }

    [Fact]
    public async Task CompletedTurnEnqueuesRecordingWithoutChangingTheResult()
    {
        Pipeline pipeline = new();
        FakeSink sink = new();
        await using LocalDictationTurn turn = new(pipeline, Start, recordings: sink);
        await turn.StartAsync(default);
        Assert.True(turn.TryEnqueueAudio(new byte[] { 1, 2, 3, 4 }));
        DictationOutcome outcome = await turn.FinishAsync(default);

        Assert.Equal(DictationStatus.Result, outcome.Status);
        Assert.Equal(1, sink.Calls);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, sink.Pcm);
        Assert.Equal("Come funziona?", sink.Result!.FinalText);
    }

    [Fact]
    public async Task LocalRetryUsesOnlySharedPipeline()
    {
        Pipeline pipeline = new();
        LocalDictationBackend backend = new(pipeline);
        DictationOutcome outcome = await backend.RetryAsync(new byte[4], Start, default);
        Assert.Equal(DictationStatus.Result, outcome.Status);
        Assert.Equal(1, pipeline.Calls);
    }

    [Fact]
    public async Task NotReadyUsesLocalMessageAndDoesNotStartInference()
    {
        Pipeline pipeline = new();
        LocalDictationBackend backend = new(pipeline, () => false);
        await using IActiveDictationTurn turn = backend.CreateTurn(Start);
        TurnStartOutcome outcome = await turn.StartAsync(default);
        Assert.False(outcome.Ready);
        Assert.Equal("local_not_ready", outcome.Error!.Code);
        Assert.DoesNotContain("Server", outcome.Error.Message);
        Assert.Equal(0, pipeline.Calls);
    }

    private sealed class FakeSink : IRecordingSink
    {
        public int Calls;
        public byte[] Pcm = [];
        public PipelineResult? Result;

        public bool TryEnqueue(ReadOnlyMemory<byte> pcm, PipelineResult result, StartMessage start)
        {
            Calls++;
            Pcm = pcm.ToArray();
            Result = result;
            return true;
        }
    }

    private sealed class Pipeline : IInferencePipeline
    {
        public int Calls, DisposeCalls;
        public byte[] Seen = [];
        public bool Hold;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;
        public async Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct)
        {
            Calls++;
            Seen = request.Pcm16.ToArray();
            Started.TrySetResult();
            if (Hold) await Release.Task;
            ct.ThrowIfCancellationRequested();
            EngineIdentity identity = new("fake", "r", "h", "v", "vulkan", "F16", "fake");
            return new("come funziona", "Come funziona?", 1, 2, 3, [], identity, identity);
        }
        public ValueTask DisposeAsync() { DisposeCalls++; return ValueTask.CompletedTask; }
    }
}
