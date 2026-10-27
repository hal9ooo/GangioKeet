using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GangioKeet.Client.Audio;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Context;
using GangioKeet.Client.Controller;
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
        return Task.CompletedTask;
    }

    public Task<AudioSummary> StopAsync(CancellationToken ct)
    {
        StopCalls++;
        return Task.FromResult(StopResult ?? new AudioSummary(0, false, 0));
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeSession : IDictationSession
{
    private readonly TaskCompletionSource<SessionConnectOutcome> _connect =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<DictationOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Connected { get; set; } = true;
    public string ConnectFailureCode { get; set; } = "connect_failed";
    public TaskCompletionSource? ConnectGate { get; set; }

    public List<byte[]> Enqueued { get; } = [];
    public bool EnqueueResult { get; set; } = true;
    public int FinishCalls { get; private set; }
    public int AbortCalls { get; private set; }
    public int DisposeCalls { get; private set; }
    public bool Aborted { get; private set; }
    public Uri? LastUri { get; private set; }
    public string? LastToken { get; private set; }

    public async Task<SessionConnectOutcome> ConnectAndStartAsync(
        Uri uri, StartMessage start, string? token, TimeSpan connectTimeout, CancellationToken ct)
    {
        LastUri = uri;
        LastToken = token;

        if (ConnectGate is not null)
        {
            await ConnectGate.Task.WaitAsync(connectTimeout, ct);
        }

        _connect.TrySetResult(Connected
            ? new SessionConnectOutcome(true, null)
            : new SessionConnectOutcome(false, new ConnectFailure(ConnectFailureCode, "fake", null)));
        return await _connect.Task.WaitAsync(connectTimeout, ct);
    }

    public bool TryEnqueueAudio(ReadOnlyMemory<byte> ownedPcm)
    {
        if (!EnqueueResult) return false;
        Enqueued.Add(ownedPcm.ToArray());
        return true;
    }

    public Task<DictationOutcome> Completion => _completion.Task;

    public Task<DictationOutcome> FinishAsync(CancellationToken ct)
    {
        FinishCalls++;
        return _completion.Task.WaitAsync(ct);
    }

    public Task AbortAsync(CancellationToken ct)
    {
        AbortCalls++;
        Aborted = true;
        _completion.TrySetResult(new DictationOutcome(
            DictationStatus.Aborted, null, new DictationError("aborted", "aborted", false)));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    public void PublishResult(string text, IReadOnlyList<string>? warnings = null)
    {
        _completion.TrySetResult(new DictationOutcome(
            DictationStatus.Result,
            new ResultMessage("raw", text, false, new DictationMetrics(1, 2, 3), warnings ?? []),
            null));
    }

    public void PublishError(string code, string message)
    {
        _completion.TrySetResult(new DictationOutcome(
            DictationStatus.Error, null, new DictationError(code, message, true)));
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

internal sealed class MutableResolver : IEndpointResolver
{
    private readonly object _gate = new();
    private EndpointConfig _active;

    public MutableResolver(EndpointConfig active)
    {
        _active = active;
    }

    public EndpointConfig Active
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    public void Set(EndpointConfig active)
    {
        lock (_gate)
        {
            _active = active;
        }
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
