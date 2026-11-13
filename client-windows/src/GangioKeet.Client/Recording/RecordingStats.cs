namespace GangioKeet.Client.Recording;

/// <summary>Una giornata di attività: <see cref="BarHeight"/> è la frazione 0..1 sul massimo del periodo.</summary>
public sealed record DailyActivity(DateOnly Day, int Count, double BarHeight, string Label);

/// <summary>
/// Aggregati di tutte le dettature registrate. Si ricalcolano dal manifest a ogni
/// lettura: nessuno stato da tenere allineato, nessun numero che possa divergere
/// dalla verità su disco.
/// </summary>
public sealed record RecordingStats(
    int Count,
    int CountToday,
    int CountLast7Days,
    double DictatedSeconds,
    double ProcessingSeconds,
    int Words,
    int Characters,
    double WordsPerMinute,
    double SttMedianMs,
    double SttP95Ms,
    double TotalMedianMs,
    double TotalP95Ms,
    int WithWarnings,
    int Silent,
    int ChangedByCleanup,
    string TopSttModel,
    int TopSttModelCount,
    string TopCleaner,
    int TopCleanerCount,
    IReadOnlyList<DailyActivity> Activity)
{
    private const int ActivityDays = 28;

    public static RecordingStats From(IReadOnlyList<RecordingEntry> entries, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);

        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        DateOnly firstDay = today.AddDays(-(ActivityDays - 1));

        int countToday = 0;
        int countLast7 = 0;
        int words = 0;
        int characters = 0;
        double dictated = 0;
        double processing = 0;
        int withWarnings = 0;
        int silent = 0;
        int changed = 0;
        List<double> stt = new(entries.Count);
        List<double> total = new(entries.Count);
        Dictionary<string, int> sttModels = new(StringComparer.Ordinal);
        Dictionary<string, int> cleaners = new(StringComparer.Ordinal);
        Dictionary<DateOnly, int> perDay = [];

        foreach (RecordingEntry entry in entries)
        {
            dictated += entry.DurationSeconds;
            processing += entry.TotalMs / 1000.0;
            stt.Add(entry.SttMs);
            total.Add(entry.TotalMs);

            string final = entry.FinalText ?? "";
            words += final.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
            characters += final.Length;

            if (entry.Warnings is { Count: > 0 }) withWarnings++;
            if (entry.IsSilence) silent++;
            if (!string.Equals(final.Trim(), (entry.RawText ?? "").Trim(), StringComparison.Ordinal)) changed++;

            string sttModel = string.IsNullOrWhiteSpace(entry.SttModel) ? "ASR" : entry.SttModel;
            sttModels[sttModel] = sttModels.GetValueOrDefault(sttModel) + 1;
            string cleaner = string.IsNullOrWhiteSpace(entry.LlmModel) ? "nessuno" : entry.LlmModel;
            cleaners[cleaner] = cleaners.GetValueOrDefault(cleaner) + 1;

            if (DateTimeOffset.TryParse(entry.CreatedAt, out DateTimeOffset created))
            {
                DateOnly day = DateOnly.FromDateTime(created.ToLocalTime().DateTime);
                if (day == today) countToday++;
                if (day >= today.AddDays(-6) && day <= today) countLast7++;
                if (day >= firstDay && day <= today) perDay[day] = perDay.GetValueOrDefault(day) + 1;
            }
        }

        List<DailyActivity> activity = new(ActivityDays);
        int busiest = 0;
        for (int i = 0; i < ActivityDays; i++)
        {
            DateOnly day = firstDay.AddDays(i);
            int dayCount = perDay.GetValueOrDefault(day);
            busiest = Math.Max(busiest, dayCount);
            activity.Add(new DailyActivity(day, dayCount, 0, day.ToString("dd/MM")));
        }
        for (int i = 0; i < activity.Count; i++)
        {
            DailyActivity day = activity[i];
            activity[i] = day with { BarHeight = busiest == 0 ? 0 : day.Count / (double)busiest };
        }

        (string topStt, int topSttCount) = Top(sttModels);
        (string topCleaner, int topCleanerCount) = Top(cleaners);

        return new RecordingStats(
            entries.Count, countToday, countLast7,
            dictated, processing, words, characters,
            dictated > 0 ? words / (dictated / 60.0) : 0,
            Median(stt), Percentile(stt, 0.95), Median(total), Percentile(total, 0.95),
            withWarnings, silent, changed,
            topStt, topSttCount, topCleaner, topCleanerCount, activity);
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        double[] sorted = [.. values];
        Array.Sort(sorted);
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static double Percentile(List<double> values, double fraction)
    {
        if (values.Count == 0) return 0;
        double[] sorted = [.. values];
        Array.Sort(sorted);
        int index = Math.Clamp((int)Math.Ceiling(fraction * sorted.Length) - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    private static (string Name, int Count) Top(Dictionary<string, int> counts)
    {
        if (counts.Count == 0) return ("—", 0);
        KeyValuePair<string, int> best = counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .First();
        return (best.Key, best.Value);
    }
}
