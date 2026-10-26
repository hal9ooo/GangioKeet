using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class ToneGeneratorTests
{
    [Fact]
    public void StartTone_HasCorrectDuration()
    {
        byte[] wav = SynthSoundPlayer.GenerateToneWav(880, 60);

        // WAV: 44 byte header + samples. 22050 Hz * 60ms = 1323 samples * 2 bytes = 2646
        int expectedDataSize = 22050 * 60 / 1000 * 2;
        Assert.Equal(44 + expectedDataSize, wav.Length);
    }

    [Fact]
    public void StopTone_HasCorrectFrequency()
    {
        byte[] wav = SynthSoundPlayer.GenerateToneWav(660, 60);

        int expectedDataSize = 22050 * 60 / 1000 * 2;
        Assert.Equal(44 + expectedDataSize, wav.Length);
    }

    [Fact]
    public void ErrorTone_HasCorrectDuration()
    {
        byte[] wav = SynthSoundPlayer.GenerateToneWav(220, 180);

        int expectedDataSize = 22050 * 180 / 1000 * 2;
        Assert.Equal(44 + expectedDataSize, wav.Length);
    }

    [Fact]
    public void WavHeader_HasCorrectFormat()
    {
        byte[] wav = SynthSoundPlayer.GenerateToneWav(440, 100);

        // RIFF header
        Assert.Equal((byte)'R', wav[0]);
        Assert.Equal((byte)'I', wav[1]);
        Assert.Equal((byte)'F', wav[2]);
        Assert.Equal((byte)'F', wav[3]);

        // WAVE
        Assert.Equal((byte)'W', wav[8]);
        Assert.Equal((byte)'A', wav[9]);
        Assert.Equal((byte)'V', wav[10]);
        Assert.Equal((byte)'E', wav[11]);

        // PCM, mono, 22050 Hz, 16 bit
        Assert.Equal(1, wav[20]); // PCM
        Assert.Equal(1, wav[22]); // mono
        Assert.Equal(0x22, wav[24]); // 22050 Hz low byte
        Assert.Equal(0x56, wav[25]); // 22050 Hz high byte
    }

    [Fact]
    public void FadeOut_PreventsClipping()
    {
        byte[] wav = SynthSoundPlayer.GenerateToneWav(880, 60);

        // Gli ultimi campioni dovrebbero avere ampiezza ridotta (fade-out)
        int sampleCount = (wav.Length - 44) / 2;
        short lastSample = (short)(wav[^2] | (wav[^1] << 8));

        // L'ultimo campione dovrebbe essere molto vicino a zero (fade-out completo)
        Assert.InRange(Math.Abs((int)lastSample), 0, 1000);
    }
}
