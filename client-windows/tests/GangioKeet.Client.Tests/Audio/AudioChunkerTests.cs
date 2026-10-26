using GangioKeet.Client.Audio;
using Xunit;

namespace GangioKeet.Client.Tests.Audio;

public sealed class AudioChunkerTests
{
    private const int TargetChunkBytes = 3200;

    private static byte[] Pattern(int length)
    {
        return Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
    }

    private static byte[] Concat(IEnumerable<byte[]> parts)
    {
        return parts.SelectMany(p => p).ToArray();
    }

    [Fact]
    public void Feed_SingleBufferAtLeastTarget_EmitsExact3200ByteChunks()
    {
        byte[] input = Pattern(7500);
        AudioChunker chunker = new();

        IReadOnlyList<byte[]> chunks = chunker.Feed(input);

        Assert.Equal(2, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(TargetChunkBytes, c.Length));
        Assert.Equal(input.AsSpan(0, TargetChunkBytes).ToArray(), chunks[0]);
        Assert.Equal(input.AsSpan(TargetChunkBytes, TargetChunkBytes).ToArray(), chunks[1]);
    }

    [Fact]
    public void Feed_SplitInputAcrossCalls_ChunksAlignOnFixedBoundaries()
    {
        byte[] input = Pattern(7500);
        AudioChunker chunker = new();
        List<byte[]> received = [];

        int offset = 0;
        foreach (int part in new[] { 4000, 3500 })
        {
            received.AddRange(chunker.Feed(input.AsSpan(offset, part)));
            offset += part;
        }

        // 4000 -> un chunk intero (3200) con 800 in sospeso; 3500 -> un altro intero.
        Assert.Equal(2, received.Count);
        Assert.All(received, c => Assert.Equal(TargetChunkBytes, c.Length));
        Assert.Equal(input.AsSpan(0, 2 * TargetChunkBytes).ToArray(), Concat(received));
        Assert.Equal(input.AsSpan(2 * TargetChunkBytes).ToArray(), chunker.Flush());
    }

    [Fact]
    public void Flush_ReturnsShortEvenRemainderWithoutPadding()
    {
        byte[] input = Pattern(3300);
        AudioChunker chunker = new();

        IReadOnlyList<byte[]> chunks = chunker.Feed(input);
        byte[] tail = chunker.Flush();

        byte[] whole = Assert.Single(chunks);
        Assert.Equal(TargetChunkBytes, whole.Length);
        Assert.Equal(100, tail.Length);
        Assert.Equal(input.AsSpan(TargetChunkBytes).ToArray(), tail);
    }

    [Fact]
    public void Flush_PadsOddRemainderToOneExtraZeroByte()
    {
        byte[] input = Pattern(1599);
        AudioChunker chunker = new();

        Assert.Empty(chunker.Feed(input));
        byte[] tail = chunker.Flush();

        // PCM16 richiede allineamento a 2 byte: il resto dispari arriva completo con 1 byte di pad.
        Assert.Equal(1600, tail.Length);
        Assert.Equal(input, tail.AsSpan(0, 1599).ToArray());
        Assert.Equal(0, tail[1599]);
    }

    [Fact]
    public void FeedSplinteredInput_NeverLosesBytes()
    {
        byte[] input = Pattern(10020);
        AudioChunker chunker = new();
        List<byte[]> received = [];

        int offset = 0;
        foreach (int part in new[] { 1, 799, 1600, 13, 6583, 23, 1001 })
        {
            received.AddRange(chunker.Feed(input.AsSpan(offset, part)));
            offset += part;
        }

        Assert.Equal(10020, offset);
        received.Add(chunker.Flush());

        Assert.Equal(input, Concat(received));
        Assert.All(received, c => Assert.True(c.Length > 0 && c.Length % 2 == 0));
        Assert.All(received.Take(received.Count - 1), c => Assert.Equal(TargetChunkBytes, c.Length));
        Assert.True(received[^1].Length <= TargetChunkBytes);
    }

    [Fact]
    public void Flush_AfterWholeChunks_ReturnsEmptyAndChunkerStaysUsable()
    {
        AudioChunker chunker = new();
        Assert.Equal(2, chunker.Feed(new byte[TargetChunkBytes * 2]).Count);

        byte[] tail = chunker.Flush();
        Assert.Empty(tail);

        // Riuso nella sessione successiva: tutto torna a lavorare dal buffer vuoto.
        byte[] input = Pattern(6600);
        Assert.Equal(2, chunker.Feed(input).Count);
        Assert.Equal(input.AsSpan(2 * TargetChunkBytes).ToArray(), chunker.Flush());
    }

    [Fact]
    public void Feed_EmptyInput_EmitsNothing()
    {
        AudioChunker chunker = new();
        Assert.Empty(chunker.Feed(ReadOnlySpan<byte>.Empty));
        Assert.Empty(chunker.Flush());
    }
}

public sealed class Pcm16ConversionTests
{
    [Fact]
    public void FromFloat_EncodesSignedLittleEndian()
    {
        // 0.5 -> 16384 (0x4000) e -0.5 -> -16384 (0xC000): little endian.
        byte[] pcm = Pcm16.FromFloat([0.5f, -0.5f]);

        Assert.Equal(new byte[] { 0x00, 0x40, 0x00, 0xC0 }, pcm);
    }

    [Fact]
    public void FromFloat_SaturatesOutOfRangeFloatsToInt16Range()
    {
        byte[] pcm = Pcm16.FromFloat([1.0f, -1.0f, 1.5f, -1.5f, 2.0f]);

        Assert.Equal(new byte[]
        {
            0xFF, 0x7F, // +1.0f -> 32767 (saturo)
            0x00, 0x80, // -1.0f -> -32768
            0xFF, 0x7F, // +1.5f -> 32767 (saturo)
            0x00, 0x80, // -1.5f -> -32768 (saturo)
            0xFF, 0x7F, // +2.0f -> 32767 (saturo)
        }, pcm);
    }

    [Fact]
    public void FromFloat_PreservesLeastSignificantBitLevelValues()
    {
        // 3f/32768f risale esattamente a 3: voce debole mantenuta, nessun valore perso.
        byte[] pcm = Pcm16.FromFloat([3f / 32768f, -3f / 32768f]);

        Assert.Equal(new byte[] { 0x03, 0x00, 0xFD, 0xFF }, pcm);
    }
}
