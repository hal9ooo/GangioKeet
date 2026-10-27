using GangioKeet.Client.Protocol;

namespace GangioKeet.Client.Controller;

public interface IDictationBackend
{
    IActiveDictationTurn CreateTurn(StartMessage start);
    Task<DictationOutcome> RetryAsync(ReadOnlyMemory<byte> pcm, StartMessage start, CancellationToken ct);
}
