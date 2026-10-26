using System.Runtime.InteropServices;

namespace GangioKeet.Client.Paste;

/// <summary>
/// Combo di paste via SendInput: modificatori premuti, tasto, rilascio del tasto e — sempre, anche
/// in caso di invio parziale — rilascio dei modificatori. Nessun Enter: il paste non invia mai.
/// </summary>
public sealed class SendInputSimulator : IInputSimulator
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    public bool SendCombo(string combo)
    {
        if (!KeyCombo.TryParse(combo, out ushort[] modifiers, out ushort key))
        {
            return false;
        }

        INPUT[] presses = new INPUT[modifiers.Length + 1];
        for (int i = 0; i < modifiers.Length; i++)
        {
            presses[i] = CreateKeyInput(modifiers[i], keyUp: false);
        }

        presses[^1] = CreateKeyInput(key, keyUp: false);

        INPUT[] releases = new INPUT[modifiers.Length + 1];
        releases[0] = CreateKeyInput(key, keyUp: true);
        for (int i = 0; i < modifiers.Length; i++)
        {
            releases[i + 1] = CreateKeyInput(modifiers[modifiers.Length - 1 - i], keyUp: true);
        }

        try
        {
            uint sent = SendInput((uint)presses.Length, presses, Marshal.SizeOf<INPUT>());
            if (sent != presses.Length)
            {
                return false;
            }
        }
        finally
        {
            SendInput((uint)releases.Length, releases, Marshal.SizeOf<INPUT>());
        }

        return true;
    }

    private static INPUT CreateKeyInput(ushort virtualKey, bool keyUp) => new()
    {
        Type = InputKeyboard,
        Data = new InputUnion
        {
            Keyboard = new KeyboardEvent
            {
                VirtualKey = virtualKey,
                Flags = keyUp ? KeyEventKeyUp : 0,
            },
        },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseEvent Mouse;

        [FieldOffset(0)]
        public KeyboardEvent Keyboard;

        [FieldOffset(0)]
        public HardwareEvent Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseEvent
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardEvent
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HardwareEvent
    {
        public uint Message;
        public ushort ParamLow;
        public ushort ParamHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}

/// <summary>Parsing ristretto delle combo configurate: modificatori (ctrl/shift/alt/win) + un tasto.</summary>
public static class KeyCombo
{
    private const ushort VkControl = 0x11;
    private const ushort VkShift = 0x10;
    private const ushort VkMenu = 0x12;
    private const ushort VkLeftWindows = 0x5B;

    public static bool TryParse(string combo, out ushort[] modifiers, out ushort key)
    {
        modifiers = [];
        key = 0;
        if (string.IsNullOrWhiteSpace(combo))
        {
            return false;
        }

        string[] parts = combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        List<ushort> parsedModifiers = [];
        for (int i = 0; i < parts.Length - 1; i++)
        {
            ushort? modifier = parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => VkControl,
                "shift" => VkShift,
                "alt" => VkMenu,
                "win" or "windows" => VkLeftWindows,
                _ => null,
            };

            if (modifier is null)
            {
                return false;
            }

            parsedModifiers.Add(modifier.Value);
        }

        string keyName = parts[^1];
        if (keyName.Length != 1)
        {
            return false;
        }

        char character = char.ToUpperInvariant(keyName[0]);
        if (character is >= 'A' and <= 'Z' or >= '0' and <= '9')
        {
            key = character;
            modifiers = parsedModifiers.ToArray();
            return true;
        }

        return false;
    }
}
