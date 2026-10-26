using GangioKeet.Client.Platform;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

public sealed class SingleInstanceTests
{
    [Fact]
    public void SecondInstance_DoesNotAcquireTheMutex()
    {
        SingleInstanceGuard guard = new($"GangioKeetTest_{Guid.NewGuid():N}");

        using IDisposable? first = guard.TryAcquire();
        Assert.NotNull(first);

        using IDisposable? second = guard.TryAcquire();
        Assert.Null(second);
    }

    [Fact]
    public void AfterRelease_ANewInstanceCanAcquire()
    {
        string name = $"GangioKeetTest_{Guid.NewGuid():N}";
        SingleInstanceGuard guard = new(name);

        IDisposable? first = guard.TryAcquire();
        Assert.NotNull(first);
        first.Dispose();

        using IDisposable? second = guard.TryAcquire();
        Assert.NotNull(second);
    }

    [Fact]
    public void TwoGuardsWithTheSameName_ShareTheMutex()
    {
        string name = $"GangioKeetTest_{Guid.NewGuid():N}";
        SingleInstanceGuard guardA = new(name);
        SingleInstanceGuard guardB = new(name);

        using IDisposable? a = guardA.TryAcquire();
        Assert.NotNull(a);

        using IDisposable? b = guardB.TryAcquire();
        Assert.Null(b);
    }
}
