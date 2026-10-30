using System.Security.Cryptography;
using System.Text.Json;

namespace GangioKeet.Inference;

public sealed record IntegrityManifest(string CatalogSha256, string RuntimeLockSha256, string CleanupPolicySha256);

public static class IntegrityManifestFile
{
    public static IntegrityManifest Load(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("schema_version", out JsonElement schema)
                || !schema.TryGetInt32(out int version) || version != 1)
                throw new InvalidDataException("Integrity manifest requires schema_version 1.");
            return new(
                RequiredHash(root, "catalog_sha256"),
                RequiredHash(root, "runtime_lock_sha256"),
                RequiredHash(root, "cleanup_policy_sha256"));
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid integrity manifest JSON.", error);
        }
    }

    public static async Task VerifyAsync(IntegrityManifest manifest, string catalogPath,
        string runtimeLockPath, string cleanupPolicyPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        await VerifyOneAsync("catalog", manifest.CatalogSha256, catalogPath, ct).ConfigureAwait(false);
        await VerifyOneAsync("runtime lock", manifest.RuntimeLockSha256, runtimeLockPath, ct).ConfigureAwait(false);
        await VerifyOneAsync("cleanup policy", manifest.CleanupPolicySha256, cleanupPolicyPath, ct).ConfigureAwait(false);
    }

    public static void Verify(IntegrityManifest manifest, string catalogPath,
        string runtimeLockPath, string cleanupPolicyPath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        VerifyOne("catalog", manifest.CatalogSha256, catalogPath);
        VerifyOne("runtime lock", manifest.RuntimeLockSha256, runtimeLockPath);
        VerifyOne("cleanup policy", manifest.CleanupPolicySha256, cleanupPolicyPath);
    }

    private static void VerifyOne(string label, string expected, string path)
    {
        using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Integrity mismatch for {label}: expected {expected}, got {actual}.");
    }

    private static async Task VerifyOneAsync(string label, string expected, string path, CancellationToken ct)
    {
        await using FileStream stream = File.OpenRead(path);
        string actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Integrity mismatch for {label}: expected {expected}, got {actual}.");
    }

    private static string RequiredHash(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement element)
            || element.ValueKind != JsonValueKind.String
            || element.GetString() is not { Length: 64 } value
            || !value.All(char.IsAsciiHexDigit))
            throw new InvalidDataException($"Integrity manifest requires {property} as a SHA256 hex string.");
        return value;
    }
}
