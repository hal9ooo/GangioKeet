using GangioKeet.Client.Configuration;
using GangioKeet.Client.Controller;
using GangioKeet.Client.Platform;
using Xunit;

namespace GangioKeet.Client.Tests.Configuration;

public sealed class ReloadPolicyTests
{
    [Theory]
    [InlineData(ClientState.Idle, ReloadOutcome.Applied)]
    [InlineData(ClientState.Connecting, ReloadOutcome.Deferred)]
    [InlineData(ClientState.Recording, ReloadOutcome.Deferred)]
    [InlineData(ClientState.Processing, ReloadOutcome.Deferred)]
    [InlineData(ClientState.Pasting, ReloadOutcome.Deferred)]
    public void Evaluate_AppliesOnlyInIdle(ClientState state, ReloadOutcome expected)
    {
        Assert.Equal(expected, ReloadPolicy.Evaluate(state));
    }

    [Fact]
    public void InvalidReload_ReturnsNullAndDoesNotThrow()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"gangio_{Guid.NewGuid():N}.yaml");

        ClientConfig? result = ReloadPolicy.TryLoad(missing, logger: null);

        Assert.Null(result);
    }

    [Fact]
    public void InvalidReload_KeepsWorkingConfigAvailableToCaller()
    {
        ClientConfig working = new();

        ClientConfig? reloaded = ReloadPolicy.TryLoad(
            Path.Combine(Path.GetTempPath(), $"gangio_{Guid.NewGuid():N}.yaml"),
            logger: null);

        Assert.Null(reloaded);
        Assert.Equal("websocket", working.Server.Transport);
        Assert.Equal(8000, working.Server.Port);
    }
}
