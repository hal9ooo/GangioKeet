using GangioKeet.Client.Audio;
using Xunit;

namespace GangioKeet.Client.Tests.Audio;

public sealed class RamAudioBufferTests
{
    private const int MaxBytes = 67108864;

    [Fact]
    public void TryAppend_AcceptsChunkAtExactLimit()
    {
        // Limite esatto 67108864 accettato interamente: l'append tocca il tetto.
        RamAudioBuffer buffer = new();
        Assert.True(buffer.TryAppend(new byte[MaxBytes]));
        Assert.Equal(MaxBytes, buffer.Length);
    }

    [Fact]
    public void TryAppend_RejectsPlusTwoOverFullBufferWithoutPartialAppend()
    {
        RamAudioBuffer buffer = new();
        Assert.True(buffer.TryAppend(new byte[MaxBytes]));
        Assert.Equal(MaxBytes, buffer.Length);

        // +2 su buffer pieno: rifiutato e nessun byte parziale resta nel buffer.
        Assert.False(buffer.TryAppend(new byte[2]));
        Assert.Equal(MaxBytes, buffer.Length);
        byte[] snapshot = buffer.Snapshot();
        Assert.Equal(MaxBytes, snapshot.Length);
        Assert.All(snapshot, b => Assert.Equal(0, b));
    }

    [Fact]
    public void TryAppend_RejectsChunkLargerThanTotalLimit()
    {
        RamAudioBuffer buffer = new(10);
        Assert.False(buffer.TryAppend(new byte[11]));
        Assert.Equal(0, buffer.Length);
    }

    [Fact]
    public void TryAppend_RejectsChunkExceedingRemainingCapacity()
    {
        RamAudioBuffer buffer = new(10);
        Assert.True(buffer.TryAppend(new byte[8]));

        Assert.False(buffer.TryAppend(new byte[3]));
        Assert.Equal(8, buffer.Length);
    }

    [Fact]
    public void Snapshot_IsIndependentFromCaptureBufferAndLaterAppends()
    {
        RamAudioBuffer buffer = new();
        byte[] capture = [1, 2, 3, 4, 5];
        Assert.True(buffer.TryAppend(capture));

        byte[] snapshot = buffer.Snapshot();
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, snapshot);

        // La capture viene mutata dopo l'append: lo snapshot non cambia.
        capture[0] = 99;
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, snapshot);

        // Append successivi non retroagiscono sullo snapshot già restituito.
        Assert.True(buffer.TryAppend((byte[])[6, 7]));
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, snapshot);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7 }, buffer.Snapshot());
    }

    [Fact]
    public void Snapshot_ReflectsAppends()
    {
        RamAudioBuffer buffer = new();
        Assert.True(buffer.TryAppend([1, 2, 3]));
        byte[] snapshot = buffer.Snapshot();
        Assert.Equal(new byte[] { 1, 2, 3 }, snapshot);
        Assert.Equal(3, buffer.Length);
    }

    [Fact]
    public void Clear_ReleasesReferencesAndAllowsReuse()
    {
        RamAudioBuffer buffer = new(10);
        Assert.True(buffer.TryAppend(new byte[10]));

        buffer.Clear();
        Assert.Equal(0, buffer.Length);
        Assert.Empty(buffer.Snapshot());

        Assert.True(buffer.TryAppend(new byte[4]));
        Assert.Equal(4, buffer.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, buffer.Snapshot());
    }
}
