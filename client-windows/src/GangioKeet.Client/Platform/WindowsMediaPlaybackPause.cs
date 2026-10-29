using System.Diagnostics;
using Windows.Media.Control;

namespace GangioKeet.Client.Platform;

/// <summary>
/// Implementazione Windows basata su GSMTC. Il budget della pausa è interno:
/// allo scadere la cattura audio può partire anche se qualche app non risponde.
/// </summary>
internal sealed class WindowsMediaPlaybackPause : IMediaPlaybackPause
{
    private static readonly TimeSpan DefaultPauseBudget = TimeSpan.FromMilliseconds(750);

    private readonly IMediaSessionManager _sessions;
    private readonly FileLogger? _logger;
    private readonly TimeSpan _pauseBudget;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<IMediaSessionControl> _paused = [];
    private volatile CancellationTokenSource? _pauseCts;
    private volatile bool _resumeRequested;

    public WindowsMediaPlaybackPause(
        IMediaSessionManager sessions,
        FileLogger? logger = null,
        TimeSpan? pauseBudget = null)
    {
        _sessions = sessions;
        _logger = logger;
        _pauseBudget = pauseBudget ?? DefaultPauseBudget;
    }

    public bool HasPausedSessions
    {
        get
        {
            lock (_paused)
            {
                return _paused.Count > 0;
            }
        }
    }

    public async Task PauseAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_resumeRequested)
            {
                _resumeRequested = false;
                return;
            }

            lock (_paused)
            {
                if (_paused.Count > 0) return;
            }

            using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(_pauseBudget);
            _pauseCts = budget;

            Stopwatch elapsed = Stopwatch.StartNew();
            try
            {
                IReadOnlyList<IMediaSessionControl> sessions =
                    await _sessions.GetSessionsAsync(budget.Token).ConfigureAwait(false);

                IMediaSessionControl[] playing = sessions
                    .Where(session => session.PlaybackStatus == MediaSessionPlaybackStatus.Playing)
                    .ToArray();

                await Task.WhenAll(playing.Select(session => PauseOneAsync(session, budget.Token)))
                    .ConfigureAwait(false);

                int count;
                lock (_paused)
                {
                    count = _paused.Count;
                }

                if (count > 0)
                {
                    _logger?.Write("media_pause", $"{count} session(s) in {elapsed.ElapsedMilliseconds} ms");
                }
            }
            catch (OperationCanceledException)
            {
                _logger?.Write("media_pause", $"budget scaduto dopo {elapsed.ElapsedMilliseconds} ms");
            }
            catch (Exception error)
            {
                _logger?.Write("media_pause_failed", error.GetType().Name);
            }
            finally
            {
                _pauseCts = null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ResumeAsync()
    {
        _resumeRequested = true;
        try
        {
            _pauseCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // La pausa ha appena rilasciato il CTS: non c'è nulla da annullare.
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            IMediaSessionControl[] snapshot;
            lock (_paused)
            {
                snapshot = [.. _paused];
                _paused.Clear();
            }

            HashSet<object>? alive = null;
            try
            {
                using CancellationTokenSource probe = new(TimeSpan.FromMilliseconds(250));
                IReadOnlyList<IMediaSessionControl> current =
                    await _sessions.GetSessionsAsync(probe.Token).ConfigureAwait(false);
                alive = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (IMediaSessionControl session in current)
                {
                    alive.Add(session.NativeIdentity);
                }
            }
            catch (Exception)
            {
                // Se il manager non risponde, proviamo comunque le sessioni registrate.
            }

            int resumed = 0;
            foreach (IMediaSessionControl session in snapshot)
            {
                if (alive is not null && !alive.Contains(session.NativeIdentity)) continue;

                try
                {
                    if (session.PlaybackStatus == MediaSessionPlaybackStatus.Paused
                        && await session.TryPlayAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        resumed++;
                    }
                }
                catch (Exception)
                {
                    // Una sessione chiusa o che rifiuta il play non deve fermare le altre.
                }
            }

            if (resumed > 0)
            {
                _logger?.Write("media_resume", $"{resumed} session(s)");
            }
        }
        catch (Exception error)
        {
            _logger?.Write("media_resume_failed", error.GetType().Name);
        }
        finally
        {
            _resumeRequested = false;
            _gate.Release();
        }
    }

    private async Task PauseOneAsync(IMediaSessionControl session, CancellationToken ct)
    {
        try
        {
            if (!await session.TryPauseAsync(ct).ConfigureAwait(false)) return;

            lock (_paused)
            {
                _paused.Add(session);
            }
        }
        catch (OperationCanceledException)
        {
            // La pausa può essere stata applicata anche se l'attesa è stata annullata:
            // registrala per non lasciare il media bloccato in pausa.
            try
            {
                if (session.PlaybackStatus == MediaSessionPlaybackStatus.Paused)
                {
                    lock (_paused)
                    {
                        _paused.Add(session);
                    }
                }
            }
            catch (Exception)
            {
                // Sessione chiusa durante l'annullamento.
            }
        }
        catch (Exception)
        {
            // Best effort: una sessione che non risponde non blocca le altre.
        }
    }
}

internal sealed class WindowsMediaSessionManager : IMediaSessionManager
{
    public async Task<IReadOnlyList<IMediaSessionControl>> GetSessionsAsync(CancellationToken ct)
    {
        GlobalSystemMediaTransportControlsSessionManager manager =
            await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(ct)
                .ConfigureAwait(false);

        return manager.GetSessions()
            .Select(session => (IMediaSessionControl)new WindowsMediaSessionControl(session))
            .ToArray();
    }
}

internal sealed class WindowsMediaSessionControl(GlobalSystemMediaTransportControlsSession session)
    : IMediaSessionControl
{
    public string SourceAppUserModelId => session.SourceAppUserModelId;
    public object NativeIdentity => session;

    public MediaSessionPlaybackStatus PlaybackStatus => session.GetPlaybackInfo().PlaybackStatus switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaSessionPlaybackStatus.Closed,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaSessionPlaybackStatus.Opened,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaSessionPlaybackStatus.Changing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaSessionPlaybackStatus.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaSessionPlaybackStatus.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaSessionPlaybackStatus.Paused,
        _ => MediaSessionPlaybackStatus.Closed,
    };

    public async Task<bool> TryPauseAsync(CancellationToken ct) =>
        await session.TryPauseAsync().AsTask(ct).ConfigureAwait(false);

    public async Task<bool> TryPlayAsync(CancellationToken ct) =>
        await session.TryPlayAsync().AsTask(ct).ConfigureAwait(false);
}
