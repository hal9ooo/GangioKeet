using GangioKeet.Client.Protocol;
using Xunit;

namespace GangioKeet.Client.Tests.Protocol;

public sealed class RestFallbackPolicyTests
{
    private readonly RestFallbackPolicy _policy = new();

    [Theory]
    [InlineData("not_found", 404)]
    [InlineData("unexpected_response", 405)]
    [InlineData("upgrade_required", 426)]
    public void CanFallback_TrueForProxyAndUpgradeRequiredHandshake(string code, int status)
    {
        Assert.True(_policy.CanFallback(new ConnectFailure(code, $"HTTP {status}.", status), anyStartOrAudioSent: false));
    }

    [Fact]
    public void CanFallback_TrueForHttp200WithoutUpgrade()
    {
        ConnectFailure failure = new("unexpected_response", "WebSocket handshake got HTTP 200 without upgrade.", 200);
        Assert.True(_policy.CanFallback(failure, anyStartOrAudioSent: false));
    }

    [Fact]
    public void CanFallback_FalseForUnauthorized()
    {
        ConnectFailure failure = new("unauthorized", "HTTP 401.", 401);
        Assert.False(_policy.CanFallback(failure, anyStartOrAudioSent: false));
    }

    [Fact]
    public void CanFallback_FalseForForbidden()
    {
        ConnectFailure failure = new("forbidden", "HTTP 403.", 403);
        Assert.False(_policy.CanFallback(failure, anyStartOrAudioSent: false));
    }

    [Theory]
    [InlineData("connect_failed", "Unable to resolve the host.")]
    [InlineData("connect_failed", "Connection refused.")]
    [InlineData("timeout", "The REST request did not complete within its deadline.")]
    public void CanFallback_FalseForNetworkLevelFailures(string code, string message)
    {
        // DNS fallito, connessione rifiutata e timeout (deadline di connect/upload/risposta):
        // arrivano come connect_failed/timeout, spesso senza HttpStatus.
        Assert.False(_policy.CanFallback(new ConnectFailure(code, message, null), anyStartOrAudioSent: false));
    }

    [Fact]
    public void CanFallback_FalseForUnexpectedResponseWithoutHttpStatus()
    {
        ConnectFailure failure = new("unexpected_response", "Handshake did not return an HTTP response.", null);
        Assert.False(_policy.CanFallback(failure, anyStartOrAudioSent: false));
    }

    [Fact]
    public void CanFallback_FalseForBusy()
    {
        ConnectFailure failure = new("busy", "Server busy.", 503);
        Assert.False(_policy.CanFallback(failure, anyStartOrAudioSent: false));
    }

    [Fact]
    public void CanFallback_FalseForPayloadTooLarge()
    {
        ConnectFailure failure = new("payload_too_large", "HTTP 413.", 413);
        Assert.False(_policy.CanFallback(failure, anyStartOrAudioSent: false));
    }

    [Fact]
    public void CanFallback_FalseForOtherUnexpectedHttpResponses()
    {
        Assert.False(_policy.CanFallback(
            new ConnectFailure("unexpected_response", "HTTP 502.", 502),
            anyStartOrAudioSent: false));
    }

    [Theory]
    [InlineData("not_found", 404)]
    [InlineData("unexpected_response", 405)]
    [InlineData("upgrade_required", 426)]
    [InlineData("unexpected_response", 200)]
    public void CanFallback_FalseOnceStartOrAudioWasSent(string code, int status)
    {
        Assert.False(_policy.CanFallback(new ConnectFailure(code, $"HTTP {status}.", status), anyStartOrAudioSent: true));
    }

    [Fact]
    public void CanFallback_FalseOnceStartOrAudioWasSentForNetworkFailures()
    {
        Assert.False(_policy.CanFallback(
            new ConnectFailure("connect_failed", "Connection refused.", null),
            anyStartOrAudioSent: true));
    }
}
