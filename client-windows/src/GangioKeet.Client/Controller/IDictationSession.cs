using GangioKeet.Client.Protocol;

namespace GangioKeet.Client.Controller;

/// <summary>
/// Contratto della sessione usato dal controller. <see cref="Protocol.DictationSession"/> lo
/// implementa; l'interfaccia esiste per poter sostituire la sessione nei test del controller
/// senza un server reale.
/// </summary>
public interface IDictationSession : IAsyncDisposable
{
    Task<SessionConnectOutcome> ConnectAndStartAsync(
        Uri uri,
        StartMessage start,
        string? token,
        TimeSpan connectTimeout,
        CancellationToken ct);

    bool TryEnqueueAudio(ReadOnlyMemory<byte> ownedPcm);

    Task<DictationOutcome> Completion { get; }

    Task<DictationOutcome> FinishAsync(CancellationToken ct);

    Task AbortAsync(CancellationToken ct);
}
