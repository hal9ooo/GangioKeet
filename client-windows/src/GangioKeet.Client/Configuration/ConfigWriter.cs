using System.IO;
using System.Text;

namespace GangioKeet.Client.Configuration;

/// <summary>
/// Scrittura puntuale della configurazione: modifica solo la riga
/// <c>audio.input_device</c> preservando commenti, ordine e indentazione.
/// Un errore di I/O restituisce false e lascia il file com'era.
/// </summary>
public static class ConfigWriter
{
    public static bool SetAudioInputDevice(string path, string? deviceId)
    {
        try
        {
            List<string> lines = File.Exists(path)
                ? [.. File.ReadAllLines(path)]
                : [];

            string rendered = deviceId is null
                ? "input_device: null"
                : $"input_device: \"{deviceId}\"";

            int audioIndex = lines.FindIndex(line => line.TrimEnd() == "audio:");
            if (audioIndex < 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0)
                {
                    lines.Add("");
                }

                lines.Add("audio:");
                lines.Add("  " + rendered);
            }
            else
            {
                int insertAt = audioIndex + 1;
                int keyIndex = -1;

                for (int i = audioIndex + 1; i < lines.Count; i++)
                {
                    string line = lines[i];
                    if (line.Length == 0 || line.TrimStart().StartsWith('#'))
                    {
                        insertAt = i + 1;
                        continue;
                    }

                    if (!char.IsWhiteSpace(line[0]))
                    {
                        break; // inizio della sezione successiva
                    }

                    if (line.TrimStart().StartsWith("input_device:", StringComparison.Ordinal))
                    {
                        keyIndex = i;
                        break;
                    }

                    insertAt = i + 1;
                }

                if (keyIndex >= 0)
                {
                    lines[keyIndex] = ReplaceValue(lines[keyIndex], rendered);
                }
                else
                {
                    lines.Insert(insertAt, "  " + rendered);
                }
            }

            File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string ReplaceValue(string line, string rendered)
    {
        int valueStart = line.Length - line.TrimStart().Length;
        string indent = line[..valueStart];
        string body = line[valueStart..];

        int commentIndex = body.IndexOf('#');
        string comment = commentIndex >= 0 ? body[commentIndex..].TrimEnd() : "";

        return comment.Length > 0
            ? $"{indent}{rendered}   {comment}"
            : $"{indent}{rendered}";
    }
}
