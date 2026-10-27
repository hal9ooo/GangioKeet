using System.Diagnostics;
using System.IO;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Platform;
using Xunit;
using Xunit.Abstractions;

namespace GangioKeet.Client.Tests.Integration;

public sealed class LocalLifecycleTests
{
    private readonly ITestOutputHelper _output;
    public LocalLifecycleTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled => Environment.GetEnvironmentVariable("RUN_LOCAL_LIFECYCLE") == "1";

    [SkippableFact]
    public async Task RealSelectedPairStopsWithinFiveSecondsWithoutChildren()
    {
        Skip.IfNot(Enabled, "RUN_LOCAL_LIFECYCLE != 1");
        string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", ".."));
        string selection = Environment.GetEnvironmentVariable("GANGIOKEET_SELECTION")
            ?? Path.Combine(repo, "client-windows", "models", "selected.json");
        Environment.SetEnvironmentVariable("GANGIOKEET_TRANSCRIBE_DIR",
            Path.Combine(repo, "client-windows", "native", "build", "transcribe-0.4.1",
                "payload", "transcribe-native-windows-x86_64-cpu-vulkan"));
        Environment.SetEnvironmentVariable("GANGIOKEET_LLAMA_DIR",
            Path.Combine(repo, "client-windows", "native", "build", "llama-0.30.0"));
        Environment.SetEnvironmentVariable("GANGIOKEET_RUNTIME_LOCK",
            Path.Combine(repo, "client-windows", "native", "runtime-lock.json"));

        ClientConfig config = new()
        {
            Inference = new InferenceConfig
            {
                Mode = "local",
                Backend = "vulkan",
                ModelSelectionPath = selection,
                CleanupPolicyPath = Path.Combine(repo, "client-windows", "cleanup.example.yaml"),
            },
        };
        LocalInferenceSetup setup = LocalInferenceFactory.Create(config,
            modelRoot: Path.Combine(repo, "models"));
        AppRuntime runtime = new(setup.Pipeline);
        await runtime.StartAsync(default);
        Assert.True(runtime.State == RuntimeState.Ready, runtime.Failure?.ToString());

        Stopwatch stop = Stopwatch.StartNew();
        await runtime.StopAsync(default);
        stop.Stop();

        Assert.Equal(RuntimeState.Stopped, runtime.State);
        _output.WriteLine($"stop_ms={stop.Elapsed.TotalMilliseconds:0.0}");
        Assert.True(stop.Elapsed < TimeSpan.FromSeconds(5), $"stop took {stop.Elapsed}");
    }
}
