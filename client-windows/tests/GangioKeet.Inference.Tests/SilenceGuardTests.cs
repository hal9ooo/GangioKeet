using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class SilenceGuardTests
{
    // I quattro testi brevi e comuni misurati su 1331 dettature reali: tutti
    // con picco sotto il 1% del fondo scala. Valori presi dai loro WAV.
    [Theory]
    [InlineData("you", 0)]
    [InlineData("Yeah.", 32)]
    [InlineData("Yeah.", 304)]
    [InlineData("Yeah.", 312)]
    public void MarksShortCommonTextOnAudioBelowSpeechFloor(string text, int peak) =>
        Assert.True(SilenceGuard.IsHallucination(text, peak));

    // Il picco da solo non basta a scartare: "Yeah." detto davvero, o un "you"
    // registrato a volume normale, è voce e va consegnata.
    [Theory]
    [InlineData("Yeah.", 3000)]
    [InlineData("you", 500)]
    [InlineData("Vai.", 3963)]
    [InlineData("Sì, fallo, grazie.", 4833)]
    public void LeavesRealSpeechUntouchedEvenWhenTheTextIsCommon(string text, int peak) =>
        Assert.False(SilenceGuard.IsHallucination(text, peak));

    [Fact]
    public void LeavesPhrasesAboveTheWordLimitUntouched() =>
        Assert.False(SilenceGuard.IsHallucination("Sì ok dai", 0));

    [Fact]
    public void LeavesUncommonWordsUntouched() =>
        Assert.False(SilenceGuard.IsHallucination("Stallo.", 0));

    [Fact]
    public void LeavesEmptyTextToTheExistingSilencePath() =>
        Assert.False(SilenceGuard.IsHallucination("   ", 0));

    [Fact]
    public void PeakOfIgnoresTrailingOddByte()
    {
        // 0x8000 = -32768: il valore assoluto eccede short e deve valere 32768.
        byte[] pcm = [0x00, 0x80];
        Assert.Equal(32768, SilenceGuard.PeakOf(pcm));
    }

    [Fact]
    public void PeakOfEmptyBufferIsZero() => Assert.Equal(0, SilenceGuard.PeakOf([]));
}
