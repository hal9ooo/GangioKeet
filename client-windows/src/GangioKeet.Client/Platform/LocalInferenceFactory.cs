using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GangioKeet.Client.Configuration;
using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
using GangioKeet.Inference.Stt;

namespace GangioKeet.Client.Platform;

public sealed record LocalInferenceSetup(IInferencePipeline Pipeline, string SttId, string CleanerId,
    string Backend, string PromptVersion, string PromptSha256, string PolicySha256,
    ModelArtifact? SttArtifact, ModelArtifact? CleanerArtifact, bool RefinementEnabled)
{
    public static LocalInferenceSetup Failed(ClientConfig config, Exception error) => new(
        new FailedInferencePipeline(error), "non disponibile", "nessuno", config.Inference.Backend,
        CleanerPrompt.Version, CleanerPrompt.Sha256, "", null, null, false);
}

/// <summary>
/// Costruisce la pipeline locale da <c>selected.json</c> (v1) o
/// <c>selection.json</c> (v2) e dagli artefatti installati. Non crea client
/// HTTP, monitor o processi figli.
/// </summary>
public static class LocalInferenceFactory
{
    public static LocalInferenceSetup Create(ClientConfig config, string? nativeDirectory = null,
        string? llamaDirectory = null, string? runtimeLockPath = null, string? modelRoot = null,
        Func<ModelArtifact, ISttEngine>? sttFactory = null,
        Func<ModelArtifact, ITextCleaner>? cleanerFactory = null,
        INativeRuntimeProvisioner? runtimeProvisioner = null)
    {
        string rawSelection = config.Inference.ModelSelectionPath;
        string selectionPath = Path.IsPathRooted(rawSelection)
            ? rawSelection
            : (File.Exists(Path.Combine(AppContext.BaseDirectory, rawSelection))
                ? Path.Combine(AppContext.BaseDirectory, rawSelection)
                : Path.GetFullPath(rawSelection));
        ModelSelection selection = ModelSelectionFile.Load(selectionPath);

        string selectedRoot = Path.GetDirectoryName(selectionPath)
            ?? throw new InvalidDataException("Model selection has no parent directory.");
        string models = modelRoot ?? selectedRoot;
        string install = AppContext.BaseDirectory;
        string transcribe = nativeDirectory ?? Environment.GetEnvironmentVariable("GANGIOKEET_TRANSCRIBE_DIR")
            ?? Path.Combine(install, "native", "transcribe");
        string llama = llamaDirectory ?? Environment.GetEnvironmentVariable("GANGIOKEET_LLAMA_DIR")
            ?? Path.Combine(install, "native", "llama");
        string lockPath = runtimeLockPath ?? Environment.GetEnvironmentVariable("GANGIOKEET_RUNTIME_LOCK")
            ?? Path.Combine(install, "native", "runtime-lock.json");
        string backend = config.Inference.Backend;
        CleanupPolicy policy = CleanupPolicy.Load(config.Inference.CleanupPolicyPath);

        if (selection.SchemaVersion == 1)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(selectionPath));
            JsonElement root = document.RootElement;
            ModelArtifact stt = ReadModel(root.GetProperty("stt"));
            ModelArtifact cleaner = ReadModel(root.GetProperty("cleaner"));
            VerifySelection(root, lockPath, policy);

            ISttEngine sttEngine = sttFactory?.Invoke(stt)
                ?? new TranscribeSttEngine(stt, models, transcribe, backend, runtimeLock: lockPath);
            ITextCleaner cleanerEngine = cleanerFactory?.Invoke(cleaner)
                ?? new LlamaTextCleaner(new LlamaGenerator(cleaner, models, llama, lockPath, backend), policy);
            return new(new InferencePipeline(sttEngine, cleanerEngine, policy, TimeProvider.System),
                stt.Id, cleaner.Id, backend, CleanerPrompt.Version, CleanerPrompt.Sha256, policy.Sha256,
                stt, cleaner, true);
        }

        string catalogPath = File.Exists(Path.Combine(selectedRoot, "candidates.json"))
            ? Path.Combine(selectedRoot, "candidates.json")
            : Path.Combine(models, "candidates.json");
        string integrityPath = File.Exists(Path.Combine(selectedRoot, "integrity.json"))
            ? Path.Combine(selectedRoot, "integrity.json")
            : Path.Combine(models, "integrity.json");
        LocalModelResolver resolver = new(models, catalogPath, integrityPath, lockPath,
            config.Inference.CleanupPolicyPath, transcribe, llama, backend, policy, runtimeProvisioner);
        ResolvedModelSelection resolved = resolver.Resolve(selection);

        ISttEngine selectedStt = sttFactory?.Invoke(resolved.Stt) ?? resolver.CreateStt(resolved.Stt);
        ITextCleaner? selectedCleaner = null;
        if (selection.RefinementEnabled && resolved.Cleaner is not null)
        {
            selectedCleaner = cleanerFactory?.Invoke(resolved.Cleaner) ?? resolver.CreateCleaner(resolved.Cleaner);
        }

        InferencePipeline pipeline = new(selectedStt, selectedCleaner, policy, TimeProvider.System,
            selection.RefinementEnabled);
        string cleanerId = selection.RefinementEnabled && resolved.Cleaner is not null
            ? resolved.Cleaner.Id
            : "nessuno";
        return new(pipeline, resolved.Stt.Id, cleanerId, backend, CleanerPrompt.Version,
            CleanerPrompt.Sha256, policy.Sha256, resolved.Stt, resolved.Cleaner, selection.RefinementEnabled);
    }

    private static void VerifySelection(JsonElement selected, string lockPath, CleanupPolicy policy)
    {
        JsonElement runtime = selected.GetProperty("runtime");
        string expectedLock = runtime.GetProperty("lock_sha256").GetString()!;
        string actualLock = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(lockPath)));
        if (!string.Equals(expectedLock, actualLock, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("selected.json runtime lock hash does not match native/runtime-lock.json.");

        JsonElement prompt = selected.GetProperty("prompt");
        if (!string.Equals(prompt.GetProperty("version").GetString(), CleanerPrompt.Version, StringComparison.Ordinal)
            || !string.Equals(prompt.GetProperty("sha256").GetString(), CleanerPrompt.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("selected.json prompt does not match the compiled cleaner prompt.");
        if (!string.Equals(selected.GetProperty("cleanup_policy").GetProperty("sha256").GetString(),
                policy.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("selected.json cleanup policy does not match the loaded policy.");

        using JsonDocument lockDocument = JsonDocument.Parse(File.ReadAllText(lockPath));
        foreach (JsonElement locked in lockDocument.RootElement.GetProperty("runtimes").EnumerateArray())
        {
            string? id = locked.GetProperty("id").GetString();
            JsonElement? chosen = id switch
            {
                "transcribe-vulkan" => runtime.GetProperty("transcribe"),
                "llama-vulkan" => runtime.GetProperty("llama"),
                _ => null,
            };
            if (chosen is not JsonElement expected) continue;
            if (expected.TryGetProperty("version", out JsonElement version)
                && version.GetString() != locked.GetProperty("version").GetString())
                throw new InvalidDataException($"selected.json runtime version mismatch for {id}.");
            if (expected.TryGetProperty("commit", out JsonElement commit)
                && commit.GetString() != locked.GetProperty("commit").GetString())
                throw new InvalidDataException($"selected.json runtime commit mismatch for {id}.");
        }
    }

    private static ModelArtifact ReadModel(JsonElement element)
    {
        string id = Required(element, "candidate_id");
        string revision = Required(element, "revision");
        string precision = Required(element, "precision");
        string license = Required(element, "license");
        string runtime = Required(element, "runtime_id");
        if (revision.Length != 40 || revision.Any(c => !char.IsAsciiHexDigit(c)))
            throw new InvalidDataException($"Model {id} requires a full revision.");
        if (!element.TryGetProperty("files", out JsonElement filesElement) || filesElement.GetArrayLength() == 0)
            throw new InvalidDataException($"Model {id} requires verified files.");
        List<ModelFile> files = [];
        foreach (JsonElement file in filesElement.EnumerateArray())
        {
            string path = Required(file, "path");
            string sha = Required(file, "sha256");
            if (sha.Length != 64 || sha.Any(c => !char.IsAsciiHexDigit(c)))
                throw new InvalidDataException($"Model {id} has an invalid SHA256.");
            files.Add(new ModelFile(path, sha));
        }
        return new ModelArtifact(id, Required(element, "family"),
            Required(element, "source_checkpoint"), revision, files, precision, license, runtime,
            element.TryGetProperty("artifact_repository", out JsonElement repository) ? repository.GetString() : null);
    }

    private static string Required(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"selected.json requires {property}.");
        return value.GetString()!;
    }
}
