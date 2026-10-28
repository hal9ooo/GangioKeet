using GangioKeet.Client.Hotkey;
using Xunit;

namespace GangioKeet.Client.Tests.Hotkey;

public sealed class CapsLockResetServiceTests
{
    [Fact]
    public void IsCapsLockActive_ReturnsStateFromReader()
    {
        bool currentState = true;
        var service = new CapsLockResetService(stateReader: () => currentState);

        Assert.True(service.IsCapsLockActive());

        currentState = false;
        Assert.False(service.IsCapsLockActive());
    }

    [Fact]
    public void ResetIfActive_WhenInactive_ReturnsFalseAndDoesNotSendKey()
    {
        int sendCount = 0;
        var service = new CapsLockResetService(
            stateReader: () => false,
            keySender: () => sendCount++);

        bool result = service.ResetIfActive();

        Assert.False(result);
        Assert.Equal(0, sendCount);
    }

    [Fact]
    public void ResetIfActive_WhenActive_SendsKeyAndReturnsTrue()
    {
        int sendCount = 0;
        var service = new CapsLockResetService(
            stateReader: () => true,
            keySender: () => sendCount++);

        bool result = service.ResetIfActive();

        Assert.True(result);
        Assert.Equal(1, sendCount);
    }
}
