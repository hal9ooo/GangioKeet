using System.Runtime.InteropServices;
using System.Text;
using GangioKeet.Inference.Native;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace GangioKeet.Inference.Cleaning;

public sealed class LlamaGenerator(ModelArtifact artifact, string modelRoot, string nativeDirectory,
    string runtimeLock, string backend = "vulkan") : ITextGenerator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private LLamaWeights? _weights;
    private ModelParams? _parameters;
    private EngineIdentity? _identity;
    private Task? _disposeTask;
    private int _disposed;
    public EngineIdentity Identity => _identity ?? throw new InvalidOperationException("Load the cleaner first.");

    public async Task LoadAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_weights is not null) return;
            await ModelCatalog.VerifyArtifactAsync(artifact, modelRoot, linked.Token).ConfigureAwait(false);
            await Task.Run(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                LlamaRuntime.Initialize(nativeDirectory, runtimeLock);
                linked.Token.ThrowIfCancellationRequested();
                string device = backend switch { "vulkan" => "Vulkan0", "cpu" => "CPU", _ => throw new ArgumentException("Unknown LLM backend.") };
                _parameters = new ModelParams(Path.GetFullPath(artifact.Files[0].Path, Path.GetFullPath(modelRoot)))
                {
                    ContextSize = 8192, BatchSize = 512, GpuLayerCount = backend == "vulkan" ? 999 : 0,
                    Devices = [device], Threads = 8, BatchThreads = 8
                };
                LLamaWeights weights = LLamaWeights.LoadFromFile(_parameters);
                if (linked.IsCancellationRequested) { weights.Dispose(); linked.Token.ThrowIfCancellationRequested(); }
                _weights = weights;
                string actual = "";
                for (nuint i = 0; i < NativeApi.ggml_backend_dev_count(); i++)
                {
                    nint pointer = NativeApi.ggml_backend_dev_get(i);
                    string name = Marshal.PtrToStringUTF8(NativeApi.ggml_backend_dev_name(pointer)) ?? "";
                    if (name == device) actual = name;
                }
                if (actual.Length == 0) { _weights.Dispose(); _weights = null; throw new InvalidOperationException("Requested native device unavailable."); }
                _identity = new EngineIdentity(artifact.ArtifactRepository ?? artifact.SourceCheckpoint, artifact.Revision,
                    artifact.Files[0].Sha256, "LLamaSharp/0.30.0", backend, artifact.Precision, actual);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<GeneratedText> GenerateAsync(string system, string prompt, int maxTokens, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_weights is null || _parameters is null) throw new InvalidOperationException("Load the cleaner first.");
            return await Task.Run(async () =>
            {
                linked.Token.ThrowIfCancellationRequested();
                string rendered;
                if (artifact.Family == "gemma4") rendered = GemmaTextTemplate.RenderForAutoBosTokenizer(system, prompt);
                else
                {
                    LLamaTemplate template = new(_weights) { AddAssistant = true };
                    template.Add("system", system).Add("user", prompt);
                    rendered = Encoding.UTF8.GetString(template.Apply());
                }
                // Official Qwen3 non-thinking continuation; no reasoning state shared between turns.
                if (artifact.Family == "qwen3") rendered += "<think>\n\n</think>\n\n";
                StatelessExecutor executor = new(_weights, _parameters);
                StringBuilder output = new();
                int tokens = 0;
                using DefaultSamplingPipeline sampling = new() { Temperature = 0, Seed = 42 };
                InferenceParams parameters = new() { MaxTokens = maxTokens,
                    SamplingPipeline = sampling };
                await foreach (string fragment in executor.InferAsync(rendered, parameters, linked.Token).ConfigureAwait(false))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    tokens++;
                    output.Append(fragment);
                }
                linked.Token.ThrowIfCancellationRequested();
                return new GeneratedText(output.ToString(), tokens >= maxTokens);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _weights?.Dispose(); _weights = null; _lifetime.Dispose(); }
        finally { _gate.Release(); }
    }
}
