using GangioKeet.Client.Configuration;
using Xunit;

namespace GangioKeet.Client.Tests.Api;

public sealed class ApiConfigTests
{
    private const string DefaultEnv = "GANGIOKEET_API_TOKEN";
    private static string? NoEnv(string _) => null;
    private static string? WithEnv(string name) => name == DefaultEnv ? "segreto" : null;

    [Fact]
    public void LoopbackOnlyNeedsNoToken() =>
        Assert.Null(ApiTokenPolicy.RequiredFor(["127.0.0.1"], DefaultEnv, NoEnv));

    [Fact]
    public void NetworkAddressRequiresAToken() =>
        Assert.Equal("segreto", ApiTokenPolicy.RequiredFor(["0.0.0.0"], DefaultEnv, WithEnv));

    [Fact]
    public void NetworkAddressWithoutTokenIsRefused() =>
        Assert.Throws<InvalidOperationException>(() =>
            ApiTokenPolicy.RequiredFor(["0.0.0.0"], DefaultEnv, NoEnv));

    [Fact]
    public void UnknownListenAddressIsRefused() =>
        Assert.Throws<InvalidOperationException>(() =>
            ApiTokenPolicy.RequiredFor(["example.com"], DefaultEnv, WithEnv));

    [Fact]
    public void AConfiguredVariableNameIsTheOneRead()
    {
        // TokenEnv è configurabile: se la policy indovinasse il nome predefinito,
        // un token messo in MY_TOKEN verrebbe rifiutato senza spiegazione.
        Assert.Equal("altro", ApiTokenPolicy.RequiredFor(["0.0.0.0"], "MY_TOKEN",
            name => name == "MY_TOKEN" ? "altro" : null));
    }

    [Fact]
    public void DefaultsMatchTheAgreedContract()
    {
        ApiConfig api = new();
        Assert.False(api.Enabled);
        Assert.Equal(["127.0.0.1"], api.Listen);
        Assert.Equal(5099, api.Port);
        Assert.Equal("gangio-keet", api.ModelAlias);
        Assert.Equal("GANGIOKEET_API_TOKEN", api.TokenEnv);
    }
}
