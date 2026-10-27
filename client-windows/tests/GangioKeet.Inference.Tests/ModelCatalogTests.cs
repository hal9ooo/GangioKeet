using System.Text.Json;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class ModelCatalogTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetCatalog-").FullName;
    private const string Digest = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    private string Catalog(string path = "weights.gguf", string sha = Digest, string revision = Revision)
    {
        File.WriteAllText(Path.Combine(_root, "weights.gguf"), "abc");
        string catalogPath = Path.Combine(_root, "catalog.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(new
        {
            schema_version = 1,
            candidates = new[] { new { id = "example", family = "qwen", source_checkpoint = "owner/model",
                revision, files = new[] { new { path, sha256 = sha } }, precision = "F16",
                license = "Apache-2.0", runtime_id = "example-runtime" } }
        }));
        return catalogPath;
    }

    [Fact]
    public async Task VerifiesPinnedLocalBytes()
    {
        ModelCatalog catalog = ModelCatalog.Load(Catalog());
        await catalog.VerifyAsync(_root, CancellationToken.None);
        Assert.Equal("owner/model", catalog.Get("example").SourceCheckpoint);
    }

    [Fact]
    public async Task CorruptedModelIsRejectedBeforeInference()
    {
        ModelCatalog catalog = ModelCatalog.Load(Catalog());
        File.WriteAllText(Path.Combine(_root, "weights.gguf"), "corrupted");
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.VerifyAsync(_root, CancellationToken.None));
    }

    [Theory]
    [InlineData("../outside.gguf")]
    [InlineData("C:/outside.gguf")]
    public async Task ModelPathCannotEscapeRoot(string path)
    {
        ModelCatalog catalog = ModelCatalog.Load(Catalog(path: path));
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.VerifyAsync(_root, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("main")]
    public async Task UnpinnedRevisionCannotBeBenchmarked(string revision)
    {
        ModelCatalog catalog = ModelCatalog.Load(Catalog(revision: revision));
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.VerifyAsync(_root, CancellationToken.None));
    }

    [Fact]
    public void UnknownModelFailsExplicitly()
    {
        ModelCatalog catalog = ModelCatalog.Load(Catalog());
        Assert.Throws<KeyNotFoundException>(() => { catalog.Get("unknown"); });
    }

    [Fact]
    public async Task CancellationStopsVerification()
    {
        ModelCatalog catalog = ModelCatalog.Load(Catalog());
        using CancellationTokenSource cts = new();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.VerifyAsync(_root, cts.Token));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
