using GangioKeet.Client.Platform;
using GangioKeet.Inference;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

public sealed class AppRuntimeTests
{
    [Fact]
    public async Task StartStaysLoadingUntilWarmupCompletes()
    {
        FakePipeline pipeline = new();
        await using AppRuntime runtime = new(pipeline);

        Task starting = runtime.StartAsync(default);
        await pipeline.WarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RuntimeState.Loading, runtime.State);
        Assert.False(runtime.IsReady);
        Assert.Equal(1, pipeline.WarmupCalls);

        pipeline.ReleaseWarmup();
        await starting;

        Assert.Equal(RuntimeState.Ready, runtime.State);
        Assert.True(runtime.IsReady);
    }

    [Fact]
    public async Task WarmupFailureIsExposedAndLeavesNoReadyState()
    {
        FakePipeline pipeline = new() { WarmupFailure = new InvalidDataException("missing model") };
        await using AppRuntime runtime = new(pipeline);

        await runtime.StartAsync(default);

        Assert.Equal(RuntimeState.Failed, runtime.State);
        Assert.False(runtime.IsReady);
        Assert.IsType<InvalidDataException>(runtime.Failure);
        Assert.Contains("missing model", runtime.Failure!.Message);
    }

    [Fact]
    public async Task StopDuringLoadCancelsWarmupAndWaitsBeforeDispose()
    {
        FakePipeline pipeline = new();
        await using AppRuntime runtime = new(pipeline);

        Task starting = runtime.StartAsync(default);
        await pipeline.WarmupStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task stopping = runtime.StopAsync(default);

        Assert.Equal(RuntimeState.Stopping, runtime.State);
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(pipeline.WarmupCancelled);
        Assert.True(pipeline.WarmupExited);
        Assert.True(pipeline.Disposed);
        Assert.True(pipeline.DisposeAfterWarmupExit);
        Assert.Equal(RuntimeState.Stopped, runtime.State);
        await starting;
    }

    [Fact]
    public async Task StopWaitsForTheActivePipelineOperationBeforeDispose()
    {
        FakePipeline pipeline = new();
        await using AppRuntime runtime = new(pipeline);
        Task starting = runtime.StartAsync(default);
        pipeline.ReleaseWarmup();
        await starting;

        Task processing = pipeline.ProcessAsync(new(new byte[4], "chat"), default);
        await pipeline.ProcessStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task stopping = runtime.StopAsync(default);

        await Task.Delay(50);
        Assert.False(stopping.IsCompleted);
        Assert.False(pipeline.Disposed);

        pipeline.ReleaseProcess();
        await processing;
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(pipeline.Disposed);
        Assert.Equal(RuntimeState.Stopped, runtime.State);
    }

    [Fact]
    public async Task StopIsIdempotentAndDisposesOnce()
    {
        FakePipeline pipeline = new();
        AppRuntime runtime = new(pipeline);
        Task starting = runtime.StartAsync(default);
        pipeline.ReleaseWarmup();
        await starting;

        await Task.WhenAll(runtime.StopAsync(default), runtime.StopAsync(default));

        Assert.Equal(1, pipeline.DisposeCalls);
        Assert.Equal(RuntimeState.Stopped, runtime.State);
    }

    private sealed class FakePipeline : IInferencePipeline
    {
        private static readonly EngineIdentity Identity = new("fake", "r", "h", "v", "vulkan", "F16", "fake");
        private readonly TaskCompletionSource _releaseWarmup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseProcess = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task<PipelineResult>? _process;
        public TaskCompletionSource WarmupStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ProcessStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? WarmupFailure { get; init; }
        public int WarmupCalls;
        public int DisposeCalls;
        public bool WarmupCancelled;
        public bool WarmupExited;
        public bool Disposed;
        public bool DisposeAfterWarmupExit;

        public async Task WarmUpAsync(CancellationToken ct)
        {
            WarmupCalls++;
            WarmupStarted.TrySetResult();
            try
            {
                if (WarmupFailure is not null) throw WarmupFailure;
                await _releaseWarmup.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                WarmupCancelled = true;
                throw;
            }
            finally { WarmupExited = true; }
        }

        public Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct)
        {
            _process = ProcessCoreAsync();
            return _process;
        }

        private async Task<PipelineResult> ProcessCoreAsync()
        {
            ProcessStarted.TrySetResult();
            await _releaseProcess.Task;
            return new("ciao", "Ciao.", 1, 1, 2, [], Identity, Identity);
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCalls++;
            DisposeAfterWarmupExit = WarmupExited;
            if (_process is not null) await _process;
            Disposed = true;
        }

        public void ReleaseWarmup() => _releaseWarmup.TrySetResult();
        public void ReleaseProcess() => _releaseProcess.TrySetResult();
    }
}
