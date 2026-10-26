using GangioKeet.Client.Hotkey;
using Xunit;

namespace GangioKeet.Client.Tests.Hotkey;

public sealed class GestureRouterTests
{
    [Fact]
    public void Start_ActivatesAbortAndStarts()
    {
        int starts = 0;
        bool abortActive = false;

        GestureRouter router = new(
            () => starts++,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            active => abortActive = active);

        router.OnGesture(GestureKind.Start);

        Assert.Equal(1, starts);
        Assert.True(abortActive);
    }

    [Fact]
    public async Task AbortThenStart_RestartsAfterAbortCompletes()
    {
        int starts = 0;
        bool abortActive = false;
        TaskCompletionSource abortGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        GestureRouter router = new(
            () => starts++,
            () => Task.CompletedTask,
            () => abortGate.Task,
            active => abortActive = active);

        router.OnGesture(GestureKind.Abort);
        router.OnGesture(GestureKind.Start);

        Assert.Equal(0, starts);

        abortGate.SetResult();
        await WaitUntilAsync(() => starts == 1);

        Assert.True(abortActive);
    }

    [Fact]
    public async Task Abort_DoesNotStartByItself()
    {
        int starts = 0;
        bool abortActive = true;

        GestureRouter router = new(
            () => starts++,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            active => abortActive = active);

        router.OnGesture(GestureKind.Abort);
        await WaitUntilAsync(() => !abortActive);

        Assert.Equal(0, starts);
    }

    [Fact]
    public async Task Stop_ClearsAbortActive()
    {
        bool abortActive = true;

        GestureRouter router = new(
            () => { },
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            active => abortActive = active);

        router.OnGesture(GestureKind.StopAndTranscribe);
        await WaitUntilAsync(() => !abortActive);

        Assert.False(abortActive);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        using CancellationTokenSource cts = new(timeoutMs);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }
}
