using System.IO;

namespace GangioKeet.Client.Platform;

/// <summary>
/// Nome visibile del prodotto e cartelle dati. Gli identificatori interni
/// (namespace, nomi dei progetti) restano invariati di proposito.
/// </summary>
public static class Branding
{
    public const string ProductName = "ChattyNeko";
    public const string DataFolderName = "ChattyNeko";

    private const string LegacyFolderName = "GangioKeet";
    private const string LegacyAutostartValue = "GangioKeet";

    /// <summary>
    /// Sposta una volta sola i dati dal vecchio nome a quello nuovo, così il
    /// rebrand non perde configurazione, registrazioni e calibrazione.
    /// </summary>
    public static void MigrateLegacyFolders()
    {
        Move(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        Move(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
    }

    private static void Move(string baseDirectory)
    {
        if (string.IsNullOrEmpty(baseDirectory)) return;

        string legacy = Path.Combine(baseDirectory, LegacyFolderName);
        string current = Path.Combine(baseDirectory, DataFolderName);
        if (Directory.Exists(current) || !Directory.Exists(legacy)) return;

        try
        {
            Directory.Move(legacy, current);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Nome della voce di avvio automatico, con pulizia di quella vecchia.</summary>
    public static string AutostartValueName => DataFolderName;

    public static string LegacyAutostartValueName => LegacyAutostartValue;
}
