using System.Text.Json;
using GangioKeet.Inference.Native;
using GangioKeet.Inference.Cleaning;

namespace GangioKeet.Eval;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0) throw new ArgumentException("Commands: devices, probe.");
            Dictionary<string, string> options = [];
            for (int i = 1; i < args.Length; i += 2)
            {
                if (i + 1 >= args.Length || !args[i].StartsWith("--")) throw new ArgumentException("Options require --name value.");
                options.Add(args[i], args[i + 1]);
            }
            string Required(string key) => options.TryGetValue(key, out string? value) ? value
                : throw new ArgumentException($"Missing {key}.");
            if (args[0] == "devices")
            {
                TranscribeApi api = new();
                api.Initialize(Required("--native-dir"));
                Console.WriteLine(JsonSerializer.Serialize(new { api.RuntimeVersion, Devices = api.ListDevices() }, ProbeCommand.JsonOptions));
                return 0;
            }
            using CancellationTokenSource cts = new();
            Console.CancelKeyPress += (_, evt) => { evt.Cancel = true; cts.Cancel(); };
            if (args[0] == "replay")
            {
                ReplayCommand replay = new(options.GetValueOrDefault("--model-root", "models"),
                    Required("--native-dir"), Required("--llama-dir"), Required("--runtime-lock"),
                    CleanupPolicy.Load(Required("--cleanup-policy")),
                    options.TryGetValue("--passes", out string? repeat) ? int.Parse(repeat) : 3,
                    options.GetValueOrDefault("--backend", "vulkan"));
                return await replay.RunAsync(Required("--catalog"), Required("--manifest"),
                    options.GetValueOrDefault("--stt"), options.GetValueOrDefault("--cleaner"),
                    Required("--phase"), Required("--output"), cts.Token);
            }
            if (args[0] != "probe") throw new ArgumentException($"Unknown command: {args[0]}");
            if (options.ContainsKey("--text-corpus")) return await CleanerProbeCommand.RunAsync(options, cts.Token);
            ProbeCommand probe = new(options.GetValueOrDefault("--model-root", "models"),
                Required("--native-dir"), options.GetValueOrDefault("--backend", "vulkan"),
                options.TryGetValue("--cancel-after-ms", out string? ms) ? int.Parse(ms) : null);
            int cycles = options.TryGetValue("--cycles", out string? count) ? int.Parse(count) : 1;
            if (cycles < 1 || cycles > 100) throw new ArgumentOutOfRangeException("--cycles");
            string output = Required("--output");
            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                string destination = cycles == 1 ? output : Path.Combine(Path.GetDirectoryName(output) ?? ".",
                    $"{Path.GetFileNameWithoutExtension(output)}-{cycle}{Path.GetExtension(output)}");
                int result = await probe.RunAsync(Required("--catalog"), Required("--candidate"),
                    Required("--wav"), destination, cts.Token);
                if (result != 0) return result;
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 2; }
    }
}
