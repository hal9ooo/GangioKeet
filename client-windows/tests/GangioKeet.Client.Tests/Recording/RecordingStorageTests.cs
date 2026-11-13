using GangioKeet.Client.Configuration;
using GangioKeet.Client.Recording;
using Xunit;

namespace GangioKeet.Client.Tests.Recording;

public sealed class RecordingStorageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetStorage-").FullName;

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task ListAllReturnsEveryEntryNewestFirst()
    {
        string manifest = Path.Combine(_root, "manifest.jsonl");
        await File.WriteAllLinesAsync(manifest,
        [
            """{"id":"a","created_at":"2026-11-13T10:00:00+01:00","raw_text":"uno","final_text":"uno"}""",
            """{"id":"b","created_at":"2026-11-13T11:00:00+01:00","raw_text":"due","final_text":"due"}""",
            """non-json""",
        ]);
        RecordingStorage storage = new(new RecordingStore(
            new RecordingConfig { Enabled = true, Directory = _root }));

        IReadOnlyList<RecordingEntry> entries = await storage.ListAllAsync();

        Assert.Equal(["b", "a"], entries.Select(entry => entry.Id));
    }

    [Fact]
    public async Task ListAllOnMissingManifestReturnsEmpty()
    {
        RecordingStorage storage = new(new RecordingStore(
            new RecordingConfig { Enabled = true, Directory = Path.Combine(_root, "vuoto") }));

        Assert.Empty(await storage.ListAllAsync());
    }
}
