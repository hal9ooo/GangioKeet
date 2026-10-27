using System.Text.Json;

namespace GangioKeet.Eval;

public static class ReportWriter
{
    public static async Task WriteAsync(string path, object report, CancellationToken ct)
    {
        string destination = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(report, ProbeCommand.JsonOptions), ct);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
