using System;
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

    [Fact]
    public void Start_ElevatedTarget_ShowsErrorWithoutCapture()
    {
        FakeCapture capture = new();
        FakeTimeProvider time = new();
        FakeForeground foreground = new();
        FakeElevation elevation = new() { Blocked = true };
        FakePillView pill = new();
        FakeSoundPlayer sound = new();

        DictationController controller = new(
            capture,
            () => throw new InvalidOperationException("should not be called"),
            CreatePasteService(time),
            new RecoveryStore(time, TimeSpan.FromSeconds(600)),
            foreground,
            elevation,
            pill,
            sound,
            DefaultConfig,
            time);

        controller.Start();

        Assert.Equal(ClientState.Error, controller.State);
        Assert.Equal(PillState.Error, pill.LastState);
        Assert.Equal(0, capture.StartCalls);
    }

    [Fact]
    public void DoubleStart_SecondCallIsIgnored()
    {
        FakeCapture capture = new();
        FakeTimeProvider time = new();
        FakeForeground foreground = new();
        FakeElevation elevation = new();
        FakePillView pill = new();
        FakeSoundPlayer sound = new();
        int factoryCalls = 0;

        DictationController controller = new(
            capture,
            () =>
            {
                factoryCalls++;
                throw new InvalidOperationException("factory called");
            },
            CreatePasteService(time),
            new RecoveryStore(time, TimeSpan.FromSeconds(600)),
            foreground,
            elevation,
            pill,
            sound,
            DefaultConfig,
            time);

        controller.Start();
        controller.Start();

        // First Start reaches the factory, second Start should be ignored (state != Idle)
        Assert.Equal(1, factoryCalls);
    }

    [Fact]
    public void DiscardLast_DelegatesToStore()
    {
        FakeCapture capture = new();
        FakeTimeProvider time = new();
        FakeForeground foreground = new();
        FakeElevation elevation = new();
        RecoveryStore recovery = new(time, TimeSpan.FromSeconds(600));

        recovery.Offer(new RecoveryItem(
            Guid.NewGuid(), null, null,
            new StartMessage("test", "test", "default"),
            new ForegroundWindowInfo(1, 1, "test.exe", "test"),
            "test",
            time.GetUtcNow().DateTime.AddSeconds(600)));

        DictationController controller = new(
            capture,
            () => throw new InvalidOperationException("should not be called"),
            CreatePasteService(time),
            recovery,
            foreground,
            elevation,
            new FakePillView(),
            new FakeSoundPlayer(),
            DefaultConfig,
            time);

        Assert.NotNull(recovery.GetCurrent());
        controller.DiscardLast();
        Assert.Null(recovery.GetCurrent());
    }

    private static PasteService CreatePasteService(FakeTimeProvider time)
    {
        return new PasteService(
            new FakeClipboardService(),
            new FakeInputSimulator(),
            new FakeForegroundWindowService(),
            new FakeElevationChecker(),
            new InlinePasteDispatcher(),
            FastPasteConfig,
            time);
    }
}

internal sealed class FakeForeground : IForegroundWindowService
{
    public nint CapturedHandle { get; set; } = 0x1234;
    public ForegroundWindowInfo Window { get; set; } = new(0x1234, 42, "notepad.exe", "Senza titolo");
    public bool Alive { get; set; } = true;

    public nint CaptureHandle() => CapturedHandle;
    public ForegroundWindowInfo Read(nint handle) => Window;
    public nint GetForegroundHandle() => CapturedHandle;
    public bool TryActivate(nint handle) => true;
    public bool IsAlive(ForegroundWindowInfo target) => Alive;
}

internal sealed class FakeElevation : IElevationChecker
{
    public bool Blocked { get; set; }
    public bool IsInjectionBlocked(ForegroundWindowInfo target) => Blocked;
}

internal sealed class FakeClipboardService : IClipboardService
{
    public string? Text { get; set; }
    public uint Sequence { get; private set; }

    public ClipboardSnapshot Backup() => new(Text, null);
    public uint SetText(string text) { Text = text; return ++Sequence; }
    public bool TryRestore(ClipboardSnapshot snapshot, uint publishedSequence)
    {
        if (Sequence != publishedSequence) return false;
        Text = snapshot.Text;
        return true;
    }
}

internal sealed class FakeInputSimulator : IInputSimulator
{
    public bool Result { get; set; } = true;
    public bool SendCombo(string combo) => Result;
}

internal sealed class FakeForegroundWindowService : IForegroundWindowService
{
    public nint CaptureHandle() => 0;
    public ForegroundWindowInfo Read(nint handle) => new(0, 0, "", "");
    public nint GetForegroundHandle() => 0;
    public bool TryActivate(nint handle) => true;
    public bool IsAlive(ForegroundWindowInfo target) => true;
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
