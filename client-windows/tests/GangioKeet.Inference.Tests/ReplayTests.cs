using System.Text.Json;
using GangioKeet.Eval;
using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class ReplayTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetReplay-").FullName;

    [Fact]
    public async Task AsrReplayPreservesIdsReferencesAndExcludesWarmupFromPasses()
    {
        FakeEngines engines = new();
        ReplayCommand command = Command(engines, passes: 2);
        string manifest = Manifest(audio: true);
        string output = Path.Combine(_root, "report.json");
        Assert.Equal(0, await command.RunAsync(Catalog(), manifest, "example", null, "asr", output, default));
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(output));
        JsonElement clips = report.RootElement.GetProperty("clips");
        Assert.Equal(4, clips.GetArrayLength());
        Assert.Equal(new[] { "a", "b", "a", "b" }, clips.EnumerateArray().Select(x => x.GetProperty("id").GetString()));
        Assert.Equal("provided", clips[0].GetProperty("reference_status").GetString());
        Assert.Equal("Ciao?", clips[0].GetProperty("reference_final").GetString());
        Assert.Equal(5, engines.SttCalls);
        Assert.Equal(1, engines.SttLoads);
        Assert.All(clips.EnumerateArray(), clip => Assert.True(clip.GetProperty("warm").GetBoolean()));
    }

    [Fact]
    public async Task CleanerPhaseConsumesFrozenTextWithoutOpeningAudioOrStt()
    {
        FakeEngines engines = new();
        ReplayCommand command = Command(engines, passes: 1);
        string output = Path.Combine(_root, "report.json");
        Assert.Equal(0, await command.RunAsync(Catalog(), Manifest(audio: false), null, "example", "cleaner", output, default));
        Assert.Equal(0, engines.SttLoads);
        Assert.Equal(0, engines.SttCalls);
        Assert.Equal(3, engines.CleanerCalls);
        using JsonDocument report = JsonDocument.Parse(File.ReadAllText(output));
        Assert.Equal("ciao", report.RootElement.GetProperty("clips")[0].GetProperty("raw_text").GetString());
    }

    private ReplayCommand Command(FakeEngines engines, int passes) => new(_root, _root, _root,
        Path.Combine(_root, "unused-lock.json"), new CleanupPolicy(), passes,
        sttFactory: _ => engines, cleanerFactory: _ => engines);

    private string Catalog()
    {
        string path = Path.Combine(_root, "catalog.json");
        File.WriteAllText(path, """
            {"schema_version":1,"candidates":[{"id":"example","family":"fake","source_checkpoint":"owner/fake",
            "revision":"0123456789abcdef0123456789abcdef01234567","files":[],"precision":"F16","license":"MIT","runtime_id":"fake"}]}
            """);
        return path;
    }

    private string Manifest(bool audio)
    {
        using (BinaryWriter writer = new(File.Create(Path.Combine(_root, "clip.wav"))))
        {
            writer.Write("RIFF"u8); writer.Write(40); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(16000); writer.Write(32000);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(4); writer.Write(0);
        }
        string path = Path.Combine(_root, "manifest.jsonl");
        File.WriteAllLines(path, new[] { "a", "b" }.Select(id => JsonSerializer.Serialize(new
        {
            id, audio_path = audio ? "clip.wav" : "does-not-exist.wav", raw_text = "ciao", reference_raw = "ciao",
            reference_final = "Ciao?", reference_status = "provided", profile = "chat"
        })));
        return path;
    }

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class FakeEngines : ISttEngine, ITextCleaner
    {
        public int SttLoads, SttCalls, CleanerCalls;
        private static readonly EngineIdentity Identity = new("fake", "r", "h", "v", "cpu", "F16", "fake");
        Task ISttEngine.LoadAsync(CancellationToken ct) { SttLoads++; return Task.CompletedTask; }
        Task ITextCleaner.LoadAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<SpeechTranscript> TranscribeAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
        { SttCalls++; return Task.FromResult(new SpeechTranscript("ciao", Identity)); }
        public Task<CleanedText> CleanAsync(string raw, string profile, CancellationToken ct)
        { CleanerCalls++; return Task.FromResult(new CleanedText("Ciao?", Identity)); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
