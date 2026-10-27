using System.Diagnostics;
using System.Text.Json;
using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
using GangioKeet.Inference.Stt;

namespace GangioKeet.Eval;

public static class CleanerProbeCommand
{
    public static async Task<int> RunAsync(IReadOnlyDictionary<string, string> options, CancellationToken ct)
    {
        string Required(string key) => options.TryGetValue(key, out string? value) ? value : throw new ArgumentException("Missing " + key);
        ModelCatalog catalog = ModelCatalog.Load(Required("--catalog"));
        string root = options.GetValueOrDefault("--model-root", "models");
        string candidate = Required("--candidate");
        CleanupPolicy policy = CleanupPolicy.Load(Required("--cleanup"));
        await using TranscribeSttEngine? stt = options.TryGetValue("--asr-candidate", out string? asr)
            ? new(catalog.Get(asr), root, Required("--native-dir"), "vulkan") : null;
        string? before = null;
        string? after = null;
        WavInput? audio = null;
        if (stt is not null)
        {
            using FileStream file = File.OpenRead(Required("--wav"));
            audio = WavInput.Read(file);
            await stt.LoadAsync(ct);
            before = (await stt.TranscribeAsync(audio.Pcm, ct)).Text;
        }
        LlamaGenerator generator = new(catalog.Get(candidate), root,
            Required("--llama-dir"), Required("--runtime-lock"), options.GetValueOrDefault("--backend", "vulkan"));
        await using LlamaTextCleaner cleaner = new(generator, policy);
        Stopwatch timer = Stopwatch.StartNew();
        await cleaner.LoadAsync(ct);
        double loadMs = timer.Elapsed.TotalMilliseconds;
        List<object> rows = [];
        EngineIdentity identity = generator.Identity;
        foreach (string line in File.ReadLines(Required("--text-corpus")))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement clip = document.RootElement;
            string raw = clip.GetProperty("raw_text").GetString()!;
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(policy.LlmTimeout);
            string? text = null;
            string? warning = null;
            timer.Restart();
            try
            {
                CleanedText result = await cleaner.CleanAsync(raw,
                    clip.TryGetProperty("profile", out JsonElement profile) ? profile.GetString()! : "chat", deadline.Token);
                text = result.Text;
                identity = result.Engine;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { warning = "llm_timeout"; }
            catch (InvalidDataException error) { warning = error.Message; }
            rows.Add(new { id = clip.GetProperty("id").GetString(), raw_text = raw,
                reference_final_text = clip.GetProperty("reference_final_text").GetString(), text,
                llm_ms = timer.Elapsed.TotalMilliseconds, warning });
            ct.ThrowIfCancellationRequested();
        }
        if (stt is not null) after = (await stt.TranscribeAsync(audio!.Pcm, ct)).Text;
        string output = Path.GetFullPath(Required("--output"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { schema_version = 1, phase = "cleaner",
            candidate, engine = identity, policy_sha256 = policy.Sha256, prompt_version = CleanerPrompt.Version, prompt_sha256 = CleanerPrompt.Sha256,
            load_ms = loadMs, clips = rows, coexistence = new { asr, before, after, same_asr_text = before == after },
            peak_working_set_bytes = Process.GetCurrentProcess().PeakWorkingSet64 }, ProbeCommand.JsonOptions), ct);
        Console.WriteLine("Report: " + output);
        return 0;
    }
}
