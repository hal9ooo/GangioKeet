using GangioKeet.Client.Protocol;

namespace GangioKeet.Client.Controller;

public interface IDictationBackend
{
    /// <summary>
    /// False finché i modelli locali non hanno completato il warm-up. Il
    /// controller controlla questo valore prima di aprire il microfono.
    /// </summary>
    bool IsReady => true;

    IActiveDictationTurn CreateTurn(StartMessage start);
    Task<DictationOutcome> RetryAsync(ReadOnlyMemory<byte> pcm, StartMessage start, CancellationToken ct);
}
