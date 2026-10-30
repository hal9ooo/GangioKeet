using GangioKeet.Inference;

namespace GangioKeet.Client.Platform;

public sealed class ModelSelectionService
{
    private readonly InferencePipeline _pipeline;
    private readonly ILocalModelResolver _resolver;
    private readonly IModelProvisioner _provisioner;
    private readonly string _selectionPath;
    private readonly SemaphoreSlim _applyGate = new(1, 1);
    private ModelSelection _current;
    private string _loadedSttId;
    private string? _loadedCleanerId;

    public ModelSelectionService(InferencePipeline pipeline, ILocalModelResolver resolver,
        IModelProvisioner provisioner, string selectionPath, ModelSelection initial)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _provisioner = provisioner ?? throw new ArgumentNullException(nameof(provisioner));
        _selectionPath = selectionPath ?? throw new ArgumentNullException(nameof(selectionPath));
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
        _loadedSttId = initial.SttId;
        _loadedCleanerId = initial.CleanerId;
    }

    public ModelSelection Current => _current;

    public async Task ApplyAsync(ModelSelection desired, IProgress<ModelProvisionProgress>? progress,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(desired);
        await _applyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ResolvedModelSelection resolved = _resolver.Resolve(desired);
            bool sttChanged = !string.Equals(desired.SttId, _loadedSttId, StringComparison.Ordinal);
            bool cleanerChanged = !string.Equals(desired.CleanerId, _loadedCleanerId, StringComparison.Ordinal);
            bool needsCleaner = desired.RefinementEnabled && resolved.Cleaner is not null;
            bool needsProvisioning = sttChanged || (needsCleaner && (cleanerChanged || !_pipeline.HasCleaner));
            if (needsProvisioning)
            {
                List<ModelArtifact> artifacts = [resolved.Stt];
                if (needsCleaner) artifacts.Add(resolved.Cleaner!);
                await _provisioner.EnsureModelsAsync(artifacts, _resolver.ModelRoot, progress, ct).ConfigureAwait(false);
            }

            ISttEngine? newStt = sttChanged ? _resolver.CreateStt(resolved.Stt) : null;
            ITextCleaner? newCleaner = null;
            if (needsCleaner && (cleanerChanged || !_pipeline.HasCleaner))
                newCleaner = _resolver.CreateCleaner(resolved.Cleaner!);

            if (sttChanged || newCleaner is not null || desired.RefinementEnabled != _current.RefinementEnabled)
                await _pipeline.SwapEnginesAsync(newStt, newCleaner, desired.RefinementEnabled, ct).ConfigureAwait(false);

            if (newStt is not null) _loadedSttId = desired.SttId;
            if (newCleaner is not null) _loadedCleanerId = desired.CleanerId;
            ModelSelectionFile.Save(_selectionPath, desired);
            _current = desired;
        }
        finally { _applyGate.Release(); }
    }
}
