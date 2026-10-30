using GangioKeet.Inference;

namespace GangioKeet.Client.Platform;

/// <summary>
/// Pipeline placeholder used when model composition fails at startup. It keeps
/// the tray and settings alive in a NotReady state until a retry succeeds.
/// </summary>
public sealed class FailedInferencePipeline(Exception error) : IInferencePipeline
{
    private readonly Exception _error = error ?? throw new ArgumentNullException(nameof(error));

    public Task WarmUpAsync(CancellationToken ct) => Task.FromException(_error);

    public Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct) =>
        Task.FromException<PipelineResult>(_error);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
