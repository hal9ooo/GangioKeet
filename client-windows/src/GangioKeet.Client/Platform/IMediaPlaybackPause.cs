namespace GangioKeet.Client.Platform;

/// <summary>
/// Mette in pausa e riprende le sessioni multimediali di Windows durante la
/// dettatura. La funzionalità è sempre attiva e best-effort: un errore non
/// deve mai bloccare né far fallire la dettatura.
/// </summary>
public interface IMediaPlaybackPause
{
    Task PauseAsync(CancellationToken ct);
    Task ResumeAsync();
    bool HasPausedSessions { get; }
}

internal sealed class NullMediaPlaybackPause : IMediaPlaybackPause
{
    public Task PauseAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ResumeAsync() => Task.CompletedTask;
    public bool HasPausedSessions => false;
}
