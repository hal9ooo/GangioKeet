using System.IO;
using System.Runtime.InteropServices;

namespace GangioKeet.Client.Context;

/// <summary>
/// Adattatore Win32 per la cattura del contesto finestra. Inietta handle+PID: la verifica
/// di vita confronta il PID corrente dell'handle con quello catturato, così un HWND
/// riciclato dal sistema viene rifiutato invece di essere trattato come target vivo.
/// </summary>
public sealed class Win32ForegroundWindowService : IForegroundWindowService
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly Func<nint> _getForegroundWindow;
    private readonly Func<nint, string> _getWindowTitle;
    private readonly Func<nint, int> _getWindowProcessId;
    private readonly Func<int, string?> _getProcessImageName;
    private readonly Func<nint, bool> _setForegroundWindow;

    /// <summary>
    /// Costruttore di test: sostituisce le chiamate Win32 con sonde iniettate,
    /// anche non verificabili (null da <paramref name="getProcessImageName"/> → nome sconosciuto).
    /// </summary>
    public Win32ForegroundWindowService(
        Func<nint> getForegroundWindow,
        Func<nint, string> getWindowTitle,
        Func<nint, int> getWindowProcessId,
        Func<int, string?> getProcessImageName,
        Func<nint, bool> setForegroundWindow)
    {
        _getForegroundWindow = getForegroundWindow;
        _getWindowTitle = getWindowTitle;
        _getWindowProcessId = getWindowProcessId;
        _getProcessImageName = getProcessImageName;
        _setForegroundWindow = setForegroundWindow;
    }

    public Win32ForegroundWindowService()
        : this(
            GetForegroundWindow,
            static handle => GetWindowTitle(handle),
            static handle => GetWindowProcessId(handle),
            static pid => GetProcessImageName(pid),
            static handle => SetForegroundWindow(handle))
    {
    }

    public nint CaptureHandle() => _getForegroundWindow();

    public ForegroundWindowInfo Read(nint handle)
    {
        int processId = _getWindowProcessId(handle);
        if (processId == 0)
        {
            throw new InvalidOperationException($"window {handle:X} has no owning process");
        }

        return new ForegroundWindowInfo(handle, processId, _getProcessImageName(processId) ?? string.Empty, _getWindowTitle(handle));
    }

    public nint GetForegroundHandle() => _getForegroundWindow();

    public bool TryActivate(nint handle) => _setForegroundWindow(handle);

    public bool IsAlive(ForegroundWindowInfo target)
    {
        int processId = _getWindowProcessId(target.Handle);
        return processId != 0 && processId == target.ProcessId;
    }

    private static int GetWindowProcessId(nint handle)
    {
        uint processId = 0;
        _ = GetWindowThreadProcessId(handle, ref processId);
        return (int)processId;
    }

    private static string GetWindowTitle(nint handle)
    {
        int length = GetWindowTextLengthW(handle);
        if (length == 0)
        {
            return string.Empty;
        }

        var buffer = new char[length + 1];
        int copied = GetWindowTextW(handle, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    private static string? GetProcessImageName(int processId)
    {
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == nint.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[2048];
            int size = buffer.Length;
            if (!QueryFullProcessImageNameW(process, 0, buffer, ref size))
            {
                return null;
            }

            return Path.GetFileName(new string(buffer, 0, size));
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowTextLengthW(nint hWnd);

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(nint hWnd, [Out] char[] lpString, int cchStringMax);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowThreadProcessId(nint hWnd, ref uint lpdwProcessId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(nint hProcess, uint dwFlags, [Out] char[] lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool CloseHandle(nint hObject);
}
