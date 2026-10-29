namespace GangioKeet.Client.Platform;

internal enum MediaSessionPlaybackStatus
{
    Closed,
    Opened,
    Changing,
    Stopped,
    Playing,
    Paused,
}

/// <summary>
/// Adapter testabile sopra una sessione GSMTC. L'implementazione reale è
/// <see cref="WindowsMediaSessionControl"/>.
/// </summary>
internal interface IMediaSessionControl
{
    string SourceAppUserModelId { get; }
    object NativeIdentity { get; }
    MediaSessionPlaybackStatus PlaybackStatus { get; }
    Task<bool> TryPauseAsync(CancellationToken ct);
    Task<bool> TryPlayAsync(CancellationToken ct);
}

internal interface IMediaSessionManager
{
    Task<IReadOnlyList<IMediaSessionControl>> GetSessionsAsync(CancellationToken ct);
}
