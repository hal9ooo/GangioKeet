using System.IO;
using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
using GangioKeet.Inference.Stt;

namespace GangioKeet.Client.Platform;

public interface ILocalModelResolver
{
    string ModelRoot { get; }
    ResolvedModelSelection Resolve(ModelSelection selection);
    Task EnsureRuntimeAsync(ModelArtifact artifact, IProgress<ModelProvisionProgress>? progress,
        CancellationToken ct);
    ISttEngine CreateStt(ModelArtifact artifact);
    ITextCleaner CreateCleaner(ModelArtifact artifact);
}

public sealed class LocalModelResolver : ILocalModelResolver
{
    private readonly string _catalogPath;
    private readonly string _integrityPath;
    private readonly string _runtimeLockPath;
    private readonly string _cleanupPolicyPath;
    private readonly string _nativeDirectory;
    private readonly string _llamaDirectory;
    private readonly string _backend;
    private readonly CleanupPolicy _policy;
    private readonly INativeRuntimeProvisioner? _runtimeProvisioner;

    public LocalModelResolver(string modelRoot, string catalogPath, string integrityPath,
        string runtimeLockPath, string cleanupPolicyPath, string nativeDirectory,
        string llamaDirectory, string backend, CleanupPolicy policy,
        INativeRuntimeProvisioner? runtimeProvisioner = null)
    {
        ModelRoot = modelRoot ?? throw new ArgumentNullException(nameof(modelRoot));
        _catalogPath = catalogPath ?? throw new ArgumentNullException(nameof(catalogPath));
        _integrityPath = integrityPath ?? throw new ArgumentNullException(nameof(integrityPath));
        _runtimeLockPath = runtimeLockPath ?? throw new ArgumentNullException(nameof(runtimeLockPath));
        _cleanupPolicyPath = cleanupPolicyPath ?? throw new ArgumentNullException(nameof(cleanupPolicyPath));
        _nativeDirectory = nativeDirectory ?? throw new ArgumentNullException(nameof(nativeDirectory));
        _llamaDirectory = llamaDirectory ?? throw new ArgumentNullException(nameof(llamaDirectory));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _runtimeProvisioner = runtimeProvisioner;
    }

    public string ModelRoot { get; }

    public IReadOnlyList<ModelArtifact> Candidates { get; private set; } = [];

    public ResolvedModelSelection Resolve(ModelSelection selection)
    {
        IntegrityManifest manifest = IntegrityManifestFile.Load(_integrityPath);
        IntegrityManifestFile.Verify(manifest, _catalogPath, _runtimeLockPath, _cleanupPolicyPath);
        ModelCatalog catalog = ModelCatalog.Load(_catalogPath);
        Candidates = catalog.Candidates;
        return ModelSelectionResolver.Resolve(selection, catalog);
    }

    public async Task EnsureRuntimeAsync(ModelArtifact artifact,
        IProgress<ModelProvisionProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.RuntimeId != NeMoSpeechRuntimeProvisioner.RuntimeId) return;
        if (_runtimeProvisioner is null)
            throw new InvalidOperationException("The NeMo-Speech runtime provisioner is not configured.");
        await _runtimeProvisioner.EnsureAsync(artifact.RuntimeId, progress, ct).ConfigureAwait(false);
    }

    public ISttEngine CreateStt(ModelArtifact artifact) => artifact.RuntimeId switch
    {
        "transcribe-vulkan" => new TranscribeSttEngine(artifact, ModelRoot, _nativeDirectory, _backend,
            runtimeLock: _runtimeLockPath),
        "nemo-speech-vulkan" => new NeMoSpeechSttEngine(artifact, ModelRoot, _backend,
            ct => _runtimeProvisioner?.EnsureAsync(artifact.RuntimeId, null, ct)
                ?? throw new InvalidOperationException("The NeMo-Speech runtime provisioner is not configured.")),
        _ => throw new InvalidDataException($"Unsupported ASR runtime: {artifact.RuntimeId}.")
    };

    public ITextCleaner CreateCleaner(ModelArtifact artifact) =>
        new LlamaTextCleaner(new LlamaGenerator(artifact, ModelRoot, _llamaDirectory, _runtimeLockPath, _backend), _policy);
}
