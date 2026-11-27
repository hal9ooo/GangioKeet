using GangioKeet.Client.Recording;

namespace GangioKeet.Client.Ui;

public sealed class RecordingLogItem
{
    public RecordingEntry Entry { get; }
    public string CreatedAtFormatted { get; }
    public string RawPreview { get; }
    public string LatencySummary { get; }
    public string EngineSummary { get; }

    public RecordingLogItem(RecordingEntry entry)
    {
        Entry = entry;
        if (DateTimeOffset.TryParse(entry.CreatedAt, out DateTimeOffset dt))
        {
            CreatedAtFormatted = dt.ToLocalTime().ToString("HH:mm:ss — dd/MM/yyyy");
        }
        else
        {
            CreatedAtFormatted = entry.CreatedAt ?? "—";
        }

        string raw = entry.RawText?.Replace("\r", " ").Replace("\n", " ").Trim() ?? "";
        RawPreview = string.IsNullOrWhiteSpace(raw) ? "(silenzio o nessun testo)" : raw;
        LatencySummary = $"{entry.DurationSeconds:0.1}s  •  {entry.TotalMs:0} ms";
        string stt = string.IsNullOrWhiteSpace(entry.SttModel) ? "ASR" : entry.SttModel;
        string cleaner = string.IsNullOrWhiteSpace(entry.LlmModel) ? "nessuno" : entry.LlmModel;
        EngineSummary = $"ASR: {stt}  |  Cleaner: {cleaner}";
    }
}
