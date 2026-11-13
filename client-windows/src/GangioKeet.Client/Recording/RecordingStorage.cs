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

    public Task<IReadOnlyList<RecordingEntry>> ListRecentAsync(int limit = 50)
    {
        if (!File.Exists(store.ManifestPath)) return Task.FromResult<IReadOnlyList<RecordingEntry>>([]);
        List<RecordingEntry> entries = [];
        try
        {
            string[] lines = File.ReadAllLines(store.ManifestPath);
            int start = Math.Max(0, lines.Length - limit);
            for (int i = lines.Length - 1; i >= start; i--)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    RecordingEntry? entry = JsonSerializer.Deserialize<RecordingEntry>(line, RecordingStore.JsonOptions);
                    if (entry is not null) entries.Add(entry);
                }
                catch { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<IReadOnlyList<RecordingEntry>>([]);
        }
        return Task.FromResult<IReadOnlyList<RecordingEntry>>(entries);
    }

    public Task<IReadOnlyList<RecordingEntry>> ListAllAsync()
    {
        if (!File.Exists(store.ManifestPath)) return Task.FromResult<IReadOnlyList<RecordingEntry>>([]);
        List<RecordingEntry> entries = [];
        try
        {
            string[] lines = File.ReadAllLines(store.ManifestPath);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    RecordingEntry? entry = JsonSerializer.Deserialize<RecordingEntry>(line, RecordingStore.JsonOptions);
                    if (entry is not null) entries.Add(entry);
                }
                catch { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<IReadOnlyList<RecordingEntry>>([]);
        }
        return Task.FromResult<IReadOnlyList<RecordingEntry>>(entries);
    }
    public Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct)
        => store.DeleteAsync(ids, ct);
}
