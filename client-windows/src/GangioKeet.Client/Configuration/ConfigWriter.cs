using System.IO;
using System.Text;

namespace GangioKeet.Client.Configuration;

/// <summary>
/// Scrittura puntuale della configurazione: modifica solo la riga
/// <c>audio.input_device</c> preservando commenti, ordine e indentazione.
/// La scrittura è atomica (file temporaneo + replace): un errore non tronca il file.
/// </summary>
public static class ConfigWriter
{
    public static bool SetModelSelectionPath(string path, string selectionPath) =>
        SetSectionScalar(path, "inference", "model_selection_path", $"\"{selectionPath.Replace('\\', '/')}\"");

    public static bool SetRecordingEnabled(string path, bool enabled) =>
        SetSectionScalar(path, "recording", "enabled", enabled ? "true" : "false");

    private static bool SetSectionScalar(string path, string section, string key, string value)
    {
        try
        {
            List<string> lines = File.Exists(path) ? [.. File.ReadAllLines(path)] : [];
            int start = lines.FindIndex(line => IsTopLevelKey(line, section));
            if (start < 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
                lines.Add(section + ":");
                lines.Add($"  {key}: {value}");
            }
            else
            {
                string remainder = lines[start][(lines[start].IndexOf(':') + 1)..].Trim();
                if (remainder.Length > 0 && !remainder.StartsWith('#')) return false;
                int end = start + 1;
                while (end < lines.Count && (string.IsNullOrWhiteSpace(lines[end])
                    || lines[end].TrimStart().StartsWith('#') || char.IsWhiteSpace(lines[end][0]))) end++;
                string indent = DetectIndent(lines, start) ?? "  ";
                int found = -1;
                for (int index = start + 1; index < end; index++)
                    if (lines[index].StartsWith(indent + key + ":", StringComparison.Ordinal))
                    {
                        if (found >= 0) return false;
                        found = index;
                    }
                if (found >= 0) lines[found] = ReplaceValue(lines[found], $"{key}: {value}");
                else lines.Insert(start + 1, $"{indent}{key}: {value}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            WriteAtomic(path, lines);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { return false; }
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

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

            int audioIndex = lines.FindIndex(IsAudioSectionStart);
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
                string indent = DetectIndent(lines, audioIndex) ?? "  ";
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
                    lines.Insert(insertAt, indent + rendered);
                }
            }

            WriteAtomic(path, lines);
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

    private static bool IsAudioSectionStart(string line)
    {
        string trimmed = line.TrimStart();
        if (!trimmed.StartsWith("audio:", StringComparison.Ordinal))
        {
            return false;
        }

        string remainder = trimmed["audio:".Length..].Trim();
        return remainder.Length == 0 || remainder.StartsWith('#');
    }

    private static string? DetectIndent(List<string> lines, int audioIndex)
    {
        for (int i = audioIndex + 1; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.Length == 0 || line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                return null;
            }

            return line[..(line.Length - line.TrimStart().Length)];
        }

        return null;
    }

    private static void WriteAtomic(string path, List<string> lines)
    {
        string directory = Path.GetDirectoryName(path) ?? ".";
        string temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllLines(temp, lines, Utf8NoBom);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static bool IsTopLevelKey(string line, string key)
    {
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#')
        {
            return false;
        }

        return line.TrimStart().StartsWith(key + ":", StringComparison.Ordinal);
    }

    private static string ReplaceValue(string line, string rendered)    {
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
