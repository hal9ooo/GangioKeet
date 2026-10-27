using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Recording;
using GangioKeet.Client.Protocol;
using GangioKeet.Inference;
using Xunit;

namespace GangioKeet.Client.Tests.Recording;

public sealed class RecordingStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetRecording-").FullName;
    private static readonly StartMessage Start = new("test.exe", "controlled target", "chat");
    private static readonly EngineIdentity Stt = new("model", "revision", "sha", "runtime", "vulkan", "F16", "gpu");
    private static readonly EngineIdentity Cleaner = new("cleaner", "revision2", "sha2", "llama", "vulkan", "Q4_0", "gpu");

    [Fact]
    public async Task DisabledStoreWritesNothing()
    {
        string disabled = Path.Combine(_root, "disabled");
        RecordingStore store = new(new RecordingConfig { Enabled = false, Directory = disabled });

        Assert.Null(await store.RecordAsync(Pcm(4), Result(), Start, default));
        Assert.False(Directory.Exists(disabled));
    }

    [Fact]
    public async Task DisablingStoreAfterStartupStopsSubsequentWrites()
    {
        RecordingConfig config = new() { Enabled = true, Directory = _root };
        RecordingStore store = new(config);

        store.Enabled = false;

        Assert.False(store.Enabled);
        Assert.Null(await store.RecordAsync(Pcm(4), Result(), Start, default));
        Assert.False(File.Exists(store.ManifestPath));
    }

    [Fact]
    public async Task CompletedResultWritesExactWavAndManifestWithoutPersonalContext()
    {
        RecordingStore store = new(new RecordingConfig { Enabled = true, Directory = _root },
            provenance: new RecordingProvenance("conservative-local-v3", "prompt-sha", "policy-sha", "personal"));
        byte[] pcm = Pcm(32);

        string? id = await store.RecordAsync(pcm, Result(), Start, default);

        Assert.NotNull(id);
        string manifest = Path.Combine(_root, "manifest.jsonl");
        string line = Assert.Single(File.ReadAllLines(manifest));
        using JsonDocument entry = JsonDocument.Parse(line);
        Assert.Equal("gangio-keet-recording-v1", entry.RootElement.GetProperty("schema").GetString());
        Assert.Equal(id, entry.RootElement.GetProperty("id").GetString());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(pcm)), entry.RootElement.GetProperty("audio_sha256").GetString());
        Assert.Equal("", entry.RootElement.GetProperty("reference_raw").GetString());
        Assert.Equal("", entry.RootElement.GetProperty("reference_final").GetString());
        Assert.Equal("unreviewed", entry.RootElement.GetProperty("reference_status").GetString());
        Assert.Equal("Come funziona?", entry.RootElement.GetProperty("final_text").GetString());
        Assert.Equal("conservative-local-v3", entry.RootElement.GetProperty("prompt_version").GetString());
        Assert.Equal("prompt-sha", entry.RootElement.GetProperty("prompt_sha256").GetString());
        Assert.Equal("policy-sha", entry.RootElement.GetProperty("policy_sha256").GetString());
        Assert.Equal("personal", entry.RootElement.GetProperty("split").GetString());
        Assert.Equal("model", entry.RootElement.GetProperty("stt_engine").GetProperty("model_id").GetString());
        Assert.False(entry.RootElement.TryGetProperty("selected_text", out _));
        Assert.False(entry.RootElement.TryGetProperty("context", out _));

        byte[] wav = File.ReadAllBytes(Path.Combine(_root, "audio", id + ".wav"));
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal(1, BitConverter.ToInt16(wav, 22));
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(16, BitConverter.ToInt16(wav, 34));
        Assert.Equal(pcm, wav[44..]);
    }

    [Fact]
    public async Task StorageSummaryAndExplicitDeleteUseOnlyResolvedIds()
    {
        RecordingStore store = new(new RecordingConfig { Enabled = true, Directory = _root });
        string? first = await store.RecordAsync(Pcm(4), Result(), Start, default);
        string? second = await store.RecordAsync(Pcm(8), Result(), Start, default);
        RecordingStorage storage = new(store);

        (int count, long bytes) = await storage.GetSummaryAsync();
        Assert.Equal(2, count);
        Assert.True(bytes > 0);

        await storage.DeleteAsync([first!], default);
        Assert.Equal(1, (await storage.GetSummaryAsync()).Count);
        Assert.True(File.Exists(Path.Combine(_root, "audio", second! + ".wav")));
        Assert.False(File.Exists(Path.Combine(_root, "audio", first! + ".wav")));
    }

    [Fact]
    public async Task ConcurrentAppendAndDeleteKeepManifestConsistent()
    {
        RecordingStore store = new(new RecordingConfig { Enabled = true, Directory = _root });
        RecordingStorage storage = new(store);
        string? first = await store.RecordAsync(Pcm(8), Result(), Start, default);
        Assert.NotNull(first);

        List<Task> work = [];
        for (int i = 0; i < 12; i++)
        {
            work.Add(store.RecordAsync(Pcm(16 + i * 2), Result(), Start, default));
            work.Add(storage.DeleteAsync([first!], default));
        }
        await Task.WhenAll(work);

        List<string> lines = [.. File.ReadAllLines(store.ManifestPath)];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            using JsonDocument entry = JsonDocument.Parse(line);
            string id = entry.RootElement.GetProperty("id").GetString()!;
            Assert.True(ids.Add(id), $"duplicate manifest id: {id}");
            string audio = Path.Combine(_root,
                entry.RootElement.GetProperty("audio_path").GetString()!.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(audio), $"manifest row points at missing audio: {id}");
        }
        Assert.DoesNotContain(first, ids);
    }

    [Fact]
    public async Task DeleteRejectsManifestPathsOutsideTheRecordingRoot()
    {
        RecordingStore store = new(new RecordingConfig { Enabled = true, Directory = _root });
        Directory.CreateDirectory(Path.Combine(_root, "audio"));
        string outside = Path.Combine(_root, "outside.wav");
        await File.WriteAllBytesAsync(outside, [1, 2]);
        string manifest = Path.Combine(_root, "manifest.jsonl");
        await File.WriteAllTextAsync(manifest,
            """{"id":"evil","audio_path":"../outside.wav"}""" + "\n");

        RecordingStorage storage = new(store);
        await Assert.ThrowsAsync<InvalidDataException>(() => storage.DeleteAsync(["evil"], default));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task ManifestFailureNeverPublishesAnIncompleteRow()
    {
        RecordingStore store = new(new RecordingConfig { Enabled = true, Directory = _root });
        Directory.CreateDirectory(Path.Combine(_root, "manifest.jsonl"));

        Assert.Null(await store.RecordAsync(Pcm(4), Result(), Start, default));

        Assert.False(File.Exists(Path.Combine(_root, "manifest.jsonl")));
        Assert.Equal(0, (await new RecordingStorage(store).GetSummaryAsync()).Count);
    }

    [Fact]
    public async Task DiskFailureReturnsNullWithoutThrowing()
    {
        string blocked = Path.Combine(_root, "blocked");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(blocked, "not a directory");
        RecordingStore store = new(new RecordingConfig { Enabled = true, Directory = blocked });

        Assert.Null(await store.RecordAsync(Pcm(4), Result(), Start, default));
    }

    [Fact]
    public async Task QueueCopiesBuffersIsBoundedAndDrainsOnce()
    {
        BlockingWriter writer = new();
        RecordingQueue queue = new(writer, capacity: 2);
        byte[] pcm = Pcm(4);

        Assert.True(queue.TryEnqueue(pcm, Result(), Start));
        await writer.Started;
        Assert.True(queue.TryEnqueue(Pcm(6), Result(), Start));
        Assert.True(queue.TryEnqueue(Pcm(8), Result(), Start));
        Assert.False(queue.TryEnqueue(Pcm(10), Result(), Start));
        Assert.Equal(1, queue.Dropped);

        pcm[0] = 99;
        writer.Release();
        await queue.DrainAsync(default);
        await queue.DrainAsync(default);

        Assert.Equal(3, writer.Calls);
        Assert.Equal(0, writer.Seen[0][0]);
        Assert.Equal(1, queue.Dropped);
    }

    [Fact]
    public async Task QueueWriterFailureDoesNotBlockDrain()
    {
        ThrowingWriter writer = new();
        RecordingQueue queue = new(writer, capacity: 1);

        Assert.True(queue.TryEnqueue(Pcm(4), Result(), Start));
        await queue.DrainAsync(default);

        Assert.Equal(1, queue.Failed);
    }

    [Fact]
    public async Task DisabledQueueDoesNotCopyOrInvokeWriter()
    {
        bool enabled = true;
        CountingWriter writer = new();
        RecordingQueue queue = new(writer, capacity: 1, enabled: () => enabled);

        enabled = false;
        Assert.False(queue.TryEnqueue(Pcm(4), Result(), Start));
        await queue.DrainAsync(default);

        Assert.Equal(0, writer.Calls);
        Assert.Equal(0, queue.Dropped);
        Assert.Equal(0, queue.Failed);
    }

    private static byte[] Pcm(int bytes) => Enumerable.Range(0, bytes).Select(x => (byte)x).ToArray();

    private static PipelineResult Result() => new("come funziona", "Come funziona?", 10, 20, 30,
        [], Stt, Cleaner);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class BlockingWriter : IRecordingWriter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public Task Started => _started.Task;
        public List<byte[]> Seen { get; } = [];
        public async Task<string?> RecordAsync(ReadOnlyMemory<byte> pcm, PipelineResult result,
            StartMessage start, CancellationToken ct)
        {
            _started.TrySetResult();
            await _release.Task;
            Seen.Add(pcm.ToArray());
            Calls++;
            return Guid.NewGuid().ToString("N");
        }
        public void Release() => _release.TrySetResult();
    }

    private sealed class ThrowingWriter : IRecordingWriter
    {
        public Task<string?> RecordAsync(ReadOnlyMemory<byte> pcm, PipelineResult result,
            StartMessage start, CancellationToken ct) => throw new IOException("disk full");
    }

    private sealed class CountingWriter : IRecordingWriter
    {
        public int Calls;
        public Task<string?> RecordAsync(ReadOnlyMemory<byte> pcm, PipelineResult result,
            StartMessage start, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<string?>(Guid.NewGuid().ToString("N"));
        }
    }
}
