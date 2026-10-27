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
                string audio = Resolve(path);
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

    public async Task DeleteAsync(IReadOnlyList<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0 || !File.Exists(store.ManifestPath)) return;
        HashSet<string> selected = [.. ids];
        List<string> kept = [];
        List<string> removed = [];
        foreach (string line in File.ReadLines(store.ManifestPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using JsonDocument entry = JsonDocument.Parse(line);
            string id = entry.RootElement.GetProperty("id").GetString()!;
            if (!selected.Contains(id))
            {
                kept.Add(line);
                continue;
            }
            string path = entry.RootElement.GetProperty("audio_path").GetString()!;
            removed.Add(Resolve(path));
        }

        foreach (string path in removed)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Delete(path);
        }
        await store.ReplaceManifestAsync(kept, ct).ConfigureAwait(false);
    }

    private string Resolve(string relative)
    {
        string root = Path.TrimEndingDirectorySeparator(store.Directory) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(relative, store.Directory);
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Recording path escapes the recording root.");
        return path;
    }
}
