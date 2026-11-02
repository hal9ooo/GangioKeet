using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class WaveformHistoryTests
{
    [Fact]
    public void NewHistoryStartsAsAFlatLine()
    {
        WaveformHistory h = new();

        Assert.Equal(WaveformHistory.BarCount, h.Levels.Count);
        Assert.All(h.Levels, level => Assert.Equal(0, level));
        Assert.False(h.IsSilent);
    }

    [Fact]
    public void Push_QuantizesIntoFourBands()
    {
        WaveformHistory h = new();
        h.Push(0.01);
        h.Push(0.10);
        h.Push(0.30);
        h.Push(0.90);

        Assert.Equal(new byte[] { 0, 1, 2, 3 }, h.Levels.TakeLast(4));
    }

    [Fact]
    public void Silence_KeepsTheHistoryAndLetsBarsRest()
    {
        WaveformHistory h = new();
        h.Push(0.9);

        for (int i = 0; i < 8; i++)
        {
            h.Push(0.0);
        }

        Assert.True(h.IsSilent);
        Assert.Equal(WaveformHistory.BarCount, h.Levels.Count);
        Assert.Equal(2, h.Levels[^1]);
    }

    [Fact]
    public void Silence_AfterAWholeRing_IsAllZero()
    {
        WaveformHistory h = new();
        h.Push(0.9);

        for (int i = 0; i < WaveformHistory.BarCount * 3; i++)
        {
            h.Push(0.0);
        }

        Assert.True(h.IsSilent);
        Assert.Equal(WaveformHistory.BarCount, h.Levels.Count);
        Assert.All(h.Levels, level => Assert.Equal(0, level));
    }

    [Fact]
    public void Ring_DropsOldest()
    {
        WaveformHistory h = new();
        for (int i = 0; i < WaveformHistory.BarCount + 5; i++)
        {
            h.Push(0.9);
        }

        Assert.Equal(WaveformHistory.BarCount, h.Levels.Count);
    }

    [Fact]
    public void SilenceNeedsFourHundredMillisecondsEvenWithManySamples()
    {
        WaveformHistory h = new();
        h.Push(0.9);
        for (int i = 0; i < 8; i++)
        {
            h.Push(0.0, TimeSpan.FromMilliseconds(16.7));
        }

        Assert.False(h.IsSilent);
    }

    [Fact]
    public void ReleaseUsesElapsedTime()
    {
        WaveformHistory h = new();
        h.Push(0.9);
        for (int i = 0; i < 30; i++)
        {
            h.Push(0.02, TimeSpan.FromMilliseconds(8));
        }

        Assert.Equal(2, h.Levels[^1]);
    }

    [Fact]
    public void Release_FallsSlowerThanAttack()
    {
        WaveformHistory h = new();
        h.Push(0.9);
        h.Push(0.02);

        // 0.9 * 0.85 = 0.765 → ancora banda 3, non scende subito.
        Assert.Equal(3, h.Levels[^1]);

        h.Push(0.02);
        h.Push(0.02);
        h.Push(0.02);
        h.Push(0.02);

        // Dopo cinque campioni in rilascio è sceso sotto 0.45.
        Assert.Equal(2, h.Levels[^1]);
        Assert.False(h.IsSilent);
    }
}
