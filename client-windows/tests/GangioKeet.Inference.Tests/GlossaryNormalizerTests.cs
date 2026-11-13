using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class GlossaryNormalizerTests
{
    private static CleanupPolicy Policy() => new()
    {
        CanonicalTerms = ["Proxmox VE", "OpenCode"],
        PhoneticOverrides = new()
        {
            ["ci per inference"] = "Cheaper Inference",
            ["cyper inference"] = "Cheaper Inference",
            ["open code"] = "OpenCode",
        },
        ContextualCorrections =
        [
            new() { From = "psic", To = "DeepSeek", Context = @"(?i)\b(?:di|da|su|con|per)\s+(?:word)\b|\b(?:word)\s+\d{2,4}\b" },
            new() { From = "cloud", To = "Claude", Context = @"(?i)\b(?:con|su|e|da)\s+(?:word)\b(?![^.]{0,12}\b(?:code|storage|native)\b)" },
        ],
    };

    [Theory]
    [InlineData("uso ci per inference", "uso Cheaper Inference")]
    [InlineData("uso Ci per inference", "uso Cheaper Inference")]
    [InlineData("uso cyper   inference", "uso Cheaper Inference")]
    public void RewritesPhoneticVariants(string raw, string expected) =>
        Assert.Equal(expected, GlossaryNormalizer.Normalize(raw, Policy()));

    [Fact]
    public void NormalisesCanonicalCasing() =>
        Assert.Equal("Proxmox VE e OpenCode",
            GlossaryNormalizer.Normalize("proxmox ve e opencode", Policy()));

    [Fact]
    public void LongestKeyWins() =>
        Assert.Equal("Cheaper Inference", GlossaryNormalizer.Normalize("ci per inference", Policy()));

    [Fact]
    public void DoesNotMatchInsideWords() =>
        Assert.Equal("cyperinferences", GlossaryNormalizer.Normalize("cyperinferences", Policy()));

    [Fact]
    public void DoesNotTouchTechnicalProfiles()
    {
        Assert.Equal("ci per inference", GlossaryNormalizer.Normalize("ci per inference", Policy(), "code"));
        Assert.Equal("ci per inference", GlossaryNormalizer.Normalize("ci per inference", Policy(), "terminal"));
    }

    [Fact]
    public void EmptyPolicyLeavesTextUntouched() =>
        Assert.Equal("ci per inference", GlossaryNormalizer.Normalize("ci per inference", new CleanupPolicy()));

    // Il caso che ha motivato la guardia: "psicologo" non deve diventare "DeepSeekologo".
    [Fact]
    public void ContextGuardProtectsWordsThatMerelyStartWithTheForm()
    {
        Assert.Equal("vado dallo psicologo domani", GlossaryNormalizer.Normalize("vado dallo psicologo domani", Policy()));
        Assert.Equal("lo psicologo mi ha detto di si", GlossaryNormalizer.Normalize("lo psicologo mi ha detto di si", Policy()));
    }

    [Theory]
    [InlineData("lancio agenti di psic", "lancio agenti di DeepSeek")]
    [InlineData("esce psic 1217 domani", "esce DeepSeek 1217 domani")]
    public void ContextGuardAllowsThePhraseWeActuallyDictate(string raw, string expected) =>
        Assert.Equal(expected, GlossaryNormalizer.Normalize(raw, Policy()));

    [Theory]
    [InlineData("voglio tutto cloud, senza locale")]
    [InlineData("cloud storage per i backup")]
    [InlineData("cloud code invece no")]
    public void CloudStaysCloudWithoutAnAgentContext(string raw) =>
        Assert.Equal(raw, GlossaryNormalizer.Normalize(raw, Policy()));

    [Fact]
    public void CloudBecomesClaudeNextToAnAgent()
    {
        Assert.Equal("interagendo con te e con Claude, con Codex",
            GlossaryNormalizer.Normalize("interagendo con te e con Cloud, con Codex", Policy()));
    }
}
