using GangioKeet.Client.Configuration;
using GangioKeet.Client.Context;
using GangioKeet.Client.Paste;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GangioKeet.Client.Tests.Paste;

public sealed class PasteServiceTests
{
    private const nint Handle = 0x1234;
    private const int Pid = 42;

    private static readonly ForegroundWindowInfo Notepad = new(Handle, Pid, "notepad.exe", "Senza titolo");
    private static readonly ForegroundWindowInfo Terminal = new(Handle, Pid, "wt.exe", "PowerShell");

    private static readonly PasteConfig FastPasteConfig = new() { ClipboardRestoreDelayMs = 0 };

    private static PasteService Build(
        FakeClipboard clipboard,
        FakeInput input,
        FakeForeground foreground,
        FakeElevation elevation,
        FakeTimeProvider time,
        PasteConfig? config = null) =>
        new(clipboard, input, foreground, elevation, new InlineDispatcher(), config ?? FastPasteConfig, time);

    [Fact]
    public async Task EmptyOrWhitespace_DoesNotTouchTheClipboard()
    {
        FakeClipboard clipboard = new();
        FakeTimeProvider time = new();
        PasteService service = Build(clipboard, new FakeInput(), new FakeForeground(), new FakeElevation(), time);

        PasteOutcome empty = await service.PasteAsync("", Notepad, CancellationToken.None);
        PasteOutcome blank = await service.PasteAsync("   ", Notepad, CancellationToken.None);

        Assert.Equal(PasteStatus.SkippedEmpty, empty.Status);
        Assert.Equal(PasteStatus.SkippedEmpty, blank.Status);
        Assert.Equal(0, clipboard.BackupCalls);
        Assert.Equal(0, clipboard.SetTextCalls);
    }

    [Fact]
    public async Task TerminalApps_UseTerminalCombo_NotProfile()
    {
        FakeInput input = new();
        PasteService service = Build(new FakeClipboard(), input, new FakeForeground(), new FakeElevation(), new FakeTimeProvider());

        await service.PasteAsync("ciao", Terminal, CancellationToken.None);
        await service.PasteAsync("ciao", Notepad, CancellationToken.None);

        Assert.Equal(["ctrl+shift+v", "ctrl+v"], input.Combos);
    }

    [Fact]
    public async Task ScreenshotDib_IsPreservedInTheSnapshot()
    {
        byte[] dib = [1, 2, 3, 4];
        FakeClipboard clipboard = new() { Text = "vecchio", Dib = dib };
        PasteService service = Build(clipboard, new FakeInput(), new FakeForeground(), new FakeElevation(), new FakeTimeProvider());

        PasteOutcome outcome = await service.PasteAsync("nuovo", Notepad, CancellationToken.None);

        Assert.Equal(PasteStatus.Pasted, outcome.Status);
        Assert.False(outcome.TextLeftInClipboard);
        Assert.NotNull(clipboard.LastRestored);
        Assert.Equal("vecchio", clipboard.LastRestored!.Text);
        Assert.Equal(dib, clipboard.LastRestored.Dib);
    }

    [Fact]
    public async Task CancelledAfterClipboardWrite_DoesNotSendComboAndRestoresClipboard()
    {
        using CancellationTokenSource cts = new();
        FakeClipboard clipboard = new() { Text = "vecchio", OnAfterSetText = cts.Cancel };
        FakeInput input = new();
        PasteService service = Build(clipboard, input, new FakeForeground(), new FakeElevation(), new FakeTimeProvider());

        PasteOutcome outcome = await service.PasteAsync("nuovo", Notepad, cts.Token);

        Assert.Equal(PasteStatus.Cancelled, outcome.Status);
        Assert.Empty(input.Combos);
        Assert.NotNull(clipboard.LastRestored);
        Assert.Equal("vecchio", clipboard.LastRestored!.Text);
    }

    [Fact]
    public async Task EmptyClipboard_IsRestoredEmpty()
    {        FakeClipboard clipboard = new() { Text = null };
        PasteService service = Build(clipboard, new FakeInput(), new FakeForeground(), new FakeElevation(), new FakeTimeProvider());

        await service.PasteAsync("nuovo", Notepad, CancellationToken.None);

        Assert.NotNull(clipboard.LastRestored);
        Assert.True(clipboard.LastRestored!.IsEmpty);
    }

    [Fact]
    public async Task UserCopiesDuringDelay_NewClipboardIsPreserved()
    {
        FakeTimeProvider time = new();
        FakeClipboard clipboard = new();
        PasteConfig slowConfig = new() { ClipboardRestoreDelayMs = 200 };
        PasteService service = Build(clipboard, new FakeInput(), new FakeForeground(), new FakeElevation(), time, slowConfig);
        clipboard.OnBeforeRestore = () => clipboard.PublishExternalCopy("altro testo");

        Task<PasteOutcome> pending = service.PasteAsync("nuovo", Notepad, CancellationToken.None);
        time.Advance(TimeSpan.FromMilliseconds(200));

        PasteOutcome outcome = await pending.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(PasteStatus.Pasted, outcome.Status);
        Assert.True(outcome.TextLeftInClipboard);
        Assert.Equal("altro testo", clipboard.Text);
    }

    [Fact]
    public async Task RestoreUsesThePublishedSequence()
    {
        FakeClipboard clipboard = new();
        PasteService service = Build(clipboard, new FakeInput(), new FakeForeground(), new FakeElevation(), new FakeTimeProvider());

        await service.PasteAsync("nuovo", Notepad, CancellationToken.None);

        Assert.Equal(clipboard.PublishedSequence, clipboard.RestoredSequence);
        Assert.Equal(1, clipboard.RestoreCalls);
    }

    [Fact]
    public async Task InjectionFailure_CleansUpAndLeavesTheTextForRecovery()
    {
        FakeInput input = new() { Result = false };
        FakeClipboard clipboard = new();
        PasteService service = Build(clipboard, input, new FakeForeground(), new FakeElevation(), new FakeTimeProvider());

        PasteOutcome outcome = await service.PasteAsync("nuovo", Notepad, CancellationToken.None);

        Assert.Equal(PasteStatus.InjectionFailed, outcome.Status);
        Assert.True(outcome.TextLeftInClipboard);
        Assert.Equal("nuovo", clipboard.Text);
        Assert.Equal(0, clipboard.RestoreCalls);
    }

    [Fact]
    public async Task RefocusDenied_NeverSendsInputAndRestoresTheClipboard()
    {
        FakeInput input = new();
        FakeClipboard clipboard = new();
        FakeForeground foreground = new() { ActivationSucceeds = false };
        PasteService service = Build(clipboard, input, foreground, new FakeElevation(), new FakeTimeProvider());

        PasteOutcome outcome = await service.PasteAsync("nuovo", Notepad, CancellationToken.None);

        Assert.Equal(PasteStatus.FocusLost, outcome.Status);
        Assert.Empty(input.Combos);
        Assert.Equal(1, clipboard.RestoreCalls);
        Assert.Equal("vecchio", clipboard.Text);
    }

    [Fact]
    public async Task FocusChangedAfterRefocus_NeverSendsInput()
    {
        FakeInput input = new();
        FakeClipboard clipboard = new();
        FakeForeground foreground = new() { CurrentAfterActivation = new ForegroundWindowInfo(0x9999, 7, "other.exe", "altra") };
        PasteService service = Build(clipboard, input, foreground, new FakeElevation(), new FakeTimeProvider());

        PasteOutcome outcome = await service.PasteAsync("nuovo", Notepad, CancellationToken.None);

        Assert.Equal(PasteStatus.FocusLost, outcome.Status);
        Assert.Empty(input.Combos);
        Assert.Equal(1, clipboard.RestoreCalls);
    }

    [Fact]
    public async Task ElevatedAtStart_NoClipboardTouch()
    {
        FakeElevation elevation = new() { Blocked = true };
        FakeClipboard clipboard = new();
        PasteService service = Build(clipboard, new FakeInput(), new FakeForeground(), elevation, new FakeTimeProvider());

        PasteOutcome outcome = await service.PasteAsync("nuovo", Notepad, CancellationToken.None);

        Assert.Equal(PasteStatus.BlockedUipi, outcome.Status);
        Assert.False(outcome.TextLeftInClipboard);
        Assert.Equal(0, clipboard.BackupCalls);
        Assert.Equal(0, clipboard.SetTextCalls);
    }

    [Fact]
    public async Task TargetBecomesElevatedBeforePaste_LeavesTheTextInClipboard()
    {
        FakeElevation elevation = new() { BlockedAfterFirstCheck = true };
        FakeClipboard clipboard = new();
        FakeInput input = new();
        PasteService service = Build(clipboard, input, new FakeForeground(), elevation, new FakeTimeProvider());

        PasteOutcome outcome = await service.PasteAsync("nuovo", Notepad, CancellationToken.None);

        Assert.Equal(PasteStatus.BlockedUipi, outcome.Status);
        Assert.True(outcome.TextLeftInClipboard);
        Assert.Empty(input.Combos);
        Assert.Equal(0, clipboard.RestoreCalls);
    }

    [Fact]
    public async Task CancelledAfterPublish_StillSettlesTheClipboard()
    {
        FakeTimeProvider time = new();
        FakeClipboard clipboard = new();
        using CancellationTokenSource source = new();
        PasteConfig slowConfig = new() { ClipboardRestoreDelayMs = 200 };
        PasteService service = Build(clipboard, new FakeInput(), new FakeForeground(), new FakeElevation(), time, slowConfig);

        Task<PasteOutcome> pending = service.PasteAsync("nuovo", Notepad, source.Token);
        Assert.True(clipboard.Published.Wait(TimeSpan.FromSeconds(3)));

        source.Cancel();
        // The delay now uses CancellationToken.None, so it completes even if ct is cancelled.
        time.Advance(TimeSpan.FromMilliseconds(200));

        PasteOutcome outcome = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PasteStatus.Pasted, outcome.Status);
        Assert.False(outcome.TextLeftInClipboard);
        Assert.Equal(1, clipboard.RestoreCalls);
    }

    [Fact]
    public async Task ClipboardUnavailable_IsReportedWithoutThrowing()
    {
        FakeClipboard clipboard = new() { BackupUnavailable = true };
        PasteService service = Build(clipboard, new FakeInput(), new FakeForeground(), new FakeElevation(), new FakeTimeProvider());

        PasteOutcome outcome = await service.PasteAsync("nuovo", Notepad, CancellationToken.None);

        Assert.Equal(PasteStatus.ClipboardUnavailable, outcome.Status);
        Assert.False(outcome.TextLeftInClipboard);
    }

    [Fact]
    public async Task DeadTarget_IsRejectedBeforeTouchingTheClipboard()
    {
        FakeClipboard clipboard = new();
        FakeForeground foreground = new() { Alive = false };
        PasteService service = Build(clipboard, new FakeInput(), foreground, new FakeElevation(), new FakeTimeProvider());

        PasteOutcome outcome = await service.PasteAsync("nuovo", Notepad, CancellationToken.None);

        Assert.Equal(PasteStatus.FocusLost, outcome.Status);
        Assert.Equal(0, clipboard.BackupCalls);
    }
}

internal sealed class InlineDispatcher : IPasteDispatcher
{
    public Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(work());
        }
        catch (Exception ex)
        {
            return Task.FromException<T>(ex);
        }
    }
}

internal sealed class FakeClipboard : IClipboardService
{
    public string? Text { get; set; } = "vecchio";

    public byte[]? Dib { get; set; }

    public uint Sequence { get; private set; } = 10;

    public uint PublishedSequence { get; private set; }

    public uint RestoredSequence { get; private set; }

    public ClipboardSnapshot? LastRestored { get; private set; }

    public int BackupCalls { get; private set; }

    public int SetTextCalls { get; private set; }

    public int RestoreCalls { get; private set; }

    public bool BackupUnavailable { get; init; }
    public Action? OnAfterSetText { get; set; }

    public Action? OnBeforeRestore { get; set; }

    public ManualResetEventSlim Published { get; } = new(false);

    public ClipboardSnapshot Backup()
    {
        BackupCalls++;
        if (BackupUnavailable)
        {
            throw new ClipboardUnavailableException("unavailable");
        }

        return new ClipboardSnapshot(Text, Dib);
    }

    public uint SetText(string text)
    {
        SetTextCalls++;
        Text = text;
        Dib = null;
        Sequence++;
        PublishedSequence = Sequence;
        Published.Set();
        OnAfterSetText?.Invoke();
        return Sequence;
    }

    public bool TryRestore(ClipboardSnapshot snapshot, uint publishedSequence)
    {
        RestoreCalls++;
        OnBeforeRestore?.Invoke();
        RestoredSequence = publishedSequence;
        LastRestored = snapshot;
        if (Sequence != publishedSequence)
        {
            return false;
        }

        Text = snapshot.Text;
        Dib = snapshot.Dib;
        return true;
    }

    public void PublishExternalCopy(string text)
    {
        Text = text;
        Sequence++;
    }
}

internal sealed class FakeInput : IInputSimulator
{
    public bool Result { get; set; } = true;

    public List<string> Combos { get; } = [];

    public bool SendCombo(string combo)
    {
        Combos.Add(combo);
        return Result;
    }
}

internal sealed class FakeForeground : IForegroundWindowService
{
    public bool Alive { get; set; } = true;

    public bool ActivationSucceeds { get; set; } = true;

    public ForegroundWindowInfo? CurrentAfterActivation { get; set; }

    public bool Activated { get; private set; }

    public nint CaptureHandle() => 0x1234;

    public ForegroundWindowInfo Read(nint handle) =>
        Activated && CurrentAfterActivation is not null
            ? CurrentAfterActivation with { Handle = handle }
            : new ForegroundWindowInfo(handle, 42, "notepad.exe", "Senza titolo");

    public nint GetForegroundHandle() => 0x1234;

    public bool TryActivate(nint handle)
    {
        Activated = true;
        return ActivationSucceeds;
    }

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
        if (BlockedAfterFirstCheck)
        {
            return _calls > 1;
        }

        return Blocked;
    }
}
