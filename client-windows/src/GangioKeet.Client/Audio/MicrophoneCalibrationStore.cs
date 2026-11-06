using System.IO;
using GangioKeet.Client.Platform;
using System.Text.Json;

namespace GangioKeet.Client.Audio;

/// <summary>
/// Persiste il picco di riferimento dell'ultima registrazione in
/// <c>%LOCALAPPDATA%\ChattyNeko\mic-calibration.json</c>. Il file contiene
/// solo un numero: nessun audio, nessun dato personale.
/// </summary>
public sealed class MicrophoneCalibrationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _directory;
    private readonly string _path;

    public MicrophoneCalibrationStore(string? appDataBase = null)
    {
        string root = appDataBase
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _directory = Path.Combine(root, Branding.DataFolderName);
        _path = Path.Combine(_directory, "mic-calibration.json");
    }

    public string FilePath => _path;

    /// <summary>Legge la taratura salvata; null se manca o non è leggibile.</summary>
    public double? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            CalibrationFile? file = JsonSerializer.Deserialize<CalibrationFile>(
                File.ReadAllText(_path), JsonOptions);
            return file is null || !double.IsFinite(file.Reference)
                ? null
                : file.Reference;
        }
        catch (Exception)
        {
            // Una taratura illeggibile non deve impedire l'avvio.
            return null;
        }
    }

    /// <summary>Salva la taratura; un errore di disco non interrompe la dettatura.</summary>
    public void Save(double reference)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(_path, JsonSerializer.Serialize(
                new CalibrationFile(reference), JsonOptions));
        }
        catch (Exception)
        {
            // best effort
        }
    }

    private sealed record CalibrationFile(double Reference);
}
