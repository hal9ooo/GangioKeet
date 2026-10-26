using System.IO;
using System.Text;

namespace GangioKeet.Client.Platform;

/// <summary>
/// Logger su file giornaliero in %LOCALAPPDATA%/GangioKeet/logs/.
/// Non registra mai payload audio, testo dettato, titoli di finestra o chiavi API:
/// solo codici e messaggi diagnostici.
/// </summary>
public sealed class FileLogger
{
    private static readonly object Gate = new();
    private readonly string _logDirectory;

    public FileLogger(string? baseDirectory = null)
    {
        string root = baseDirectory
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GangioKeet",
                "logs");
        _logDirectory = root;
    }

    public string CurrentLogPath =>
        Path.Combine(_logDirectory, $"{DateTime.UtcNow:yyyy-MM-dd}.log");

    public void Write(string code, string message)
    {
        string line = $"{DateTime.UtcNow:O} [{code}] {message}{Environment.NewLine}";

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(_logDirectory);
                File.AppendAllText(CurrentLogPath, line, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Il logging non deve mai far fallire l'applicazione.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
