using System.Diagnostics;
using Microsoft.Win32;

namespace GangioKeet.Client.Platform;

/// <summary>Collegamento allo stato privaci del microfono di Windows: consentito o no, e apertura delle impostazioni.</summary>
public interface IMicrophonePrivacy
{
    bool IsAccessAllowed();

    void OpenSettings();
}

/// <summary>
/// Legge il ConsentStore di Windows (HKCU) per il microfono. Manca la chiave = permesso al default
/// (Allow): una lettura impossibile del registro non viene interpretata come consenso negato.
/// <see cref="OpenSettings"/> apre <c>ms-settings:privacy-microphone</c> e va invocato solo su
/// azione esplicita dell'utente, mai in automatico a ogni fallimento di cattura.
/// </summary>
public sealed class MicrophonePrivacy : IMicrophonePrivacy
{
    public const string SettingsUri = "ms-settings:privacy-microphone";

    private const string ConsentStorePath = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    public bool IsAccessAllowed()
    {
        return ReadConsentValue(ConsentStorePath) != "Deny"
            && ReadConsentValue(ConsentStorePath + @"\NonPackaged") != "Deny";
    }

    public void OpenSettings()
    {
        using Process? process = Process.Start(new ProcessStartInfo(SettingsUri) { UseShellExecute = true });
    }

    private static string? ReadConsentValue(string path)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path);
            return key?.GetValue("Value") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException or ObjectDisposedException)
        {
            // Registro protetto o non leggibile: le impostazioni restano al default (consentito),
            // l'eventuale negazione reale emerge comunque da WASAPI come device_unavailable.
            return null;
        }
    }
}
