using GangioKeet.Client.Recording;
using GangioKeet.Inference;
using Xunit;

namespace GangioKeet.Client.Tests.Recording;

public sealed class RecordingStatsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 12, 0, 0, TimeSpan.FromHours(1));
    private static readonly EngineIdentity Stt = new("m", "r", "s", "rt", "vulkan", "F16", "gpu");

    private static RecordingEntry Entry(string createdAt, double seconds, double sttMs, double totalMs,
        string raw, string final, string sttModel = "parakeet_v3", string? llmModel = "light_cleanup",
        IReadOnlyList<string>? warnings = null, bool silence = false) =>
        new("schema", Guid.NewGuid().ToString(), "audio.wav", "sha", 0, seconds, 16000, 1, 2,
            createdAt, "local", "default", sttModel, llmModel, raw, final, null,
            warnings ?? [], sttMs, 0, totalMs, "", "", "unreviewed", "it_general", [], silence,
            Stt, null, "p", "ps", "pol", "personal");

    [Fact]
    public void EmptyManifestYieldsZerosAndTwentyEightDays()
    {
        RecordingStats stats = RecordingStats.From([], Now);
        Assert.Equal(0, stats.Count);
        Assert.Equal(0, stats.Words);
        Assert.Equal(0, stats.WordsPerMinute);
        Assert.Equal(0, stats.SttMedianMs);
        Assert.Equal(28, stats.Activity.Count);
        Assert.All(stats.Activity, day => Assert.Equal(0, day.Count));
    }

    [Fact]
    public void CountsTotalsWordsAndPace()
    {
        RecordingStats stats = RecordingStats.From(
        [
            Entry("2026-11-13T10:00:00+01:00", 30, 100, 200, "ciao mondo", "ciao mondo"),
            Entry("2026-11-13T11:00:00+01:00", 30, 300, 400, "due parole", "due parole"),
        ], Now);
        Assert.Equal(2, stats.Count);
        Assert.Equal(60, stats.DictatedSeconds);
        Assert.Equal(4, stats.Words);
        Assert.Equal(20, stats.Characters);
        Assert.Equal(4, stats.WordsPerMinute);
        Assert.Equal(0.6, stats.ProcessingSeconds, 3);
    }

    [Fact]
    public void MedianAveragesTheTwoMiddleValues()
    {
        RecordingStats stats = RecordingStats.From(
        [
            Entry("2026-11-13T10:00:00+01:00", 1, 100, 100, "a", "a"),
            Entry("2026-11-13T10:01:00+01:00", 1, 200, 200, "a", "a"),
            Entry("2026-11-13T10:02:00+01:00", 1, 300, 300, "a", "a"),
            Entry("2026-11-13T10:03:00+01:00", 1, 400, 400, "a", "a"),
        ], Now);
        Assert.Equal(250, stats.SttMedianMs);
        Assert.Equal(400, stats.SttP95Ms);
    }

    [Fact]
    public void UnparseableDateCountsInTotalsButNotInToday()
    {
        RecordingStats stats = RecordingStats.From(
        [
            Entry("non-una-data", 10, 100, 100, "a", "a"),
            Entry("2026-11-13T10:00:00+01:00", 10, 100, 100, "a", "a"),
        ], Now);
        Assert.Equal(2, stats.Count);
        Assert.Equal(1, stats.CountToday);
        Assert.Equal(1, stats.CountLast7Days);
        Assert.Equal(1, stats.Activity.Sum(day => day.Count));
    }

    [Fact]
    public void NullOrEmptyFinalTextYieldsZeroWords()
    {
        RecordingStats stats = RecordingStats.From(
        [
            Entry("2026-11-13T10:00:00+01:00", 10, 100, 100, "ciao", ""),
            Entry("2026-11-13T10:01:00+01:00", 10, 100, 100, "ciao", null!),
        ], Now);
        Assert.Equal(0, stats.Words);
        Assert.Equal(0, stats.Characters);
        Assert.Equal(0, stats.WordsPerMinute);
    }

    [Fact]
    public void CountsWarningsSilenceAndCleanupChanges()
    {
        RecordingStats stats = RecordingStats.From(
        [
            Entry("2026-11-13T10:00:00+01:00", 1, 1, 1, "cyper inference", "Cheaper Inference"),
            Entry("2026-11-13T10:01:00+01:00", 1, 1, 1, "a", "a", warnings: ["llm_failed"]),
            Entry("2026-11-13T10:02:00+01:00", 1, 1, 1, "", "", silence: true),
        ], Now);
        Assert.Equal(1, stats.ChangedByCleanup);
        Assert.Equal(1, stats.WithWarnings);
        Assert.Equal(1, stats.Silent);
    }

    [Fact]
    public void PicksTheMostFrequentEngines()
    {
        RecordingStats stats = RecordingStats.From(
        [
            Entry("2026-11-13T10:00:00+01:00", 1, 1, 1, "a", "a", sttModel: "parakeet_v3"),
            Entry("2026-11-13T10:01:00+01:00", 1, 1, 1, "a", "a", sttModel: "parakeet_v3"),
            Entry("2026-11-13T10:02:00+01:00", 1, 1, 1, "a", "a", sttModel: "nemotron", llmModel: null),
        ], Now);
        Assert.Equal("parakeet_v3", stats.TopSttModel);
        Assert.Equal(2, stats.TopSttModelCount);
        Assert.Equal("light_cleanup", stats.TopCleaner);
        Assert.Equal(2, stats.TopCleanerCount);
    }

    [Fact]
    public void ActivityCoversTwentyEightDaysEndingToday()
    {
        RecordingStats stats = RecordingStats.From(
        [
            Entry("2026-11-13T10:00:00+01:00", 1, 1, 1, "a", "a"),
            Entry("2026-11-13T11:00:00+01:00", 1, 1, 1, "a", "a"),
            Entry("2026-10-17T10:00:00+01:00", 1, 1, 1, "a", "a"),
            Entry("2026-10-01T10:00:00+01:00", 1, 1, 1, "a", "a"),
        ], Now);
        Assert.Equal(28, stats.Activity.Count);
        Assert.Equal(new DateOnly(2026, 10, 17), stats.Activity[0].Day);
        Assert.Equal(1, stats.Activity[0].Count);
        Assert.Equal(new DateOnly(2026, 11, 13), stats.Activity[^1].Day);
        Assert.Equal(2, stats.Activity[^1].Count);
        Assert.Equal(1, stats.Activity[^1].BarHeight);
        Assert.Equal(3, stats.Activity.Sum(day => day.Count));
    }

    [Fact]
    public void TenThousandEntriesStayUnderOneHundredMilliseconds()
    {
        RecordingEntry[] entries = Enumerable.Range(0, 10_000)
            .Select(_ => Entry("2026-11-13T10:00:00+01:00", 5, 100, 200,
                "testo dettato di prova", "testo dettato di prova"))
            .ToArray();
        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();
        RecordingStats stats = RecordingStats.From(entries, Now);
        watch.Stop();
        Assert.Equal(10_000, stats.Count);
        Assert.True(watch.ElapsedMilliseconds < 100, $"aggregazione lenta: {watch.ElapsedMilliseconds} ms");
    }
}
