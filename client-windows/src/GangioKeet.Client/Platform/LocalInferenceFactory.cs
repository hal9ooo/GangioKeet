using System.IO;
using System.Text.Json;
using GangioKeet.Client.Configuration;
using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
using GangioKeet.Inference.Stt;

namespace GangioKeet.Client.Platform;

public sealed record LocalInferenceSetup(IInferencePipeline Pipeline, string SttId, string CleanerId, string Backend);

/// <summary>
/// Costruisce la pipeline locale da <c>selected.json</c> e dagli artefatti
/// installati. Non crea client HTTP, monitor o processi figli.
/// </summary>
public static class LocalInferenceFactory
{
    public static LocalInferenceSetup Create(ClientConfig config, string? nativeDirectory = null,
        string? llamaDirectory = null, string? runtimeLockPath = null)
    {
        if (config.Inference.Mode != "local")
            throw new InvalidOperationException("Local inference requires inference.mode=local.");

        string selectionPath = Path.GetFullPath(config.Inference.ModelSelectionPath);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(selectionPath));
        JsonElement root = document.RootElement;
        if (root.GetProperty("schema_version").GetInt32() != 1)
            throw new InvalidDataException("selected.json requires schema_version 1.");
        ModelArtifact stt = ReadModel(root.GetProperty("stt"));
        ModelArtifact cleaner = ReadModel(root.GetProperty("cleaner"));

        string modelRoot = Path.GetDirectoryName(selectionPath)
            ?? throw new InvalidDataException("selected.json has no parent directory.");
        string install = AppContext.BaseDirectory;
        string transcribe = nativeDirectory ?? Environment.GetEnvironmentVariable("GANGIOKEET_TRANSCRIBE_DIR")
            ?? Path.Combine(install, "native", "transcribe");
        string llama = llamaDirectory ?? Environment.GetEnvironmentVariable("GANGIOKEET_LLAMA_DIR")
            ?? Path.Combine(install, "native", "llama");
        string lockPath = runtimeLockPath ?? Environment.GetEnvironmentVariable("GANGIOKEET_RUNTIME_LOCK")
            ?? Path.Combine(install, "native", "runtime-lock.json");
        string backend = config.Inference.Backend;

        CleanupPolicy policy = CleanupPolicy.Load(config.Inference.CleanupPolicyPath);
        TranscribeSttEngine sttEngine = new(stt, modelRoot, transcribe, backend);
        LlamaTextCleaner textCleaner = new(new LlamaGenerator(cleaner, modelRoot, llama, lockPath, backend), policy);
        return new(new InferencePipeline(sttEngine, textCleaner, policy, TimeProvider.System),
            stt.Id, cleaner.Id, backend);
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
