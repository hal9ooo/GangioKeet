using GangioKeet.Client.Hotkey;
using Xunit;

namespace GangioKeet.Client.Tests.Hotkey;

public sealed class KeyCodesTests
{
    [Theory]
    [InlineData("capslock")]
    [InlineData("caps_lock")]
    [InlineData("CapsLock")]
    public void Parse_AcceptsCapsLock(string name)
    {
        KeyCode code = KeyCodes.Parse(name);

        Assert.Equal(0x14, code.VirtualKey);
        Assert.False(code.FromMouse);
    }

    [Fact]
    public void Parse_StillRejectsUnknownKeys()
    {
        Assert.Throws<ArgumentException>(() => KeyCodes.Parse("caps"));
    }
}
