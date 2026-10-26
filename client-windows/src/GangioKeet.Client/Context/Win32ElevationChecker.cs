using System.Runtime.InteropServices;

namespace GangioKeet.Client.Context;

/// <summary>
/// Adattatore Win32 per il controllo UIPI: l'iniezione è bloccata se la finestra target è
/// elevata e il processo del client non lo è. Valore non verificabile del target (es. accesso
/// negato o handle morto) → blocco attivo; processo corrente elevato → mai bloccato;
/// processo corrente non ispezionabile → blocco attivo, esito esplicito e non un falso "consentito".
/// </summary>
public sealed class Win32ElevationChecker : IElevationChecker
{
    private const uint TokenQuery = 0x0008;
    private const uint TokenElevation = 20;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private readonly Func<int, bool?> _getTargetElevation;
    private readonly Func<bool?> _getSelfElevation;

    /// <summary>Costruttore di test con sonde di elevazione iniettate.</summary>
    public Win32ElevationChecker(Func<int, bool?> getTargetElevation, Func<bool?> getSelfElevation)
    {
        _getTargetElevation = getTargetElevation;
        _getSelfElevation = getSelfElevation;
    }

    public Win32ElevationChecker()
        : this(static pid => ReadTargetElevation(pid), static () => ReadSelfElevation())
    {
    }

    public bool IsInjectionBlocked(ForegroundWindowInfo target)
    {
        bool? selfElevated = _getSelfElevation();
        if (selfElevated is null)
        {
            return true;
        }

        if (selfElevated.Value)
        {
            return false;
        }

        bool? targetElevated = _getTargetElevation(target.ProcessId);
        return targetElevated is null or true;
    }

    private static bool? ReadSelfElevation()
    {
        return ReadTokenElevation(GetCurrentProcess());
    }

    private static bool? ReadTargetElevation(int processId)
    {
        nint process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == nint.Zero)
        {
            return null;
        }

        try
        {
            return ReadTokenElevation(process);
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    private static bool? ReadTokenElevation(nint process)
    {
        if (!OpenProcessToken(process, TokenQuery, out nint token))
        {
            return null;
        }

        try
        {
            if (!GetTokenInformation(token, TokenElevation, out uint isElevated, sizeof(uint), out uint returnLength)
                || returnLength != sizeof(uint))
            {
                return null;
            }

            return isElevated != 0;
        }
        finally
        {
            _ = CloseHandle(token);
        }
    }

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern bool OpenProcessToken(nint hProcess, uint dwDesiredAccess, out nint hToken);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern bool GetTokenInformation(nint hToken, uint tokenInformationClass, [Out] out uint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern bool CloseHandle(nint hObject);
}
