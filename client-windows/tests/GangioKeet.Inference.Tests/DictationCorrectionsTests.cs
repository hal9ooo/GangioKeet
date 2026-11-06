using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class DictationCorrectionsTests
{
    // Le stesse regole di default di cleanup.example.yaml.
    private static CleanupPolicy Policy() => new()
    {
        ElisionCorrections =
        [
            new() { From = "con la gente", To = "con l'agente" },
            new() { From = "nella gente", To = "nell'agente" },
            new() { From = "della gente", To = "dell'agente" },
            new() { From = "dalla gente", To = "dall'agente" },
            new() { From = "sulla gente", To = "sull'agente" },
            new() { From = "alla gente", To = "all'agente" },
            new() { From = "per la gente", To = "per l'agente" },
            new()
            {
                From = "la gente",
                To = "l'agente",
                KeepWhenPrecededBy = ["molta", "tanta", "poca", "altra", "qualche", "un sacco di"],
                KeepWhenFollowedBy = ["comune", "del posto"],
            },
        ],
    };

    [Fact]
    public void CorrectsTheElidedPrepositionToo() =>
        Assert.Equal(
            "un prompt che io lo incollo nell'agente e l'agente sa cosa deve fare",
            DictationCorrections.Apply(
                "un prompt che io lo incollo nella gente e la gente sa cosa deve fare", Policy()));

    [Fact]
    public void KeepsCapitalisation() =>
        Assert.Equal("L'agente sa cosa deve fare",
            DictationCorrections.Apply("La gente sa cosa deve fare", Policy()));

    [Fact]
    public void CorrectsTheBareForm() =>
        Assert.Equal("fai girare l'agente windows",
            DictationCorrections.Apply("fai girare la gente windows", Policy()));

    [Theory]
    [InlineData("dalla gente")]
    [InlineData("alla gente")]
    [InlineData("sulla gente")]
    [InlineData("per la gente")]
    public void CorrectsEveryPreposition(string raw) =>
        Assert.Equal(raw.Replace("la gente", "l'agente"),
            DictationCorrections.Apply(raw, Policy()));

    [Theory]
    [InlineData("c'era molta gente alla fiera")]
    [InlineData("c'era un sacco di gente")]
    [InlineData("la gente comune non capisce")]
    [InlineData("parliamo della gente del posto")]
    public void LeavesPeopleAlone(string raw) =>
        Assert.Equal(raw, DictationCorrections.Apply(raw, Policy()));

    [Fact]
    public void DoesNotTouchTechnicalProfiles()
    {
        const string raw = "la gente";
        Assert.Equal(raw, DictationCorrections.Apply(raw, Policy(), "code"));
        Assert.Equal(raw, DictationCorrections.Apply(raw, Policy(), "terminal"));
        Assert.Equal("l'agente", DictationCorrections.Apply(raw, Policy(), "chat"));
    }

    [Fact]
    public void DoesNotMatchInsideWords() =>
        Assert.Equal("la gentex", DictationCorrections.Apply("la gentex", Policy()));

    [Fact]
    public void EmptyPolicyLeavesTextUntouched() =>
        Assert.Equal("la gente", DictationCorrections.Apply("la gente", new CleanupPolicy()));
}
