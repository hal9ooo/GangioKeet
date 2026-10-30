using System.Text.Json;

namespace GangioKeet.Inference;

public sealed record ModelSelection(int SchemaVersion, string SttId, string? CleanerId, bool RefinementEnabled);

public sealed record ResolvedModelSelection(ModelSelection Selection, ModelArtifact Stt, ModelArtifact? Cleaner);

public static class ModelSelectionFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static ModelSelection Load(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("schema_version", out JsonElement schemaElement)
                || !schemaElement.TryGetInt32(out int schemaVersion))
                throw new InvalidDataException("Model selection requires schema_version.");

            if (schemaVersion == 1)
            {
                string sttId = RequiredCandidateId(root, "stt");
                string? cleanerId = OptionalCandidateId(root, "cleaner");
                return new(1, sttId, cleanerId, cleanerId is not null);
            }

            if (schemaVersion == 2)
            {
                string sttId = RequiredString(root, "stt_id");
                string? cleanerId = OptionalString(root, "cleaner_id");
                if (!root.TryGetProperty("refinement_enabled", out JsonElement refinementElement)
                    || refinementElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Model selection requires refinement_enabled.");
                bool refinementEnabled = refinementElement.GetBoolean();
                if (refinementEnabled && string.IsNullOrWhiteSpace(cleanerId))
                    throw new InvalidDataException("Refinement requires a cleaner_id.");
                return new(2, sttId, cleanerId, refinementEnabled);
            }

            throw new InvalidDataException($"Unsupported model selection schema_version: {schemaVersion}.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid model selection JSON.", error);
        }
    }

    public static void Save(string path, ModelSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (string.IsNullOrWhiteSpace(selection.SttId))
            throw new InvalidDataException("Model selection requires stt_id.");
        if (selection.RefinementEnabled && string.IsNullOrWhiteSpace(selection.CleanerId))
            throw new InvalidDataException("Refinement requires a cleaner_id.");

        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new
            {
                schema_version = 2,
                stt_id = selection.SttId,
                cleaner_id = selection.CleanerId,
                refinement_enabled = selection.RefinementEnabled
            }, Options));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static void Migrate(string legacyPath, string v2Path)
    {
        ModelSelection legacy = Load(legacyPath);
        Save(v2Path, new(2, legacy.SttId, legacy.CleanerId, legacy.RefinementEnabled));
    }

    private static string RequiredCandidateId(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement element) || element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("candidate_id", out JsonElement idElement)
            || idElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idElement.GetString()))
            throw new InvalidDataException($"Model selection requires {property}.candidate_id.");
        return idElement.GetString()!;
    }

    private static string? OptionalCandidateId(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("candidate_id", out JsonElement idElement)
            || idElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(idElement.GetString()))
            throw new InvalidDataException($"Model selection requires {property}.candidate_id.");
        return idElement.GetString();
    }

    private static string RequiredString(JsonElement root, string property)
    {
        string? value = OptionalString(root, property);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Model selection requires {property}.");
        return value;
    }

    private static string? OptionalString(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
            return null;
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            throw new InvalidDataException($"Model selection requires {property}.");
        return element.GetString();
    }
}

public static class ModelSelectionResolver
{
    public static ResolvedModelSelection Resolve(ModelSelection selection, ModelCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(catalog);
        ModelArtifact stt = Required(catalog, selection.SttId, "stt_id");
        if (stt.RuntimeId != "transcribe-vulkan")
            throw new InvalidDataException(
                $"stt_id '{selection.SttId}' must use transcribe-vulkan, got '{stt.RuntimeId}'.");

        ModelArtifact? cleaner = null;
        if (!string.IsNullOrWhiteSpace(selection.CleanerId))
        {
            cleaner = Required(catalog, selection.CleanerId, "cleaner_id");
            if (cleaner.RuntimeId != "llama-vulkan")
                throw new InvalidDataException(
                    $"cleaner_id '{selection.CleanerId}' must use llama-vulkan, got '{cleaner.RuntimeId}'.");
        }
        if (selection.RefinementEnabled && cleaner is null)
            throw new InvalidDataException("Refinement requires a cleaner_id.");
        return new(selection, stt, cleaner);
    }

    private static ModelArtifact Required(ModelCatalog catalog, string id, string field)
    {
        try { return catalog.Get(id); }
        catch (KeyNotFoundException error)
        {
            throw new InvalidDataException($"Unknown {field}: {id}.", error);
        }
    }
}
