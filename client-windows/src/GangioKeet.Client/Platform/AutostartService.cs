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
    private const string ValueName = Branding.DataFolderName;

    private readonly string _executablePath;

    public AutostartService(string? executablePath = null)
    {
        _executablePath = executablePath
            ?? Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "ChattyNeko.exe");
    }

    /// <summary>
    /// Se esiste ancora la voce col vecchio nome, la sposta su quello nuovo
    /// puntando all'eseguibile corrente. Chiamata una volta all'avvio.
    /// </summary>
    public static void MigrateLegacyValue(string executablePath)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key?.GetValue(Branding.LegacyAutostartValueName) is not string) return;

            key.DeleteValue(Branding.LegacyAutostartValueName, throwOnMissingValue: false);
            key.SetValue(Branding.AutostartValueName, $"\"{executablePath}\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
        }
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
            key.DeleteValue(Branding.LegacyAutostartValueName, throwOnMissingValue: false);
            key.SetValue(ValueName, $"\"{_executablePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
