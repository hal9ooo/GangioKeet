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
            ["psic"] = "DeepSeek",
            ["open code"] = "OpenCode",
        },
    };

    [Theory]
    [InlineData("uso ci per inference", "uso Cheaper Inference")]
    [InlineData("uso Ci per inference", "uso Cheaper Inference")]
    [InlineData("uso cyper   inference", "uso Cheaper Inference")]
    [InlineData("uso PSIC", "uso DeepSeek")]
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
}
