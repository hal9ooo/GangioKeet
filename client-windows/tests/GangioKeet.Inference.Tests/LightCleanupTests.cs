using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class LightCleanupTests
{
    [Theory]
    [InlineData("Ehm, scrivimi una funzione", "scrivimi una funzione")]
    [InlineData("Ehm, Scrivi una funzione", "Scrivi una funzione")]
    [InlineData("uhm, mi mi mandi", "mi mandi")]
    [InlineData("mmh", "")]
    public void RemovesOnlyExplicitFilledPauses(string raw, string expected) =>
        Assert.Equal(expected, LightCleanup.Apply(raw));

    [Theory]
    [InlineData("No, no, lo stack", "No, lo stack")]
    [InlineData("non, non includerli", "non includerli")]
    [InlineData("Metti, metti solo", "Metti solo")]
    public void CollapsesCommaSeparatedRepetitions(string raw, string expected) =>
        Assert.Equal(expected, LightCleanup.Apply(raw));

    [Theory]
    [InlineData("Mi mi mandi una lista", "Mi mandi una lista")]
    [InlineData("al al al nido", "al nido")]
    [InlineData("log log plc", "log plc")]
    [InlineData("no no no", "no")]
    [InlineData("Le le l m", "Le l m")]
    public void CollapsesShortRepetitionsButKeepsSingleLetters(string raw, string expected) =>
        Assert.Equal(expected, LightCleanup.Apply(raw));

    [Theory]
    [InlineData("S T T e L L M")]
    [InlineData("S, S")]
    [InlineData("2, 2")]
    [InlineData("12 12")]
    [InlineData("piano piano")]
    [InlineData("ciao ciao")]
    [InlineData("molto molto")]
    [InlineData("eccetera, eccetera")]
    [InlineData("tranquillo tranquillo")]
    [InlineData("troppo troppo")]
    public void PreservesAcronymsAndLegitimateReduplications(string raw) =>
        Assert.Equal(raw, LightCleanup.Apply(raw));

    [Theory]
    [InlineData("un una lista", "una lista")]
    [InlineData("Un una lista", "Una lista")]
    [InlineData("gli lo faccio", "lo faccio")]
    [InlineData("al allo stack", "allo stack")]
    [InlineData("sul nel Mac", "nel Mac")]
    public void KeepsTheLastArticleOrPrepositionForm(string raw, string expected) =>
        Assert.Equal(expected, LightCleanup.Apply(raw));

    [Theory]
    [InlineData("uno del piano")]
    [InlineData("quindi allora insomma tipo diciamo")]
    [InlineData("in pratica inoltre")]
    public void DoesNotRewriteDiscourseOrMixCategories(string raw) =>
        Assert.Equal(raw, LightCleanup.Apply(raw));

    [Fact]
    public void EmptyInputStaysEmpty() => Assert.Equal("", LightCleanup.Apply(""));
}
