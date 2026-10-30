using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class IntegrityManifestTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetIntegrity-").FullName;

    private string CatalogPath => Path.Combine(_root, "candidates.json");
    private string RuntimePath => Path.Combine(_root, "runtime-lock.json");
    private string PolicyPath => Path.Combine(_root, "cleanup.yaml");

    public IntegrityManifestTests()
    {
        File.WriteAllText(CatalogPath, "catalog");
        File.WriteAllText(RuntimePath, "runtime");
        File.WriteAllText(PolicyPath, "policy");
    }

    private static string Sha(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    private IntegrityManifest WriteManifest(string catalog, string runtime, string policy)
    {
        string path = Path.Combine(_root, "integrity.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schema_version = 1,
            catalog_sha256 = catalog,
            runtime_lock_sha256 = runtime,
            cleanup_policy_sha256 = policy
        }));
        return IntegrityManifestFile.Load(path);
    }

    [Fact]
    public async Task VerifyMatchingHashesSucceeds()
    {
        IntegrityManifest manifest = WriteManifest(Sha("catalog"), Sha("runtime"), Sha("policy"));
        await IntegrityManifestFile.VerifyAsync(manifest, CatalogPath, RuntimePath, PolicyPath, default);
    }

    [Fact]
    public async Task VerifyCatalogMismatchThrows()
    {
        IntegrityManifest manifest = WriteManifest(new string('0', 64), Sha("runtime"), Sha("policy"));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            IntegrityManifestFile.VerifyAsync(manifest, CatalogPath, RuntimePath, PolicyPath, default));
        Assert.Contains("catalog", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadMissingFieldThrows()
    {
        string path = Path.Combine(_root, "integrity.json");
        File.WriteAllText(path, """{"schema_version":1,"catalog_sha256":"abc"}""");
        Assert.Throws<InvalidDataException>(() => IntegrityManifestFile.Load(path));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    public void LoadNonObjectRootThrows(string json)
    {
        string path = Path.Combine(_root, "integrity.json");
        File.WriteAllText(path, json);
        Assert.Throws<InvalidDataException>(() => IntegrityManifestFile.Load(path));
    }

    [Fact]
    public async Task VerifyMissingFileThrows()
    {
        IntegrityManifest manifest = WriteManifest(Sha("catalog"), Sha("runtime"), Sha("policy"));
        File.Delete(PolicyPath);
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            IntegrityManifestFile.VerifyAsync(manifest, CatalogPath, RuntimePath, PolicyPath, default));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
