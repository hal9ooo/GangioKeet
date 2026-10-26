using GangioKeet.Client.Hotkey;
using Xunit;

namespace GangioKeet.Client.Tests.Hotkey;

public sealed class InputHooksTests
{
    private const int F8 = 0x77;
    private const int MouseX1 = 1;

    [Fact]
    public void Start_InstallsMouseHookOnlyWhenHotkeyIsMouse()
    {
        FakeHookBackend keyboardBackend = new();
        using (LowLevelInputHooks keyboard = new(keyboardBackend, new HookSuppressionPolicy("f8", "escape")))
        {
            keyboard.Start();
            keyboardBackend.WaitUntilRunning();
            Assert.False(keyboardBackend.InstalledMouse);
        }

        FakeHookBackend mouseBackend = new();
        using (LowLevelInputHooks mouse = new(mouseBackend, new HookSuppressionPolicy("mouse_x1", "escape")))
        {
            mouse.Start();
            mouseBackend.WaitUntilRunning();
            Assert.True(mouseBackend.InstalledMouse);
        }
    }

    [Fact]
    public async Task RawEvents_CarryTheForegroundHandleCapturedInTheCallback()
    {
        FakeHookBackend backend = new();
        using LowLevelInputHooks hooks = new(backend, new HookSuppressionPolicy("f8", "escape"));
        TaskCompletionSource<RawKeyEvent> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        hooks.RawEvent += (_, raw) => received.TrySetResult(raw);

        hooks.Start();
        backend.WaitUntilRunning();
        backend.Emit(new RawInput(F8, IsDown: true, FromMouse: false, ForegroundHandle: 0x1234));

        RawKeyEvent raw = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(F8, raw.VirtualKey);
        Assert.True(raw.IsDown);
        Assert.False(raw.FromMouse);
        Assert.Equal((nint)0x1234, raw.ForegroundHandle);
    }

    [Fact]
    public void MouseEvents_AreSuppressedAndForwardedWhenConfigured()
    {
        FakeHookBackend backend = new();
        using LowLevelInputHooks hooks = new(backend, new HookSuppressionPolicy("mouse_x1", "escape"));
        int events = 0;
        hooks.RawEvent += (_, _) => Interlocked.Increment(ref events);

        hooks.Start();
        backend.WaitUntilRunning();
        backend.Emit(new RawInput(MouseX1, IsDown: true, FromMouse: true, ForegroundHandle: 0x55));
        backend.Emit(new RawInput(MouseX1, IsDown: false, FromMouse: true, ForegroundHandle: 0x55));

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref events) == 2, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Overflow_RaisesExplicitErrorAndResetsPolicy()
    {
        FakeHookBackend backend = new();
        using LowLevelInputHooks hooks = new(
            backend,
            new HookSuppressionPolicy("f8", "escape"),
            capacity: 1);

        ManualResetEventSlim handlerEntered = new(false);
        ManualResetEventSlim releaseHandler = new(false);
        ManualResetEventSlim handlerExited = new(false);
        int overflow = 0;

        hooks.RawEvent += (_, _) =>
        {
            handlerEntered.Set();
            releaseHandler.Wait(TimeSpan.FromSeconds(3));
            handlerExited.Set();
        };
        hooks.Failed += (_, _) => Interlocked.Increment(ref overflow);

        hooks.Start();
        backend.WaitUntilRunning();
        backend.Emit(new RawInput(F8, IsDown: true, FromMouse: false, ForegroundHandle: 1));
        Assert.True(handlerEntered.Wait(TimeSpan.FromSeconds(3)));

        backend.Emit(new RawInput(F8, IsDown: false, FromMouse: false, ForegroundHandle: 1));
        backend.Emit(new RawInput(F8, IsDown: true, FromMouse: false, ForegroundHandle: 1));

        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref overflow) == 1, TimeSpan.FromSeconds(3)));
        releaseHandler.Set();
        Assert.True(handlerExited.Wait(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void Dispose_StopsTheBackendOnItsOwnThreadAndJoins()
    {
        FakeHookBackend backend = new();
        LowLevelInputHooks hooks = new(backend, new HookSuppressionPolicy("f8", "escape"));
        hooks.Start();
        backend.WaitUntilRunning();

        int runThread = backend.RunThreadId;
        hooks.Dispose();

        Assert.True(backend.RunCompleted);
        Assert.True(runThread > 0);
        Assert.NotEqual(Environment.CurrentManagedThreadId, runThread);
    }
}

internal sealed class FakeHookBackend : IInputHookBackend
{
    private readonly ManualResetEventSlim _running = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);

    public bool InstalledMouse { get; private set; }

    public int RunThreadId { get; private set; }

    public bool RunCompleted { get; private set; }

    private Func<RawInput, bool>? Dispatch { get; set; }

    public void Run(bool installMouseHook, Func<RawInput, bool> onInput)
    {
        InstalledMouse = installMouseHook;
        Dispatch = onInput;
        RunThreadId = Environment.CurrentManagedThreadId;
        _running.Set();
        _stopped.Wait(TimeSpan.FromSeconds(5));
        RunCompleted = true;
    }

    public void Stop() => _stopped.Set();

    public void Dispose()
    {
    }

    public void WaitUntilRunning() =>
        Assert.True(_running.Wait(TimeSpan.FromSeconds(3)), "the hook thread did not start");

    public void Emit(RawInput raw) =>
        _ = (Dispatch ?? throw new InvalidOperationException("backend not running")).Invoke(raw);
}
