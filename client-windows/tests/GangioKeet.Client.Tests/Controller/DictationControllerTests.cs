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
using GangioKeet.Client.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GangioKeet.Client.Tests.Controller;

public sealed class DictationControllerTests
{
    private static readonly ClientConfig DefaultConfig = new();
    private static readonly PasteConfig FastPasteConfig = new() { ClipboardRestoreDelayMs = 0 };
    private static readonly ForegroundWindowInfo Window = new(0x1234, 42, "notepad.exe", "Senza titolo");

    [Fact]
    public void Start_ElevatedTarget_ShowsErrorWithoutCapture()
    {
        Harness h = new(elevationBlocked: true);

        h.Controller.Start();

        Assert.Equal(ClientState.Idle, h.Controller.State);
        Assert.Equal(PillState.Error, h.Pill.LastState);
        Assert.Equal(0, h.Capture.StartCalls);
    }

    [Fact]
    public void DoubleStart_SecondCallIsIgnored()
    {
        Harness h = new();
        h.Sessions.Enqueue(new FakeSession());

        h.Controller.Start();
        h.Controller.Start();

        Assert.Equal(1, h.Capture.StartCalls);
    }

    [Fact]
    public async Task ConnectFailure_StopsCaptureAndOffersRecovery()
    {
        Harness h = new();
        FakeSession session = new() { Connected = false, ConnectGate = new() };
        h.Sessions.Enqueue(session);

        h.Controller.Start();
        await WaitUntilAsync(() => h.Capture.StartCalls == 1);
        h.Capture.EmitChunk(new byte[] { 1, 2, 3, 4 });
        session.ConnectGate!.SetResult();

        await WaitUntilAsync(() => h.Recovery.GetCurrent() is not null);

        Assert.Equal(1, h.Capture.StopCalls);
        Assert.Equal(ClientState.Idle, h.Controller.State);
        Assert.Equal("connect_failed", h.Recovery.GetCurrent()!.FailureCode);
        Assert.NotNull(h.Recovery.GetCurrent()!.Pcm);
        Assert.Empty(session.Enqueued);
    }

    [Fact]
    public async Task ChunksDuringConnecting_AreFlushedBeforeLiveChunks()
    {
        Harness h = new();
        FakeSession session = new() { ConnectGate = new() };
        h.Sessions.Enqueue(session);

        h.Controller.Start();
        await WaitUntilAsync(() => h.Capture.StartCalls == 1);

        h.Capture.EmitChunk(new byte[] { 1 });
        h.Capture.EmitChunk(new byte[] { 2 });

        session.ConnectGate!.SetResult();
        await WaitUntilAsync(() => session.Enqueued.Count == 2);

        h.Capture.EmitChunk(new byte[] { 3 });
        await WaitUntilAsync(() => session.Enqueued.Count == 3);

        Assert.Equal(new byte[] { 1 }, session.Enqueued[0]);
        Assert.Equal(new byte[] { 2 }, session.Enqueued[1]);
        Assert.Equal(new byte[] { 3 }, session.Enqueued[2]);
    }

    [Fact]
    public async Task EnqueueFailure_StopsAndOffersBackpressureRecovery()
    {
        Harness h = new();
        FakeSession session = new() { EnqueueResult = false };
        h.Sessions.Enqueue(session);

        h.Controller.Start();
        await WaitUntilAsync(() => h.Controller.State == ClientState.Recording);

        h.Capture.EmitChunk(new byte[] { 9, 9 });

        await WaitUntilAsync(() => h.Recovery.GetCurrent() is not null);

        Assert.Equal("backpressure", h.Recovery.GetCurrent()!.FailureCode);
        Assert.True(h.Capture.StopCalls >= 1);
        Assert.Equal(ClientState.Idle, h.Controller.State);
    }

    [Fact]
    public async Task ErrorOutcome_ReturnsToIdleAndAllowsNextStart()
    {
        Harness h = new();
        FakeSession first = new();
        FakeSession second = new();
        h.Sessions.Enqueue(first);
        h.Sessions.Enqueue(second);

        h.Controller.Start();
        await WaitUntilAsync(() => h.Controller.State == ClientState.Recording);
        h.Capture.EmitChunk(new byte[] { 1, 2 });
        first.PublishError("busy", "Server occupato");

        await WaitUntilAsync(() => h.Controller.State == ClientState.Idle);
        Assert.NotNull(h.Recovery.GetCurrent());
        Assert.Equal("busy", h.Recovery.GetCurrent()!.FailureCode);

        h.Controller.Start();
        await WaitUntilAsync(() => h.Capture.StartCalls == 2);
        Assert.Equal(ClientState.Recording, h.Controller.State);
    }

    [Fact]
    public async Task ResultWithUipiBlockedAtPaste_OffersRecoveryWithoutPasting()
    {
        Harness h = new(elevationBlockedAfterFirstCheck: true);
        FakeSession session = new();
        h.Sessions.Enqueue(session);

        h.Controller.Start();
        await WaitUntilAsync(() => h.Controller.State == ClientState.Recording);
        session.PublishResult("ciao mondo");

        await WaitUntilAsync(() => h.Recovery.GetCurrent() is not null);

        Assert.Equal(0, h.Clipboard.SetTextCalls);
        Assert.Equal("uipi_blocked", h.Recovery.GetCurrent()!.FailureCode);
        Assert.Equal("ciao mondo", h.Recovery.GetCurrent()!.Result!.FinalText);
        Assert.Equal(ClientState.Idle, h.Controller.State);
    }

    [Fact]
    public async Task SuccessfulResult_PastesTextAndHidesPill()
    {
        Harness h = new();
        FakeSession session = new();
        h.Sessions.Enqueue(session);

        h.Controller.Start();
        await WaitUntilAsync(() => h.Controller.State == ClientState.Recording);
        session.PublishResult("testo finale");

        await WaitUntilAsync(() => h.Input.Combos.Count > 0);

        Assert.Equal("ctrl+v", h.Input.Combos[0]);
        Assert.Equal(ClientState.Idle, h.Controller.State);
        Assert.True(h.Pill.HideCalls > 0);
        Assert.Contains(SoundKind.Stop, h.Sound.PlayedSounds);
    }

    [Fact]
    public async Task EmptyResult_DoesNotTouchTheClipboard()
    {
        Harness h = new();
        FakeSession session = new();
        h.Sessions.Enqueue(session);

        h.Controller.Start();
        await WaitUntilAsync(() => h.Controller.State == ClientState.Recording);
        session.PublishResult("   ");

        await WaitUntilAsync(() => h.Pill.LastState == PillState.EmptyResult);

        Assert.Equal(0, h.Clipboard.SetTextCalls);
        Assert.Equal(ClientState.Idle, h.Controller.State);
    }

    [Fact]
    public async Task CopyLastText_UsesClipboardWithoutPasting()
    {
        Harness h = new();
        h.OfferResult("testo recuperato");

        await h.Controller.CopyLastTextAsync(CancellationToken.None);

        Assert.Equal("testo recuperato", h.Clipboard.Text);
        Assert.Null(h.Recovery.GetCurrent());
        Assert.Equal(0, h.Clipboard.BackupCalls);
    }

    [Fact]
    public async Task Retry_WithExistingResult_CopiesWithoutNewTranscription()
    {
        Harness h = new();
        h.OfferResult("già trascritto");

        await h.Controller.RetryLastAsync(CancellationToken.None);

        Assert.Equal("già trascritto", h.Clipboard.Text);
        Assert.Null(h.Recovery.GetCurrent());
    }

    [Fact]
    public async Task AbortDuringRecording_IgnoresALateResultAndNeverPastes()
    {
        Harness h = new();
        FakeSession session = new();
        h.Sessions.Enqueue(session);

        h.Controller.Start();
        await WaitUntilAsync(() => h.Controller.State == ClientState.Recording);

        await h.Controller.AbortAsync(CancellationToken.None);
        session.PublishResult("risultato tardivo");

        await Task.Delay(100);
        Assert.Empty(h.Input.Combos);
        Assert.Equal(ClientState.Idle, h.Controller.State);
    }

    [Fact]
    public async Task StopAsync_WhenIdle_DoesNothing()
    {
        Harness h = new();

        await h.Controller.StopAsync(CancellationToken.None);

        Assert.Equal(0, h.Capture.StopCalls);
        Assert.Equal(ClientState.Idle, h.Controller.State);
    }

    [Fact]
    public async Task AbortAsync_WhenIdle_LeavesIdleAndHidesPill()
    {
        Harness h = new();

        await h.Controller.AbortAsync(CancellationToken.None);

        Assert.Equal(ClientState.Idle, h.Controller.State);
        Assert.Equal(1, h.Pill.HideCalls);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        using CancellationTokenSource cts = new(timeoutMs);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }

    private sealed class Harness
    {
        public FakeCapture Capture { get; } = new();
        public FakePillView Pill { get; } = new();
        public FakeSoundPlayer Sound { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public FakeClipboardService Clipboard { get; } = new();
        public FakeInputSimulator Input { get; } = new();
        public RecoveryStore Recovery { get; }
        public Queue<FakeSession> Sessions { get; } = new();
        public DictationController Controller { get; }

        public Harness(
            bool elevationBlocked = false,
            bool elevationBlockedAfterFirstCheck = false)
        {
            Recovery = new RecoveryStore(Time, TimeSpan.FromSeconds(600));

            PasteService paste = new(
                Clipboard,
                Input,
                new FakeForegroundWindowService(),
                new FakeElevationChecker(),
                new InlinePasteDispatcher(),
                FastPasteConfig,
                Time);

            FakeElevation elevation = new()
            {
                Blocked = elevationBlocked,
                BlockedAfterFirstCheck = elevationBlockedAfterFirstCheck,
            };

            Controller = new DictationController(
                Capture,
                () => Sessions.Count > 0 ? Sessions.Dequeue() : new FakeSession(),
                paste,
                Recovery,
                new FakeForegroundWindowService { Window = Window },
                elevation,
                Pill,
                Sound,
                DefaultConfig,
                Time);
        }

        public void OfferResult(string text)
        {
            Recovery.Offer(new RecoveryItem(
                Guid.NewGuid(),
                null,
                new ResultMessage("raw", text, false, new DictationMetrics(1, 2, 3), []),
                new StartMessage("GangioKeet.Client", Window.WindowTitle, "default"),
                Window,
                "paste_failed",
                Time.GetUtcNow().DateTime.AddSeconds(600)));
        }
    }
}

internal sealed class FakeForegroundWindowService : IForegroundWindowService
{
    public ForegroundWindowInfo Window { get; set; } = new(0x1234, 42, "notepad.exe", "Senza titolo");
    public bool Alive { get; set; } = true;

    public nint CaptureHandle() => Window.Handle;
    public ForegroundWindowInfo Read(nint handle) => Window;
    public nint GetForegroundHandle() => Window.Handle;
    public bool TryActivate(nint handle) => true;
    public bool IsAlive(ForegroundWindowInfo target) => Alive;
}

internal sealed class FakeElevation : IElevationChecker
{
    public bool Blocked { get; set; }
    public bool BlockedAfterFirstCheck { get; set; }

    private int _calls;

    public bool IsInjectionBlocked(ForegroundWindowInfo target)
    {
        _calls++;
        return BlockedAfterFirstCheck ? _calls > 1 : Blocked;
    }
}

internal sealed class FakeClipboardService : IClipboardService
{
    public string? Text { get; set; }
    public byte[]? Dib { get; set; }
    public uint Sequence { get; private set; } = 1;

    public int BackupCalls { get; private set; }
    public int SetTextCalls { get; private set; }

    public ClipboardSnapshot Backup()
    {
        BackupCalls++;
        return new ClipboardSnapshot(Text, Dib);
    }

    public uint SetText(string text)
    {
        SetTextCalls++;
        Text = text;
        Dib = null;
        return ++Sequence;
    }

    public bool TryRestore(ClipboardSnapshot snapshot, uint publishedSequence)
    {
        if (Sequence != publishedSequence) return false;
        Text = snapshot.Text;
        Dib = snapshot.Dib;
        return true;
    }
}

internal sealed class FakeInputSimulator : IInputSimulator
{
    public bool Result { get; set; } = true;
    public List<string> Combos { get; } = [];
    public bool SendCombo(string combo)
    {
        Combos.Add(combo);
        return Result;
    }
}

internal sealed class FakeElevationChecker : IElevationChecker
{
    public bool IsInjectionBlocked(ForegroundWindowInfo target) => false;
}

internal sealed class InlinePasteDispatcher : IPasteDispatcher
{
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return Task.FromResult(work()); }
        catch (Exception ex) { return Task.FromException<T>(ex); }
    }
}
