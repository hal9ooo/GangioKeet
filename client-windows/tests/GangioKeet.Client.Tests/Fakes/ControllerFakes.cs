using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GangioKeet.Client.Audio;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Context;
using GangioKeet.Client.Controller;
using GangioKeet.Client.Platform;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recovery;

namespace GangioKeet.Client.Tests.Fakes;

internal sealed class FakeCapture : IAudioCapture
{
    public event EventHandler? Ready;
    public event EventHandler<ReadOnlyMemory<byte>>? ChunkReady;
    public event EventHandler<double>? LevelChanged;
    public event EventHandler<CaptureFailure>? Failed;

    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public string? LastDeviceId { get; private set; }
    public Action<string>? Record { get; set; }

    public List<byte[]> EmittedChunks { get; } = [];
    public AudioSummary? StopResult { get; set; } = new(0, false, 0);

    public void EmitReady() => Ready?.Invoke(this, EventArgs.Empty);
    public void EmitChunk(byte[] chunk) { EmittedChunks.Add(chunk); ChunkReady?.Invoke(this, chunk); }
    public void EmitLevel(double level) => LevelChanged?.Invoke(this, level);
    public void EmitFailed(CaptureFailure failure) => Failed?.Invoke(this, failure);

    public Task StartAsync(string? deviceId, CancellationToken ct)
    {
        StartCalls++;
        LastDeviceId = deviceId;
        Record?.Invoke("capture_start");
        return Task.CompletedTask;
    }

    public Task<AudioSummary> StopAsync(CancellationToken ct)
    {
        StopCalls++;
        Record?.Invoke("capture_stop");
        return Task.FromResult(StopResult ?? new AudioSummary(0, false, 0));
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeMediaPlaybackPause(Action<string>? record = null) : IMediaPlaybackPause
{
    public int PauseCalls { get; private set; }
    public int ResumeCalls { get; private set; }
    public TaskCompletionSource? PauseGate { get; set; }
    public Exception? PauseException { get; set; }
    public bool HasPausedSessions => PauseCalls > ResumeCalls;

    public async Task PauseAsync(CancellationToken ct)
    {
        PauseCalls++;
        if (PauseGate is not null) await PauseGate.Task.WaitAsync(ct);
        if (PauseException is not null) throw PauseException;
        record?.Invoke("media_pause");
    }

    public Task ResumeAsync()
    {
        ResumeCalls++;
        record?.Invoke("media_resume");
        return Task.CompletedTask;
    }
}

internal sealed class FakeDictationBackend : IDictationBackend
{
    public int RetryCalls { get; private set; }
    public ReadOnlyMemory<byte> LastRetryPcm { get; private set; }
    public StartMessage? LastRetryStart { get; private set; }
    public DictationOutcome RetryOutcome { get; set; } = new(
        DictationStatus.Result,
        new ResultMessage("raw", "testo ritentato", false, new DictationMetrics(1, 2, 3), []),
        null);

    public IActiveDictationTurn CreateTurn(StartMessage start) =>
        throw new NotSupportedException("This fake is used only for retry routing.");

    public Task<DictationOutcome> RetryAsync(
        ReadOnlyMemory<byte> pcm,
        StartMessage start,
        CancellationToken ct)
    {
        RetryCalls++;
        LastRetryPcm = pcm;
        LastRetryStart = start;
        return Task.FromResult(RetryOutcome);
    }
}

internal sealed class FakeRecoveryStore
{    public RecoveryItem? Current { get; set; }
    public int OfferCalls { get; private set; }
    public int DiscardCalls { get; private set; }
    public bool RemoveResult { get; set; } = true;

    public void Offer(RecoveryItem item) { OfferCalls++; Current = item; }
    public RecoveryItem? GetCurrent() => Current;
    public void Discard() { DiscardCalls++; Current = null; }
    public bool RemoveIfCurrent(Guid id) => RemoveResult;
}

internal sealed class FakePillView : IPillView
{
    public PillState? LastState { get; private set; }
    public string? LastMessage { get; private set; }
    public DictationProfile? LastProfile { get; private set; }
    public double LastLevel { get; private set; }
    public int HideCalls { get; private set; }
    public int ShowCalls { get; private set; }
    public int SetLevelCalls { get; private set; }

    public void Show(PillState state, string? message, DictationProfile? profile)
    {
        ShowCalls++;
        LastState = state;
        LastMessage = message;
        LastProfile = profile;
    }

    public void Hide() { HideCalls++; }
    public void SetLevel(double level) { SetLevelCalls++; LastLevel = level; }
}

internal sealed class FakeSoundPlayer : ISoundPlayer
{
    public List<SoundKind> PlayedSounds { get; } = [];
    public void Play(SoundKind kind) => PlayedSounds.Add(kind);
}
