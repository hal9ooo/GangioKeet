using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GangioKeet.Client.Audio;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Context;
using GangioKeet.Client.Controller;
using GangioKeet.Client.Paste;
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

    public List<byte[]> EmittedChunks { get; } = [];
    public AudioSummary? StopResult { get; set; } = new(0, false, 0);

    public void EmitReady() => Ready?.Invoke(this, EventArgs.Empty);
    public void EmitChunk(byte[] chunk) { EmittedChunks.Add(chunk); ChunkReady?.Invoke(this, chunk); }
    public void EmitLevel(double level) => LevelChanged?.Invoke(this, level);
    public void EmitFailed(CaptureFailure failure) => Failed?.Invoke(this, failure);

    public Task StartAsync(string? deviceId, CancellationToken ct)
    {
        StartCalls++;
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

internal sealed class FakePasteService
{
    public PasteOutcome? Result { get; set; } = new(PasteStatus.Pasted, false);
    public int PasteCalls { get; private set; }
    public string? LastText { get; private set; }
    public ForegroundWindowInfo? LastTarget { get; private set; }

    public Task<PasteOutcome> PasteAsync(string text, ForegroundWindowInfo target, CancellationToken ct)
    {
        PasteCalls++;
        LastText = text;
        LastTarget = target;
        return Task.FromResult(Result ?? new PasteOutcome(PasteStatus.Pasted, false));
    }
}

internal sealed class FakeRecoveryStore
{
    public RecoveryItem? Current { get; set; }
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


