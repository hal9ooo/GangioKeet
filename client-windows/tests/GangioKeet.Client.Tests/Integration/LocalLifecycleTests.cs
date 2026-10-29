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
    public async Task RealSelectedPairStopsWithinFiveSeconds()
    {
        Skip.IfNot(Enabled, "RUN_LOCAL_LIFECYCLE != 1");
        AppRuntime runtime = CreateRuntime();
        await runtime.StartAsync(default);
        Assert.True(runtime.State == RuntimeState.Ready, runtime.Failure?.ToString());

        Stopwatch stop = Stopwatch.StartNew();
        await runtime.StopAsync(default);
        stop.Stop();

        Assert.Equal(RuntimeState.Stopped, runtime.State);
        _output.WriteLine($"stop_ms={stop.Elapsed.TotalMilliseconds:0.0}");
        Assert.True(stop.Elapsed < TimeSpan.FromSeconds(5), $"stop took {stop.Elapsed}");
    }

    [SkippableFact]
    public async Task RealSelectedPairStopDuringLoadIsMeasuredAndCompletes()
    {
        Skip.IfNot(Enabled, "RUN_LOCAL_LIFECYCLE != 1");
        Skip.If(HasNativeModule("transcribe.dll"), "requires a fresh process to observe the load");
        AppRuntime runtime = CreateRuntime();
        _ = runtime.StartAsync(default);
        await WaitForNativeModuleAsync("transcribe.dll", TimeSpan.FromSeconds(30));
        await Task.Delay(1000);

        Stopwatch stop = Stopwatch.StartNew();
        await runtime.StopAsync(default);
        stop.Stop();

        Assert.Equal(RuntimeState.Stopped, runtime.State);
        _output.WriteLine($"stop_during_load_ms={stop.Elapsed.TotalMilliseconds:0.0}");
        Assert.True(stop.Elapsed < TimeSpan.FromSeconds(60), $"stop during load took {stop.Elapsed}");
    }

    private static bool HasNativeModule(string name)
    {
        try
        {
            return Process.GetCurrentProcess().Modules.Cast<ProcessModule>()
                .Any(module => string.Equals(module.ModuleName, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception) { return false; }
    }

    private static async Task WaitForNativeModuleAsync(string name, TimeSpan timeout)
    {
        DateTime until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (HasNativeModule(name)) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Native module did not load: {name}");
    }

    private static AppRuntime CreateRuntime()
    {
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
                Backend = "vulkan",
                ModelSelectionPath = selection,
                CleanupPolicyPath = Path.Combine(repo, "client-windows", "cleanup.example.yaml"),
            },
        };
        LocalInferenceSetup setup = LocalInferenceFactory.Create(config,
            modelRoot: Environment.GetEnvironmentVariable("GANGIOKEET_MODEL_ROOT")
                ?? Path.Combine(repo, "models"));
        return new AppRuntime(setup.Pipeline);
    }
}
