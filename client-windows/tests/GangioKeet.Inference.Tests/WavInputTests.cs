using GangioKeet.Eval;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class WavInputTests
{
    private static byte[] Wav(int sampleRate = 16000, short channels = 1, short bits = 16)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write("RIFF"u8); writer.Write(40); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write(channels);
        writer.Write(sampleRate); writer.Write(sampleRate * channels * bits / 8);
        writer.Write((short)(channels * bits / 8)); writer.Write(bits);
        writer.Write("data"u8); writer.Write(4); writer.Write(new byte[] { 0, 0, 255, 127 });
        return stream.ToArray();
    }

    [Fact]
    public void ReadsOnlyPcmPayloadAndDuration()
    {
        WavInput input = WavInput.Read(new MemoryStream(Wav()));
        Assert.Equal(new byte[] { 0, 0, 255, 127 }, input.Pcm);
        Assert.Equal(0.000125, input.DurationSeconds);
    }

    [Theory]
    [InlineData(48000, 1, 16)]
    [InlineData(16000, 2, 16)]
    [InlineData(16000, 1, 8)]
    public void RejectsNonContractAudio(int rate, short channels, short bits)
    {
        Assert.Throws<InvalidDataException>(() => WavInput.Read(new MemoryStream(Wav(rate, channels, bits))));
    }

    [Fact]
    public void RejectsTruncatedPayload()
    {
        byte[] wav = Wav();
        Assert.Throws<InvalidDataException>(() => WavInput.Read(new MemoryStream(wav[..^1])));
    }
}
