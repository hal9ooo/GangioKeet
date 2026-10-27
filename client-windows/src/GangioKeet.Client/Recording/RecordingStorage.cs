using System.IO;
using System.Text.Json;

namespace GangioKeet.Client.Recording;

public sealed class RecordingStorage(RecordingStore store)
{
    public async Task<(int Count, long Bytes)> GetSummaryAsync()
    {
        if (!File.Exists(store.ManifestPath)) return (0, 0);
        int count = 0;
        long bytes = 0;
        foreach (string line in File.ReadLines(store.ManifestPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using JsonDocument entry = JsonDocument.Parse(line);
            count++;
            if (entry.RootElement.TryGetProperty("audio_path", out JsonElement relative)
                && relative.GetString() is string path)
            {
                string audio = store.ResolvePath(path);
                if (File.Exists(audio)) bytes += new FileInfo(audio).Length;
            }
        }
        return (count, bytes);
    }

    public Task<IReadOnlyList<string>> ListIdsAsync()
    {
        if (!File.Exists(store.ManifestPath)) return Task.FromResult<IReadOnlyList<string>>([]);
        List<string> ids = [];
        foreach (string line in File.ReadLines(store.ManifestPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using JsonDocument entry = JsonDocument.Parse(line);
            ids.Add(entry.RootElement.GetProperty("id").GetString()!);
        }
        return Task.FromResult<IReadOnlyList<string>>(ids);
    }

    public Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct)
        => store.DeleteAsync(ids, ct);
}
