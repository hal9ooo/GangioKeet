using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
using GangioKeet.Inference.Stt;

namespace GangioKeet.Eval;

public sealed class ReplayCommand(string modelRoot, string nativeDirectory, string llamaDirectory,
    string runtimeLock, CleanupPolicy policy, int passes = 3, string backend = "vulkan",
    Func<ModelArtifact, ISttEngine>? sttFactory = null, Func<ModelArtifact, ITextCleaner>? cleanerFactory = null,
    int? cancelAfterMs = null)
{
    public async Task<int> RunAsync(string catalog, string manifest, string? sttId, string? cleanerId,
        string phase, string output, CancellationToken ct)
    {
        List<Dictionary<string, object?>> rows = [];
        ISttEngine? stt = null;
        ITextCleaner? cleaner = null;
        InferencePipeline? pipeline = null;
        double coldStartMs = 0;
        double disposeMs = 0;
        Stopwatch cold = Stopwatch.StartNew();
        CancellationTokenSource? turnCancellation = null;
        int exit = 0;
        string status = "completed";
        string? errorType = null;
        string manifestSha = "";
        try
        {
            if (passes is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(passes));
            if (phase is not ("asr" or "cleaner" or "pipeline")) throw new ArgumentException("Unknown replay phase.");
            if ((phase != "cleaner" && sttId is null) || (phase != "asr" && cleanerId is null))
                throw new ArgumentException("Replay phase requires the corresponding model IDs.");
            ModelCatalog models = ModelCatalog.Load(catalog);
            string text = File.ReadAllText(manifest).Replace("\r\n", "\n");
            manifestSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
            List<Dictionary<string, JsonElement>> clips = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(x)!).ToList();
            if (clips.Count == 0 || clips.Select(x => x["id"].GetString()).Distinct().Count() != clips.Count)
                throw new InvalidDataException("Replay requires nonempty, unique clip IDs.");
            string baseDirectory = Path.GetDirectoryName(Path.GetFullPath(manifest))!;
            WavInput ReadAudio(Dictionary<string, JsonElement> clip)
            {
                string audio = Path.GetFullPath(clip["audio_path"].GetString()!, baseDirectory);
                string prefix = baseDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                if (!audio.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Audio path escapes the corpus.");
                using FileStream file = File.OpenRead(audio);
                WavInput wav = WavInput.Read(file);
                if (clip.TryGetValue("audio_sha256", out JsonElement expected) &&
                    Convert.ToHexStringLower(SHA256.HashData(wav.Pcm)) != expected.GetString())
                    throw new InvalidDataException("Audio PCM hash mismatch.");
                return wav;
            }
            if (phase != "cleaner")
            {
                ModelArtifact artifact = models.Get(sttId!);
                stt = sttFactory?.Invoke(artifact) ?? new TranscribeSttEngine(artifact, modelRoot, nativeDirectory, backend);
            }
            if (phase != "asr")
            {
                ModelArtifact artifact = models.Get(cleanerId!);
                cleaner = cleanerFactory?.Invoke(artifact) ?? new LlamaTextCleaner(new LlamaGenerator(artifact,
                    modelRoot, llamaDirectory, runtimeLock, backend), policy);
            }
            if (phase == "pipeline")
            {
                pipeline = new(stt!, cleaner!, policy, TimeProvider.System);
                await pipeline.WarmUpAsync(ct);
            }
            else if (stt is not null)
            {
                await stt.LoadAsync(ct);
                await stt.TranscribeAsync(ReadAudio(clips[0]).Pcm, ct);
            }
            else
            {
                await cleaner!.LoadAsync(ct);
                await cleaner.CleanAsync(clips[0]["raw_text"].GetString()!, "default", ct);
            }
            coldStartMs = cold.Elapsed.TotalMilliseconds;
            CancellationToken runToken = ct;
            if (cancelAfterMs is int milliseconds)
            {
                turnCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                turnCancellation.CancelAfter(milliseconds);
                runToken = turnCancellation.Token;
            }
            for (int pass = 1; pass <= passes; pass++)
            {
                foreach (Dictionary<string, JsonElement> clip in clips)
                {
                    runToken.ThrowIfCancellationRequested();
                    Dictionary<string, object?> row = clip.ToDictionary(x => x.Key, x => (object?)x.Value);
                    row["pass"] = pass;
                    row["warm"] = true;
                    WavInput? wav = phase != "cleaner" ? ReadAudio(clip) : null;
                    row["duration_seconds"] = wav?.DurationSeconds ?? (clip.TryGetValue("duration_seconds", out JsonElement duration) ? duration.GetDouble() : null);
                    string profile = clip.TryGetValue("profile", out JsonElement value) ? value.GetString()! : "default";
                    Stopwatch timer = Stopwatch.StartNew();
                    try
                    {
                        if (pipeline is not null)
                        {
                            PipelineResult result = await pipeline.ProcessAsync(new(wav!.Pcm, profile), runToken);
                            row["raw_text"] = result.RawText; row["final_text"] = result.FinalText;
                            row["stt_ms"] = result.SttMs; row["llm_ms"] = result.LlmMs;
                            row["total_ms"] = result.TotalMs; row["warnings"] = result.Warnings;
                            row["stt_engine"] = result.Stt; row["cleaner_engine"] = result.Cleaner;
                        }
                        else if (stt is not null)
                        {
                            SpeechTranscript result = await stt.TranscribeAsync(wav!.Pcm, runToken);
                            row["raw_text"] = result.Text; row["final_text"] = null;
                            row["stt_ms"] = timer.Elapsed.TotalMilliseconds; row["llm_ms"] = 0;
                            row["stt_engine"] = result.Engine; row["warnings"] = Array.Empty<string>();
                        }
                        else
                        {
                            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                            deadline.CancelAfter(policy.LlmTimeout);
                            CleanedText result = await cleaner!.CleanAsync(clip["raw_text"].GetString()!, profile, deadline.Token);
                            deadline.Token.ThrowIfCancellationRequested();
                            row["final_text"] = result.Text; row["cleaner_engine"] = result.Engine;
                            row["llm_ms"] = timer.Elapsed.TotalMilliseconds; row["stt_ms"] = 0;
                            row["warnings"] = Array.Empty<string>();
                        }
                        runToken.ThrowIfCancellationRequested();
                        row["status"] = "completed";
                    }
                    catch (OperationCanceledException) when (!runToken.IsCancellationRequested)
                    { row["status"] = "failed"; row["warnings"] = new[] { "llm_timeout" }; row["final_text"] = null; }
                    catch (Exception error) when (!runToken.IsCancellationRequested)
                    { row["status"] = "failed"; row["error_type"] = error.GetType().Name; row["final_text"] = null; }
                    row["total_ms"] = timer.Elapsed.TotalMilliseconds;
                    rows.Add(row);
                    if (pass == 1 || clips.Count < 10) Console.WriteLine($"{phase} pass={pass} clip={row["id"]} status={row["status"]}");
                }
            }
        }
        catch (OperationCanceledException) { exit = 3; status = "cancelled"; }
        catch (Exception error) { exit = 2; status = "failed"; errorType = error.GetType().FullName; Console.Error.WriteLine(error.Message); }
        finally
        {
            Stopwatch shutdown = Stopwatch.StartNew();
            try
            {
                if (pipeline is not null) await pipeline.DisposeAsync();
                else
                {
                    try { if (cleaner is not null) await cleaner.DisposeAsync(); }
                    finally { if (stt is not null) await stt.DisposeAsync(); }
                }
            }
            finally
            {
                disposeMs = shutdown.Elapsed.TotalMilliseconds;
                turnCancellation?.Dispose();
            }
        }
        string[] nativeModules = Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
            .Where(x => new[] { "ggml", "lgml", "llama", "transcribe" }.Any(prefix => x.ModuleName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .Select(x => x.FileName).Order().ToArray();
        await ReportWriter.WriteAsync(output, new { schema_version = 1, phase, stt_id = sttId, cleaner_id = cleanerId,
            status, error_type = errorType, manifest_sha256_utf8_lf = manifestSha, passes, cancel_after_ms = cancelAfterMs,
            cold_start_ms = coldStartMs, elapsed_ms = cold.Elapsed.TotalMilliseconds, dispose_ms = disposeMs,
            policy_sha256 = policy.Sha256, prompt_version = CleanerPrompt.Version, prompt_sha256 = CleanerPrompt.Sha256, backend,
            clips = rows, loaded_native_modules = nativeModules, peak_working_set_bytes = Process.GetCurrentProcess().PeakWorkingSet64 }, CancellationToken.None);
        Console.WriteLine("Report: " + Path.GetFullPath(output));
        return exit;
    }
}
