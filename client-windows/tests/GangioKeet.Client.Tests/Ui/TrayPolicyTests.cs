using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class TrayPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public void Retry_RequiresRecoveryAndIdleSession(bool hasRecovery, bool isActive, bool expected)
    {
        Assert.Equal(expected, RecoveryMenuPolicy.CanRetry(hasRecovery, isActive));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Copy_RequiresAnyLastText(bool hasText, bool expected)
    {
        Assert.Equal(expected, RecoveryMenuPolicy.CanCopy(hasText));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Discard_RequiresARecoveryItem(bool hasRecovery, bool expected)
    {
        Assert.Equal(expected, RecoveryMenuPolicy.CanDiscard(hasRecovery));
    }
}
