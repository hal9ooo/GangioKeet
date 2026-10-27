using System.Diagnostics;
using System.Text.Json;
using GangioKeet.Inference;
using GangioKeet.Inference.Stt;

namespace GangioKeet.Eval;

public sealed class ProbeCommand(string modelRoot, string nativeDirectory, string backend = "vulkan", int? cancelAfterMs = null)
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, WriteIndented = true
    };

    public async Task<int> RunAsync(string catalog, string candidate, string wavPath, string output, CancellationToken ct)
    {
        Stopwatch timer = Stopwatch.StartNew();
        double loadMs = 0;
        object report;
        int exit;
        try
        {
            ModelArtifact artifact = ModelCatalog.Load(catalog).Get(candidate);
            using FileStream file = File.OpenRead(wavPath);
            WavInput input = WavInput.Read(file);
            await using TranscribeSttEngine engine = new(artifact, modelRoot, nativeDirectory, backend);
            timer.Restart();
            await engine.LoadAsync(ct);
            loadMs = timer.Elapsed.TotalMilliseconds;
            using CancellationTokenSource decodeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (cancelAfterMs is int milliseconds) decodeCts.CancelAfter(milliseconds);
            timer.Restart();
            SpeechTranscript transcript = await engine.TranscribeAsync(input.Pcm, decodeCts.Token);
            report = new { schema_version = 1, phase = "probe", candidate, status = "completed",
                engine = transcript.Engine, text = transcript.Text, load_ms = loadMs,
                inference_ms = timer.Elapsed.TotalMilliseconds, input.DurationSeconds,
                peak_working_set_bytes = Process.GetCurrentProcess().PeakWorkingSet64 };
            exit = 0;
        }
        catch (OperationCanceledException)
        {
            report = new { schema_version = 1, phase = "probe", candidate, status = "cancelled",
                load_ms = loadMs, elapsed_ms = timer.Elapsed.TotalMilliseconds };
            exit = 3;
        }
        catch (Exception error)
        {
            report = new { schema_version = 1, phase = "probe", candidate, status = "unavailable",
                error = error.Message, error_type = error.GetType().FullName };
            exit = 2;
        }
        string destination = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(report, JsonOptions), CancellationToken.None);
        Console.WriteLine($"Report: {destination}");
        return exit;
    }
}
