using GangioKeet.Client.Audio;
using Xunit;

namespace GangioKeet.Client.Tests.Audio;

public sealed class AudioSignalTrackerTests
{
    private static byte[] ToPcm16(params short[] samples)
    {
        return Pcm16.FromFloat(samples.Select(s => s / 32768f).ToArray());
    }

    [Fact]
    public void BuildSummary_NoSamplesEver_DistinguishesZeroSampleRecording()
    {
        AudioSignalTracker tracker = new();

        AudioSummary summary = tracker.BuildSummary();

        Assert.Equal(0, summary.SampleCount);
        Assert.False(summary.AllZero);
        Assert.Equal(0, summary.Peak);
    }

    [Fact]
    public void AppendPcm16_AllZeroSamples_MarksAllZeroWithPositiveCount()
    {
        AudioSignalTracker tracker = new();

        double level = tracker.AppendPcm16(new byte[200]);

        Assert.Equal(0, level);
        AudioSummary summary = tracker.BuildSummary();
        Assert.Equal(100, summary.SampleCount);
        Assert.True(summary.AllZero);
        Assert.Equal(0, summary.Peak);
    }

    [Fact]
    public void AppendPcm16_WeakVoice_KeptWithNonZeroPeakAndAllZeroFalse()
    {
        AudioSignalTracker tracker = new();
        byte[] weak = ToPcm16(Enumerable.Repeat<short>(3, 50).ToArray());

        double level = tracker.AppendPcm16(weak);

        // La voce debole non viene scartata: il livello resta > 0 nel marker.
        Assert.Equal(3.0 / 32768.0, level);
        AudioSummary summary = tracker.BuildSummary();
        Assert.Equal(50, summary.SampleCount);
        Assert.False(summary.AllZero);
        Assert.Equal(3.0 / 32768.0, summary.Peak);
    }

    [Fact]
    public void AppendPcm16_MixedSilenceAndVoice_AllZeroIsFalseWithAnySampleNotZero()
    {
        AudioSignalTracker tracker = new();
        tracker.AppendPcm16(new byte[20]);

        tracker.AppendPcm16(ToPcm16(1));

        AudioSummary summary = tracker.BuildSummary();
        Assert.Equal(11, summary.SampleCount);
        Assert.False(summary.AllZero);
        Assert.Equal(1.0 / 32768.0, summary.Peak);
    }

    [Fact]
    public void AppendPcm16_LevelReflectsChunkPeakNotCumulativeHistory()
    {
        AudioSignalTracker tracker = new();

        Assert.Equal(1.0, tracker.AppendPcm16(ToPcm16(short.MaxValue, short.MinValue)));
        Assert.Equal(0, tracker.AppendPcm16(new byte[10]));

        Assert.Equal(0.25, tracker.AppendPcm16(ToPcm16(8192)));
        AudioSummary summary = tracker.BuildSummary();
        Assert.Equal(8, summary.SampleCount);
        Assert.False(summary.AllZero);
        Assert.Equal(1.0, summary.Peak);
    }

    [Fact]
    public void AppendPcm16_OddByteCountCarriesPartialSampleToNextCall()
    {
        AudioSignalTracker tracker = new();

        tracker.AppendPcm16(new byte[] { 0x01, 0x02, 0x03 });
        tracker.AppendPcm16(new byte[] { 0x04, 0x05, 0x06 });

        // Campioni little endian: 0x0201, 0x0403, 0x0605.
        AudioSummary summary = tracker.BuildSummary();
        Assert.Equal(3, summary.SampleCount);
        Assert.Equal((double)HexValue(0x05, 0x06) / 32768.0, summary.Peak);
    }

    private static int HexValue(byte low, byte high) => low | (high << 8);
}
