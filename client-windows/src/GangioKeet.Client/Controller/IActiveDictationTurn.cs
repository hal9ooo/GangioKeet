using GangioKeet.Client.Protocol;

namespace GangioKeet.Client.Controller;

public sealed record TurnStartOutcome(bool Ready, DictationError? Error);

public interface IActiveDictationTurn : IAsyncDisposable
{
    string BackendName { get; }
    Task<TurnStartOutcome> StartAsync(CancellationToken ct);
    bool TryEnqueueAudio(ReadOnlyMemory<byte> ownedPcm);
    Task<DictationOutcome> Completion { get; }
    Task<DictationOutcome> FinishAsync(CancellationToken ct);
    Task AbortAsync(CancellationToken ct);
}
