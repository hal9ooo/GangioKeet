using GangioKeet.Client.Protocol;

namespace GangioKeet.Client.Controller;

public interface IDictationBackend
{
    /// <summary>
    /// False finché i modelli locali non hanno completato il warm-up. Il
    /// controller controlla questo valore prima di aprire il microfono.
    /// </summary>
    bool IsReady => true;

    /// <summary>True while the local pipeline is warming up and early audio can be buffered.</summary>
    bool AllowsEarlyAudio => false;

    /// <summary>True when the local runtime has failed and cannot become ready.</summary>
    bool IsFailed => false;

    IActiveDictationTurn CreateTurn(StartMessage start);
    Task<DictationOutcome> RetryAsync(ReadOnlyMemory<byte> pcm, StartMessage start, CancellationToken ct);
}
