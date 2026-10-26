using System.IO;
using Microsoft.Win32;

namespace GangioKeet.Client.Platform;

/// <summary>
/// Gestione dell'avvio automatico via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// Default: disabilitato. Il percorso dell'eseguibile viene sempre quotato.
/// </summary>
public sealed class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "GangioKeet";

    private readonly string _executablePath;

    public AutostartService(string? executablePath = null)
    {
        _executablePath = executablePath
            ?? Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "GangioKeet.Client.exe");
    }

    /// <summary>True se l'avvio automatico è attualmente registrato.</summary>
    public bool IsEnabled()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) is string value && value.Length > 0;
    }

    /// <summary>
    /// Abilita o disabilita l'avvio automatico. Il percorso viene sempre quotato
    /// per gestire spazi nel nome del file.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            key.SetValue(ValueName, $"\"{_executablePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
