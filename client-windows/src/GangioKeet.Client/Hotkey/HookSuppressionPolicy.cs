namespace GangioKeet.Client.Hotkey;

/// <summary>Esito puro per un evento raw: sopprimere il tasto e inoltrarlo al controller.</summary>
public readonly record struct HookDecision(bool Suppress, bool Emit);

/// <summary>
/// Soppressione per coppia down/up: l'hotkey (F1–F24 o mouse_x1) è sempre soppresso e produce un
/// solo gesto per pressione (autorepeat filtrato); Esc è consumato solo durante una sessione attiva
/// e il suo keyup resta soppresso anche se la sessione è già finita, così l'app non riceve una
/// pressione monca.
/// </summary>
public sealed class HookSuppressionPolicy
{
    private readonly object _gate = new();
    private readonly int _hotkeyVirtualKey;
    private readonly bool _hotkeyIsMouse;
    private readonly int _abortVirtualKey;

    private bool _hotkeyDown;
    private bool _abortDownConsumed;
    private bool _abortActive;

    public HookSuppressionPolicy(string hotkey, string abortKey)
        : this(KeyCodes.Parse(hotkey), KeyCodes.Parse(abortKey))
    {
    }

    public HookSuppressionPolicy(KeyCode hotkey, KeyCode abortKey)
        : this(hotkey.VirtualKey, abortKey.VirtualKey, hotkey.FromMouse)
    {
    }

    public HookSuppressionPolicy(int hotkeyVirtualKey, int abortVirtualKey, bool hotkeyIsMouse = false)
    {
        _hotkeyVirtualKey = hotkeyVirtualKey;
        _abortVirtualKey = abortVirtualKey;
        _hotkeyIsMouse = hotkeyIsMouse;
    }

    public bool AbortActive
    {
        get => Volatile.Read(ref _abortActive);
        set => Volatile.Write(ref _abortActive, value);
    }

    public bool InstallsMouseHook => _hotkeyIsMouse;

    public HookDecision Decide(int virtualKey, bool isDown, bool fromMouse)
    {
        lock (_gate)
        {
            if (fromMouse == _hotkeyIsMouse && virtualKey == _hotkeyVirtualKey)
            {
                if (isDown)
                {
                    if (_hotkeyDown)
                    {
                        return new HookDecision(Suppress: true, Emit: false);
                    }

                    _hotkeyDown = true;
                    return new HookDecision(Suppress: true, Emit: true);
                }

                _hotkeyDown = false;
                return new HookDecision(Suppress: true, Emit: true);
            }

            if (!fromMouse && virtualKey == _abortVirtualKey)
            {
                if (isDown)
                {
                    if (_abortDownConsumed)
                    {
                        return new HookDecision(Suppress: true, Emit: false);
                    }

                    if (_abortActive)
                    {
                        _abortDownConsumed = true;
                        return new HookDecision(Suppress: true, Emit: true);
                    }

                    return new HookDecision(Suppress: false, Emit: false);
                }

                if (_abortDownConsumed)
                {
                    _abortDownConsumed = false;
                    return new HookDecision(Suppress: true, Emit: true);
                }

                return new HookDecision(Suppress: false, Emit: false);
            }

            return new HookDecision(Suppress: false, Emit: false);
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _hotkeyDown = false;
            _abortDownConsumed = false;
        }
    }
}

/// <summary>Tasto configurato: virtual key Win32 e se arriva dal mouse.</summary>
public readonly record struct KeyCode(int VirtualKey, bool FromMouse);

/// <summary>Mappatura ristretta ai tasti ammessi dalla configurazione (F1–F24, mouse_x1, escape).</summary>
public static class KeyCodes
{
    public const int MouseX1 = 1;

    public static KeyCode Parse(string name)
    {
        string normalized = name.Trim().ToLowerInvariant();
        if (normalized == "mouse_x1")
        {
            return new KeyCode(MouseX1, FromMouse: true);
        }

        if (normalized is "capslock" or "caps_lock")
        {
            return new KeyCode(0x14, FromMouse: false);
        }

        if (normalized is "escape" or "esc")
        {
            return new KeyCode(0x1B, FromMouse: false);
        }

        if (normalized.Length is >= 2 and <= 3 && normalized[0] == 'f'
            && int.TryParse(normalized[1..], out int number) && number is >= 1 and <= 24)
        {
            return new KeyCode(0x70 + number - 1, FromMouse: false);
        }

        throw new ArgumentException($"Unsupported hotkey '{name}'.", nameof(name));
    }
}
