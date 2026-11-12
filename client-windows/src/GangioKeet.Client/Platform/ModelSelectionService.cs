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
            bool needsNewCleaner = needsCleaner && (cleanerChanged || !_pipeline.HasCleaner);
            bool provisionStt = sttChanged;
            bool provisionCleaner = needsNewCleaner && resolved.Cleaner!.Files.Count > 0;
            if (provisionStt || provisionCleaner)
            {
                List<ModelArtifact> artifacts = [];
                if (provisionStt) artifacts.Add(resolved.Stt);
                if (provisionCleaner) artifacts.Add(resolved.Cleaner!);
                await _provisioner.EnsureModelsAsync(artifacts, _resolver.ModelRoot, progress, ct).ConfigureAwait(false);
            }
            if (sttChanged)
                await _resolver.EnsureRuntimeAsync(resolved.Stt, progress, ct).ConfigureAwait(false);

            ISttEngine? newStt = sttChanged ? _resolver.CreateStt(resolved.Stt) : null;
            ITextCleaner? newCleaner = null;
            if (needsNewCleaner)
                newCleaner = _resolver.CreateCleaner(resolved.Cleaner!);

            if (sttChanged || newCleaner is not null || desired.RefinementEnabled != _current.RefinementEnabled)
            {
                Action<string>? onStage = progress is null ? null : stage =>
                {
                    ModelProvisionStatus status = stage switch
                    {
                        "stt_loading" => ModelProvisionStatus.Loading,
                        "stt_warming" => ModelProvisionStatus.WarmingUp,
                        "cleaner_loading" => ModelProvisionStatus.Loading,
                        "cleaner_warming" => ModelProvisionStatus.WarmingUp,
                        _ => ModelProvisionStatus.Loading
                    };
                    string id = stage.StartsWith("stt") ? resolved.Stt.Id : (resolved.Cleaner?.Id ?? "");
                    progress.Report(new ModelProvisionProgress(id, "", status, 0, null));
                };
                await _pipeline.SwapEnginesAsync(newStt, newCleaner, desired.RefinementEnabled, ct, onStage).ConfigureAwait(false);
            }

            if (newStt is not null) _loadedSttId = desired.SttId;
            if (newCleaner is not null) _loadedCleanerId = desired.CleanerId;
            ModelSelectionFile.Save(_selectionPath, desired);
            _current = desired;
        }
        finally { _applyGate.Release(); }
    }
}
