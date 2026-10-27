using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Protocol;
using GangioKeet.Inference;

namespace GangioKeet.Client.Recording;

/// <summary>
/// Writes completed local dictations as PCM16 mono 16 kHz WAV plus one JSONL
/// row. Audio is renamed before the manifest is replaced atomically, so a
/// crash can leave an orphan WAV but never a manifest row pointing at a
/// partial file. Reference fields stay empty until a human review.
/// </summary>
public sealed class RecordingStore : IRecordingWriter
{
    public const string Schema = "gangio-keet-recording-v1";
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RecordingConfig _config;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _manifestGate = new(1, 1);

    public RecordingStore(RecordingConfig config, TimeProvider? time = null)
    {
        _config = config;
        _time = time ?? TimeProvider.System;
        Directory = Path.GetFullPath(config.Directory);
    }

    public bool Enabled => _config.Enabled;
    public string Directory { get; }
    public string AudioDirectory => Path.Combine(Directory, "audio");
    public string ManifestPath => Path.Combine(Directory, "manifest.jsonl");

    public async Task<string?> RecordAsync(ReadOnlyMemory<byte> pcm, PipelineResult result,
        StartMessage start, CancellationToken ct)
    {
        if (!_config.Enabled || pcm.IsEmpty) return null;
        try
        {
            ct.ThrowIfCancellationRequested();
            string id = $"{_time.GetUtcNow():yyyyMMdd'T'HHmmssfffffff'Z'}-{Guid.NewGuid():N}"[..40];
            string relative = $"audio/{id}.wav";
            string audioPath = Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(AudioDirectory);
            WriteWave(audioPath, pcm.Span);

            RecordingEntry entry = Entry(id, relative, pcm, result, start);
            await AppendManifestAsync(JsonSerializer.Serialize(entry, JsonOptions), ct).ConfigureAwait(false);
            return id;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or OperationCanceledException)
        {
            return null;
        }
    }

    private RecordingEntry Entry(string id, string relative, ReadOnlyMemory<byte> pcm,
        PipelineResult result, StartMessage start) => new(
        Schema,
        id,
        relative,
        Convert.ToHexStringLower(SHA256.HashData(pcm.Span)),
        pcm.Length,
        Math.Round(pcm.Length / (2.0 * 16000), 3),
        16000,
        1,
        2,
        _time.GetUtcNow().ToString("O"),
        "local",
        start.Profile,
        result.Stt.ModelId,
        result.Cleaner?.ModelId,
        result.RawText,
        result.FinalText,
        result.Warnings,
        result.SttMs,
        result.LlmMs,
        result.TotalMs,
        "",
        "",
        "unreviewed",
        "it_general",
        [],
        string.IsNullOrWhiteSpace(result.RawText) && string.IsNullOrWhiteSpace(result.FinalText),
        result.Stt,
        result.Cleaner);

    private static void WriteWave(string path, ReadOnlySpan<byte> pcm)
    {
        string temporary = path + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WriteWave(stream, pcm);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static void WriteWave(Stream stream, ReadOnlySpan<byte> pcm)
    {
        using BinaryWriter writer = new(stream, Utf8NoBom, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16000);
        writer.Write(32000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
    }

    private async Task AppendManifestAsync(string line, CancellationToken ct)
    {
        await _manifestGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            List<string> lines = File.Exists(ManifestPath)
                ? [.. File.ReadAllLines(ManifestPath, Encoding.UTF8)]
                : [];
            lines.Add(line);
            await ReplaceManifestAsync(lines, ct).ConfigureAwait(false);
        }
        finally { _manifestGate.Release(); }
    }

    public async Task ReplaceManifestAsync(IReadOnlyList<string> lines, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Directory);
        string temporary = ManifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary,
                string.Join("\n", lines) + (lines.Count > 0 ? "\n" : ""), Utf8NoBom, ct).ConfigureAwait(false);
            File.Move(temporary, ManifestPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
