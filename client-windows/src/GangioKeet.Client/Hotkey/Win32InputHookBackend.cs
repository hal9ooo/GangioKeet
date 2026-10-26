using System.ComponentModel;
using System.Runtime.InteropServices;

namespace GangioKeet.Client.Hotkey;

/// <summary>
/// Backend Win32: WH_KEYBOARD_LL sempre, WH_MOUSE_LL solo quando l'hotkey è mouse_x1. Installazione,
/// message loop e disinstallazione avvengono sullo stesso thread dedicato; <see cref="Stop"/> posta
/// WM_QUIT a quel thread. Nel callback si decodifica la struttura, si cattura la finestra attiva e
/// si restituisce subito l'esito di soppressione, senza lavoro non deterministico.
/// </summary>
public sealed class Win32InputHookBackend : IInputHookBackend
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmQuit = 0x0012;
    private const uint LlkhfInjected = 0x00000010;

    private readonly HookProc _keyboardProc;
    private readonly HookProc _mouseProc;

    private Func<RawInput, bool>? _onInput;
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private int _threadId;

    public Win32InputHookBackend()
    {
        _keyboardProc = KeyboardProc;
        _mouseProc = MouseProc;
    }

    public void Run(bool installMouseHook, Func<RawInput, bool> onInput)
    {
        _onInput = onInput;
        Volatile.Write(ref _threadId, GetCurrentThreadId());

        IntPtr module = GetModuleHandle(null);
        _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, module, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_KEYBOARD_LL) failed");
        }

        if (installMouseHook)
        {
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, module, 0);
            if (_mouseHook == IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetWindowsHookEx(WH_MOUSE_LL) failed");
            }
        }

        try
        {
            while (GetMessage(out NativeMessage message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }

            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            Volatile.Write(ref _threadId, 0);
        }
    }

    public void Stop()
    {
        int threadId = Volatile.Read(ref _threadId);
        if (threadId != 0)
        {
            PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
    }

    public void Dispose()
    {
    }

    private IntPtr KeyboardProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = (int)wParam;
            if (message is WmKeyDown or WmSysKeyDown or WmKeyUp or WmSysKeyUp)
            {
                KeyboardHookData data = Marshal.PtrToStructure<KeyboardHookData>(lParam);
                bool injected = (data.Flags & LlkhfInjected) != 0;
                if (!injected)
                {
                    bool isDown = message is WmKeyDown or WmSysKeyDown;
                    RawInput raw = new((int)data.VirtualKey, isDown, FromMouse: false, GetForegroundWindow());
                    if (_onInput?.Invoke(raw) == true)
                    {
                        return (IntPtr)1;
                    }
                }
            }
        }

        return CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int message = (int)wParam;
            if (message is WmXButtonDown or WmXButtonUp)
            {
                MouseHookData data = Marshal.PtrToStructure<MouseHookData>(lParam);
                int button = (int)(data.Buttons >> 16);
                if (button is 1 or 2)
                {
                    bool isDown = message == WmXButtonDown;
                    RawInput raw = new(button, isDown, FromMouse: true, GetForegroundWindow());
                    if (_onInput?.Invoke(raw) == true)
                    {
                        return (IntPtr)1;
                    }
                }
            }
        }

        return CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardHookData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookData
    {
        public int X;
        public int Y;
        public uint Buttons;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public IntPtr Handle;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookType, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(out NativeMessage message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(int threadId, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
