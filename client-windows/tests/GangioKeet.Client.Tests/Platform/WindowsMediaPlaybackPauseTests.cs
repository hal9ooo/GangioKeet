using GangioKeet.Client.Platform;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

public sealed class WindowsMediaPlaybackPauseTests
{
    [Fact]
    public async Task PauseAsync_PausesOnlyPlayingSessionsAndResumePlaysOnlyThose()
    {
        FakeSessionControl playing = new("spotify.exe", MediaSessionPlaybackStatus.Playing);
        FakeSessionControl paused = new("chrome.exe", MediaSessionPlaybackStatus.Paused);
        FakeSessionControl unsupported = new("vlc.exe", MediaSessionPlaybackStatus.Playing) { PauseResult = false };
        WindowsMediaPlaybackPause sut = Create(playing, paused, unsupported);

        await sut.PauseAsync(CancellationToken.None);

        Assert.Equal(1, playing.PauseCalls);
        Assert.Equal(0, paused.PauseCalls);
        Assert.Equal(1, unsupported.PauseCalls);
        Assert.True(sut.HasPausedSessions);

        await sut.ResumeAsync();

        Assert.Equal(1, playing.PlayCalls);
        Assert.Equal(0, paused.PlayCalls);
        Assert.Equal(0, unsupported.PlayCalls);
        Assert.False(sut.HasPausedSessions);
    }

    [Fact]
    public async Task PauseAsync_SkipsSessionsWhosePauseFails()
    {
        FakeSessionControl session = new("vlc.exe", MediaSessionPlaybackStatus.Playing) { PauseResult = false };
        WindowsMediaPlaybackPause sut = Create(session);

        await sut.PauseAsync(CancellationToken.None);
        await sut.ResumeAsync();

        Assert.Equal(1, session.PauseCalls);
        Assert.Equal(0, session.PlayCalls);
        Assert.False(sut.HasPausedSessions);
    }

    [Fact]
    public async Task PauseAsync_TwoSessionsOfSameApp_AreResumedIndependently()
    {
        FakeSessionControl first = new("chrome.exe", MediaSessionPlaybackStatus.Playing);
        FakeSessionControl second = new("chrome.exe", MediaSessionPlaybackStatus.Playing);
        WindowsMediaPlaybackPause sut = Create(first, second);

        await sut.PauseAsync(CancellationToken.None);
        await sut.ResumeAsync();

        Assert.Equal(1, first.PlayCalls);
        Assert.Equal(1, second.PlayCalls);
    }

    [Fact]
    public async Task ResumeAsync_SkipsSessionThatIsNoLongerPaused()
    {
        FakeSessionControl session = new("spotify.exe", MediaSessionPlaybackStatus.Playing);
        WindowsMediaPlaybackPause sut = Create(session);
        await sut.PauseAsync(CancellationToken.None);
        session.Status = MediaSessionPlaybackStatus.Playing;

        await sut.ResumeAsync();

        Assert.Equal(0, session.PlayCalls);
        Assert.False(sut.HasPausedSessions);
    }

    [Fact]
    public async Task ResumeAsync_IsIdempotent()
    {
        FakeSessionControl session = new("spotify.exe", MediaSessionPlaybackStatus.Playing);
        WindowsMediaPlaybackPause sut = Create(session);
        await sut.PauseAsync(CancellationToken.None);
        await sut.ResumeAsync();
        await sut.ResumeAsync();

        Assert.Equal(1, session.PlayCalls);
    }

    [Fact]
    public async Task PauseAndResumeConcurrently_DoNotLeaveSessionsPaused()
    {
        FakeSessionControl session = new("spotify.exe", MediaSessionPlaybackStatus.Playing);
        FakeSessionManager manager = new(session) { Gate = new TaskCompletionSource() };
        WindowsMediaPlaybackPause sut = new(manager, pauseBudget: TimeSpan.FromSeconds(10));

        Task pause = sut.PauseAsync(CancellationToken.None);
        await Task.Delay(20);
        Task firstResume = sut.ResumeAsync();
        manager.Gate.TrySetResult();
        await pause;
        Task secondResume = sut.ResumeAsync();
        await Task.WhenAll(firstResume, secondResume);

        Assert.False(sut.HasPausedSessions);
        if (session.PauseCalls == 1) Assert.Equal(1, session.PlayCalls);
    }

    [Fact]
    public async Task PauseAsync_ReturnsAtBudgetWhenManagerHangs()
    {
        FakeSessionManager manager = new() { Gate = new TaskCompletionSource() };
        WindowsMediaPlaybackPause sut = new(manager, pauseBudget: TimeSpan.FromMilliseconds(50));

        Task pause = sut.PauseAsync(CancellationToken.None);
        await Task.WhenAny(pause, Task.Delay(2_000));

        Assert.True(pause.IsCompleted);
        Assert.False(sut.HasPausedSessions);
        manager.Gate.TrySetResult();
    }

    [Fact]
    public async Task ResumeAsync_CancelsInFlightPauseWithoutLeavingMediaPaused()
    {
        FakeSessionControl session = new("spotify.exe", MediaSessionPlaybackStatus.Playing);
        FakeSessionManager manager = new(session) { Gate = new TaskCompletionSource() };
        WindowsMediaPlaybackPause sut = new(manager, pauseBudget: TimeSpan.FromSeconds(10));

        Task pause = sut.PauseAsync(CancellationToken.None);
        await Task.Delay(20);
        Task resume = sut.ResumeAsync();
        manager.Gate.TrySetResult();
        await resume;
        await pause;

        Assert.False(sut.HasPausedSessions);
        if (session.PauseCalls == 1) Assert.Equal(1, session.PlayCalls);
    }

    [Fact]
    public async Task PauseAsync_CancelledAfterSessionPaused_StillRegistersForResume()
    {
        FakeSessionControl session = new("spotify.exe", MediaSessionPlaybackStatus.Playing)
        {
            PauseGate = new TaskCompletionSource(),
            PauseDespiteCancellation = true,
        };
        WindowsMediaPlaybackPause sut = new(
            new FakeSessionManager(session),
            pauseBudget: TimeSpan.FromMilliseconds(50));

        await sut.PauseAsync(CancellationToken.None);

        Assert.True(sut.HasPausedSessions);
        await sut.ResumeAsync();
        Assert.Equal(1, session.PlayCalls);
    }

    [Fact]
    public async Task ResumeAsync_SkipsSessionNoLongerPresentInManager()
    {
        FakeSessionControl session = new("spotify.exe", MediaSessionPlaybackStatus.Playing);
        FakeSessionManager manager = new(session);
        WindowsMediaPlaybackPause sut = new(manager, pauseBudget: TimeSpan.FromMilliseconds(500));

        await sut.PauseAsync(CancellationToken.None);
        manager.Hidden = true;
        await sut.ResumeAsync();

        Assert.Equal(0, session.PlayCalls);
        Assert.False(sut.HasPausedSessions);
    }

    private static WindowsMediaPlaybackPause Create(params FakeSessionControl[] sessions) =>
        new(new FakeSessionManager(sessions), pauseBudget: TimeSpan.FromMilliseconds(500));

    private sealed class FakeSessionManager(params FakeSessionControl[] sessions) : IMediaSessionManager
    {
        public TaskCompletionSource? Gate { get; set; }
        public bool Hidden { get; set; }

        public async Task<IReadOnlyList<IMediaSessionControl>> GetSessionsAsync(CancellationToken ct)
        {
            if (Gate is not null) await Gate.Task.WaitAsync(ct);
            return Hidden ? [] : sessions;
        }
    }

    private sealed class FakeSessionControl(string aumid, MediaSessionPlaybackStatus status) : IMediaSessionControl
    {
        public string SourceAppUserModelId { get; } = aumid;
        public object NativeIdentity => this;
        public MediaSessionPlaybackStatus Status { get; set; } = status;
        public MediaSessionPlaybackStatus PlaybackStatus => Status;
        public bool PauseResult { get; set; } = true;
        public TaskCompletionSource? PauseGate { get; set; }
        public bool PauseDespiteCancellation { get; set; }
        public int PauseCalls { get; private set; }
        public int PlayCalls { get; private set; }

        public async Task<bool> TryPauseAsync(CancellationToken ct)
        {
            PauseCalls++;
            if (PauseGate is not null)
            {
                try
                {
                    await PauseGate.Task.WaitAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    if (PauseDespiteCancellation) Status = MediaSessionPlaybackStatus.Paused;
                    throw;
                }
            }

            if (PauseResult) Status = MediaSessionPlaybackStatus.Paused;
            return PauseResult;
        }

        public Task<bool> TryPlayAsync(CancellationToken ct)
        {
            PlayCalls++;
            Status = MediaSessionPlaybackStatus.Playing;
            return Task.FromResult(true);
        }
    }
}
