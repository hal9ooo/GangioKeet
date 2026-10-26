using GangioKeet.Client.Hotkey;
using Xunit;

namespace GangioKeet.Client.Tests.Hotkey;

public sealed class HookSuppressionPolicyTests
{
    private const int F8 = 0x77;
    private const int Escape = 0x1B;
    private const int LetterJ = 0x4A;
    private const int MouseX1 = 1;

    private static HookSuppressionPolicy BuildPolicy() => new("f8", "escape");

    [Fact]
    public void HotkeyDownAndUp_AreAlwaysSuppressedAndEmitted()
    {
        HookSuppressionPolicy policy = BuildPolicy();

        HookDecision down = policy.Decide(F8, isDown: true, fromMouse: false);
        HookDecision up = policy.Decide(F8, isDown: false, fromMouse: false);

        Assert.Equal(new HookDecision(Suppress: true, Emit: true), down);
        Assert.Equal(new HookDecision(Suppress: true, Emit: true), up);
    }

    [Fact]
    public void UnrelatedKeys_PassThroughAndAreNotEmitted()
    {
        HookSuppressionPolicy policy = BuildPolicy();
        policy.AbortActive = true;

        HookDecision down = policy.Decide(LetterJ, isDown: true, fromMouse: false);
        HookDecision up = policy.Decide(LetterJ, isDown: false, fromMouse: false);

        Assert.Equal(new HookDecision(false, false), down);
        Assert.Equal(new HookDecision(false, false), up);
    }

    [Fact]
    public void EscapeDuringActiveSession_ConsumesDownAndKeepsUpSuppressedAfterAbortEnds()
    {
        HookSuppressionPolicy policy = BuildPolicy();
        policy.AbortActive = true;

        HookDecision down = policy.Decide(Escape, isDown: true, fromMouse: false);
        Assert.Equal(new HookDecision(Suppress: true, Emit: true), down);

        policy.AbortActive = false;
        HookDecision up = policy.Decide(Escape, isDown: false, fromMouse: false);
        Assert.Equal(new HookDecision(Suppress: true, Emit: true), up);

        // La coppia consumata non lascia stato residuo: in Idle Esc passa di nuovo.
        Assert.Equal(new HookDecision(false, false), policy.Decide(Escape, isDown: false, fromMouse: false));
        Assert.Equal(new HookDecision(false, false), policy.Decide(Escape, isDown: true, fromMouse: false));
    }

    [Fact]
    public void EscapeInIdle_PassesCompletely()
    {
        HookSuppressionPolicy policy = BuildPolicy();

        Assert.Equal(new HookDecision(false, false), policy.Decide(Escape, isDown: true, fromMouse: false));
        Assert.Equal(new HookDecision(false, false), policy.Decide(Escape, isDown: false, fromMouse: false));
    }

    [Fact]
    public void RepeatedHotkeyDown_EmitsOnlyOneDown()
    {
        HookSuppressionPolicy policy = BuildPolicy();

        Assert.True(policy.Decide(F8, isDown: true, fromMouse: false).Emit);
        HookDecision repeat = policy.Decide(F8, isDown: true, fromMouse: false);
        Assert.Equal(new HookDecision(Suppress: true, Emit: false), repeat);

        Assert.True(policy.Decide(F8, isDown: false, fromMouse: false).Emit);
        Assert.True(policy.Decide(F8, isDown: true, fromMouse: false).Emit);
    }

    [Fact]
    public void MouseHotkey_OnlyHandlesMouseVariant()
    {
        HookSuppressionPolicy policy = new("mouse_x1", "escape");
        Assert.True(policy.InstallsMouseHook);

        Assert.Equal(new HookDecision(true, true), policy.Decide(MouseX1, isDown: true, fromMouse: true));
        Assert.Equal(new HookDecision(true, true), policy.Decide(MouseX1, isDown: false, fromMouse: true));
        Assert.Equal(new HookDecision(false, false), policy.Decide(MouseX1, isDown: true, fromMouse: false));
    }

    [Fact]
    public void KeyboardHotkey_DoesNotInstallMouseHook()
    {
        HookSuppressionPolicy policy = BuildPolicy();

        Assert.False(policy.InstallsMouseHook);
        Assert.Equal(new HookDecision(false, false), policy.Decide(MouseX1, isDown: true, fromMouse: true));
    }

    [Fact]
    public void Reset_DiscardsPartialKeyState()
    {
        HookSuppressionPolicy policy = BuildPolicy();
        policy.AbortActive = true;
        policy.Decide(F8, isDown: true, fromMouse: false);
        policy.Decide(Escape, isDown: true, fromMouse: false);

        policy.Reset();
        policy.AbortActive = false;

        // Dopo il reset un nuovo down è un gesto nuovo e Esc torna passante.
        Assert.True(policy.Decide(F8, isDown: true, fromMouse: false).Emit);
        Assert.False(policy.Decide(Escape, isDown: true, fromMouse: false).Suppress);
    }
}
