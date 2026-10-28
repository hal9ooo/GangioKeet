using System.Runtime.InteropServices;

namespace GangioKeet.Client.Hotkey;

public interface ICapsLockResetService
{
    bool IsCapsLockActive();
    bool ResetIfActive();
}

/// <summary>
/// Controlla e spegne Caps Lock all'avvio prima dell'installazione degli hook di input.
/// </summary>
public sealed class CapsLockResetService : ICapsLockResetService
{
    private const int VkCapital = 0x14;
    private const byte ScanCodeCapital = 0x3A; // Scan code standard Win32 per Caps Lock
    private const uint KeyEventKeyUp = 0x0002;

    private readonly Func<bool> _stateReader;
    private readonly Action _keySender;

    public CapsLockResetService(Func<bool>? stateReader = null, Action? keySender = null)
    {
        _stateReader = stateReader ?? DefaultIsCapsLockActive;
        _keySender = keySender ?? DefaultSendCapsLockToggle;
    }

    public bool IsCapsLockActive() => _stateReader();

    public bool ResetIfActive()
    {
        if (!_stateReader())
        {
            return false;
        }

        _keySender();
        return !_stateReader();
    }

    private static bool DefaultIsCapsLockActive()
    {
        return (GetKeyState(VkCapital) & 0x0001) != 0;
    }

    private static void DefaultSendCapsLockToggle()
    {
        keybd_event((byte)VkCapital, ScanCodeCapital, 0, UIntPtr.Zero);
        keybd_event((byte)VkCapital, ScanCodeCapital, KeyEventKeyUp, UIntPtr.Zero);
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
    private static extern short GetKeyState(int keyCode);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);
}
