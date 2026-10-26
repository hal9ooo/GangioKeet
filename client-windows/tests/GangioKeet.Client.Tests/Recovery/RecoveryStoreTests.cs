using GangioKeet.Client.Context;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recovery;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GangioKeet.Client.Tests.Recovery;

public sealed class RecoveryStoreTests
{
    private static readonly StartMessage Start =
        new("notepad.exe", "Senza titolo - Blocco note", "default");

    private static readonly ForegroundWindowInfo Target = new((nint)0x1234, 42, "notepad.exe", "Senza titolo");

    private static readonly TimeSpan Retention = TimeSpan.FromSeconds(600);

    private static RecoveryItem Item(Guid id, byte[]? pcm = null, ResultMessage? result = null) =>
        new(id, pcm, result, Start, Target, "timeout", DateTimeOffset.MinValue);

    [Fact]
    public void Offer_ReplacesTheSingleSlotAndNotifies()
    {
        FakeTimeProvider time = new();
        RecoveryStore store = new(time, Retention);
        int changed = 0;
        store.Changed += (_, _) => changed++;

        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        store.Offer(Item(first, pcm: [1, 2]));
        store.Offer(Item(second, pcm: [3, 4]));

        Assert.Equal(2, changed);
        Assert.Equal(second, store.GetCurrent()!.Id);
        Assert.Equal([3, 4], store.GetCurrent()!.Pcm);
    }

    [Fact]
    public void OfferedItem_ExpiresAfterRetention()
    {
        FakeTimeProvider time = new();
        RecoveryStore store = new(time, Retention);
        store.Offer(Item(Guid.NewGuid(), pcm: [1, 2]));

        time.Advance(Retention - TimeSpan.FromSeconds(1));
        Assert.NotNull(store.GetCurrent());

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(store.GetCurrent());
    }

    [Fact]
    public void Expiry_NotifiesOnce()
    {
        FakeTimeProvider time = new();
        RecoveryStore store = new(time, Retention);
        store.Offer(Item(Guid.NewGuid(), pcm: [1]));
        int changed = 0;
        store.Changed += (_, _) => changed++;

        time.Advance(Retention);
        Assert.Null(store.GetCurrent());
        Assert.Null(store.GetCurrent());
        Assert.Equal(1, changed);
    }

    [Fact]
    public void Discard_RemovesTheCurrentItemAndNotifies()
    {
        FakeTimeProvider time = new();
        RecoveryStore store = new(time, Retention);
        store.Offer(Item(Guid.NewGuid(), pcm: [1]));
        int changed = 0;
        store.Changed += (_, _) => changed++;

        store.Discard();

        Assert.Null(store.GetCurrent());
        Assert.Equal(1, changed);

        store.Discard();
        Assert.Equal(1, changed);
    }

    [Fact]
    public void RemoveIfCurrent_RemovesOnlyTheRetriedId()
    {
        FakeTimeProvider time = new();
        RecoveryStore store = new(time, Retention);
        Guid retried = Guid.NewGuid();
        store.Offer(Item(retried, pcm: [1]));

        Assert.True(store.RemoveIfCurrent(retried));
        Assert.Null(store.GetCurrent());
        Assert.False(store.RemoveIfCurrent(retried));
    }

    [Fact]
    public void OlderCompletion_DoesNotRemoveANewerError()
    {
        FakeTimeProvider time = new();
        RecoveryStore store = new(time, Retention);
        Guid older = Guid.NewGuid();
        Guid newer = Guid.NewGuid();

        store.Offer(Item(older, pcm: [1]));
        store.Offer(Item(newer, pcm: [2]));

        Assert.False(store.RemoveIfCurrent(older));
        Assert.Equal(newer, store.GetCurrent()!.Id);
    }

    [Fact]
    public void ResultOnlyItem_IsKeptForCopyingWithoutAudio()
    {
        FakeTimeProvider time = new();
        RecoveryStore store = new(time, Retention);
        ResultMessage result = new("grezzo", "finale", FastPath: true, new DictationMetrics(1, 0, 1), []);
        Guid id = Guid.NewGuid();

        store.Offer(Item(id, result: result));

        RecoveryItem current = store.GetCurrent()!;
        Assert.Null(current.Pcm);
        Assert.Equal("finale", current.Result!.FinalText);
        Assert.Equal(id, current.Id);
    }
}
