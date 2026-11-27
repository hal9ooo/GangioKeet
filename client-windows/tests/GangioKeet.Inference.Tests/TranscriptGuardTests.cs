using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class TranscriptGuardTests
{
    [Fact]
    public void RejectsTranslationFromNonLatinToLatin() =>
        AssertRejected("呃，这些湖南。", "Questi sono.", "cleaner_language_switch");

    [Fact]
    public void AcceptsSameScriptCleanup() =>
        AssertAccepted("いいよ、いいよ、何でもいいよ。", "いいよ、いいよ、何でもいいよ。");

    [Fact]
    public void AcceptsLatinWithTechnicalCyrillic() =>
        AssertAccepted("Piva piva lolio doliva пива piwa", "Piva piva lolio doliva пива piwa");

    [Fact]
    public void RejectsEmptyOutputForSubstantiveSpeech() =>
        AssertRejected("Ehm, scrivimi una funzione", "", "cleaner_empty_output");

    [Fact]
    public void AcceptsEmptyOutputForFillerOnly() =>
        AssertAccepted("Ehm, mmh", "");

    private static void AssertRejected(string raw, string cleaned, string warning)
    {
        Assert.False(TranscriptGuard.Evaluate(raw, cleaned, out string actual));
        Assert.Equal(warning, actual);
    }

    private static void AssertAccepted(string raw, string cleaned)
    {
        Assert.True(TranscriptGuard.Evaluate(raw, cleaned, out string actual));
        Assert.Equal("", actual);
    }
}
