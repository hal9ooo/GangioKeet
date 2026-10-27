using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GangioKeet.Inference;

public sealed class ModelCatalog
{
    private sealed record Document(int SchemaVersion, ModelArtifact[] Candidates);
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public IReadOnlyList<ModelArtifact> Candidates { get; }

    private ModelCatalog(ModelArtifact[] candidates) => Candidates = Array.AsReadOnly(candidates);

    public static ModelCatalog Load(string path)
    {
        Document document;
        try
        {
            document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException("Model catalog is empty.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid model catalog JSON.", error);
        }

        if (document.SchemaVersion != 1 || document.Candidates is not { Length: > 0 })
            throw new InvalidDataException("Model catalog requires schema_version 1 and candidates.");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (ModelArtifact candidate in document.Candidates)
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.Id)
                || string.IsNullOrWhiteSpace(candidate.SourceCheckpoint)
                || string.IsNullOrWhiteSpace(candidate.Family) || !ids.Add(candidate.Id))
                throw new InvalidDataException("Model identities must be nonempty and unique.");
        }
        return new ModelCatalog(document.Candidates);
    }

    public ModelArtifact Get(string id) => Candidates.FirstOrDefault(candidate => candidate.Id == id)
        ?? throw new KeyNotFoundException($"Unknown model candidate: {id}");

    public static Task VerifyArtifactAsync(ModelArtifact candidate, string modelRoot, CancellationToken ct)
        => new ModelCatalog([candidate]).VerifyAsync(modelRoot, ct);

    public async Task VerifyAsync(string modelRoot, CancellationToken ct)
    {
        string root = Path.GetFullPath(modelRoot);
        foreach (ModelArtifact candidate in Candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsHex(candidate.Revision, 40) || candidate.Files is not { Count: > 0 }
                || string.IsNullOrWhiteSpace(candidate.Precision)
                || string.IsNullOrWhiteSpace(candidate.RuntimeId)
                || string.IsNullOrWhiteSpace(candidate.License))
                throw new InvalidDataException($"Model {candidate.Id} has no pinned revision, files or runtime metadata.");

            foreach (ModelFile file in candidate.Files)
            {
                if (file is null || !IsHex(file.Sha256, 64))
                    throw new InvalidDataException($"Model {candidate.Id} requires a SHA256 for each file.");
                string path = ResolveInsideRoot(root, file.Path);
                await using FileStream stream = File.OpenRead(path);
                string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
                if (!string.Equals(actual, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Model {candidate.Id} file hash mismatch: {file.Path}");
            }
        }
    }

    private static bool IsHex(string? value, int length) => value?.Length == length
        && value.All(char.IsAsciiHexDigit);

    private static string ResolveInsideRoot(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("Model file paths must be relative to the model root.");
        string path = Path.GetFullPath(relative, root);
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Model file escapes root: {relative}");
        // Refuse links inside the declared root: they could retarget verified bytes
        // to an unrelated location. The caller explicitly chooses the root itself.
        string current = root;
        foreach (string part in Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Model file path contains a link: {relative}");
        }
        return path;
    }
}
