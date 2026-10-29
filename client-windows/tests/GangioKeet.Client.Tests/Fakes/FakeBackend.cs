using GangioKeet.Client.Controller;
using GangioKeet.Client.Protocol;

namespace GangioKeet.Client.Tests.Fakes;

internal sealed class FakeBackend : IDictationBackend
{
    private readonly Queue<FakeTurn> _queued = [];

    public bool IsReady { get; set; } = true;
    public List<FakeTurn> Turns { get; } = [];

    public FakeTurn EnqueueTurn()
    {
        FakeTurn turn = new();
        _queued.Enqueue(turn);
        return turn;
    }

    public IActiveDictationTurn CreateTurn(StartMessage start)
    {
        FakeTurn turn = _queued.Count > 0 ? _queued.Dequeue() : new FakeTurn();
        Turns.Add(turn);
        return turn;
    }

    public Task<DictationOutcome> RetryAsync(
        ReadOnlyMemory<byte> pcm,
        StartMessage start,
        CancellationToken ct) =>
        Task.FromResult(new DictationOutcome(
            DictationStatus.Result,
            new ResultMessage("raw", "retry", false, new DictationMetrics(0, 0, 0), []),
            null));
}

internal sealed class FakeTurn : IActiveDictationTurn
{
    private readonly TaskCompletionSource<DictationOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string BackendName => "fake";
    public TaskCompletionSource? StartGate { get; set; }
    public bool StartReady { get; set; } = true;
    public string StartErrorCode { get; set; } = "start_failed";
    public bool EnqueueResult { get; set; } = true;
    public List<byte[]> Enqueued { get; } = [];
    public int FinishCalls { get; private set; }
    public int AbortCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public bool Aborted { get; private set; }

    public async Task<TurnStartOutcome> StartAsync(CancellationToken ct)
    {
        if (StartGate is not null) await StartGate.Task.WaitAsync(ct);
        return new TurnStartOutcome(
            StartReady,
            StartReady ? null : new DictationError(StartErrorCode, "fake", false));
    }

    public bool TryEnqueueAudio(ReadOnlyMemory<byte> ownedPcm)
    {
        if (!EnqueueResult) return false;
        Enqueued.Add(ownedPcm.ToArray());
        return true;
    }

    public Task<DictationOutcome> Completion => _completion.Task;

    public Task<DictationOutcome> FinishAsync(CancellationToken ct)
    {
        FinishCalls++;
        return _completion.Task.WaitAsync(ct);
    }

    public Task AbortAsync(CancellationToken ct)
    {
        AbortCalls++;
        Aborted = true;
        _completion.TrySetResult(new DictationOutcome(
            DictationStatus.Aborted, null, new DictationError("aborted", "aborted", false)));
        return Task.CompletedTask;
    }

    public void PublishResult(string text, IReadOnlyList<string>? warnings = null)
    {
        _completion.TrySetResult(new DictationOutcome(
            DictationStatus.Result,
            new ResultMessage("raw", text, false, new DictationMetrics(1, 2, 3), warnings ?? []),
            null));
    }

    public void PublishError(string code, string message)
    {
        _completion.TrySetResult(new DictationOutcome(
            DictationStatus.Error, null, new DictationError(code, message, false)));
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
