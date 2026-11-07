using System.Threading;
using GangioKeet.Client.Platform;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

public sealed class ShutdownRequestListenerTests
{
    [Fact]
    public void SignallingTheEventInvokesTheCallback()
    {
        string name = $@"Local\ChattyNeko.Test.{Guid.NewGuid():N}";
        using ManualResetEventSlim fired = new(false);

        using ShutdownRequestListener listener = new(() => fired.Set(), name);

        using EventWaitHandle signal = EventWaitHandle.OpenExisting(name);
        signal.Set();

        Assert.True(fired.Wait(TimeSpan.FromSeconds(5)), "la richiesta di chiusura non è arrivata");
    }

    [Fact]
    public void DisposeDoesNotInvokeTheCallback()
    {
        string name = $@"Local\ChattyNeko.Test.{Guid.NewGuid():N}";
        int calls = 0;

        ShutdownRequestListener listener = new(() => Interlocked.Increment(ref calls), name);
        listener.Dispose();

        Thread.Sleep(300);

        Assert.Equal(0, Volatile.Read(ref calls));
    }
}
