using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GangioKeet.Client.Platform;
using GangioKeet.Inference;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

public sealed class ModelProvisionerTests : IDisposable
{
    private readonly string _tempDir;

    public ModelProvisionerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GangioKeet_ModelProvisionerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    private static ModelArtifact Artifact(string id, string relativePath, byte[] payload, string repository)
    {
        string sha = Convert.ToHexStringLower(SHA256.HashData(payload));
        return new(id, "fake", "owner/model", "1234567890123456789012345678901234567890",
            [new ModelFile(relativePath, sha)], "F16", "Apache-2.0", "transcribe-vulkan", repository);
    }

    [Fact]
    public async Task EnsureModelsAsync_WithArtifacts_DownloadsOnlyListed()
    {
        byte[] payload = Encoding.UTF8.GetBytes("selected artifact payload");
        ModelArtifact wanted = Artifact("wanted", "w/model.gguf", payload, "test-org/wanted");
        ModelArtifact other = Artifact("other", "o/other.gguf", payload, "test-org/other");
        List<string> requested = [];

        var handler = new MockHttpMessageHandler(request =>
        {
            requested.Add(request.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });

        using var client = new HttpClient(handler);
        using var provisioner = new ModelProvisioner(client);

        await provisioner.EnsureModelsAsync([wanted], _tempDir);

        Assert.True(File.Exists(Path.Combine(_tempDir, "w/model.gguf")));
        Assert.False(File.Exists(Path.Combine(_tempDir, "o/other.gguf")));
        Assert.Single(requested);
        Assert.Contains("test-org/wanted", requested[0]);
    }

    [Fact]
    public async Task EnsureModelsAsync_WithArtifacts_EmptyListMakesNoRequest()
    {
        bool called = false;
        var handler = new MockHttpMessageHandler(_ =>
        {
            called = true;
            throw new InvalidOperationException("HTTP should not be called for an empty list");
        });

        using var client = new HttpClient(handler);
        using var provisioner = new ModelProvisioner(client);

        await provisioner.EnsureModelsAsync([], _tempDir);

        Assert.False(called);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch { /* best effort */ }
    }

    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private string CreateSelectedJson(string candidateId, string repo, string revision, string relativePath, string sha256)
    {
        var json = new
        {
            schema_version = 1,
            stt = new
            {
                candidate_id = candidateId,
                artifact_repository = repo,
                revision = revision,
                files = new[]
                {
                    new { path = relativePath, sha256 = sha256 }
                }
            }
        };

        string selectionPath = Path.Combine(_tempDir, "selected.json");
        File.WriteAllText(selectionPath, JsonSerializer.Serialize(json));
        return selectionPath;
    }

    [Fact]
    public async Task EnsureModelsAsync_WhenFileAlreadyExistsAndHashMatches_DoesNotDownload()
    {
        byte[] payload = Encoding.UTF8.GetBytes("valid model payload content");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));

        string relativePath = "models/model.gguf";
        string fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, payload);

        string selectionPath = CreateSelectedJson("test-model", "test-org/test-repo", "1234567890123456789012345678901234567890", relativePath, sha256);

        bool httpCalled = false;
        var handler = new MockHttpMessageHandler(_ =>
        {
            httpCalled = true;
            throw new InvalidOperationException("HTTP should not be called when file is valid");
        });

        using var client = new HttpClient(handler);
        using var provisioner = new ModelProvisioner(client);

        var progressList = new List<ModelProvisionProgress>();
        var progress = new Progress<ModelProvisionProgress>(progressList.Add);

        await provisioner.EnsureModelsAsync(selectionPath, modelRoot: _tempDir, progress);

        Assert.False(httpCalled);
        Assert.True(File.Exists(fullPath));
    }

    [Fact]
    public async Task EnsureModelsAsync_WhenFileMissing_DownloadsAndVerifiesHash()
    {
        byte[] payload = Encoding.UTF8.GetBytes("downloaded model bytes content");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));

        string relativePath = "test_sub/model.gguf";
        string selectionPath = CreateSelectedJson("test-model", "test-org/test-repo", "1234567890123456789012345678901234567890", relativePath, sha256);

        string? requestedUrl = null;
        var handler = new MockHttpMessageHandler(req =>
        {
            requestedUrl = req.RequestUri?.ToString();
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            return response;
        });

        using var client = new HttpClient(handler);
        using var provisioner = new ModelProvisioner(client);

        await provisioner.EnsureModelsAsync(selectionPath, modelRoot: _tempDir);

        string expectedUrl = "https://huggingface.co/test-org/test-repo/resolve/1234567890123456789012345678901234567890/model.gguf";
        Assert.Equal(expectedUrl, requestedUrl);

        string targetPath = Path.Combine(_tempDir, relativePath);
        Assert.True(File.Exists(targetPath));
        Assert.True(File.Exists(targetPath + ".verified"));
        byte[] writtenBytes = await File.ReadAllBytesAsync(targetPath);
        Assert.Equal(payload, writtenBytes);
    }

    [Fact]
    public async Task EnsureModelsAsync_WhenVerifiedFileChanges_RehashesAndReDownloads()
    {
        byte[] validPayload = Encoding.UTF8.GetBytes("valid model payload content");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(validPayload));
        string relativePath = "sub/model.gguf";
        string fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, validPayload);

        int downloads = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            downloads++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(validPayload) };
        });
        using var client = new HttpClient(handler);
        using var provisioner = new ModelProvisioner(client);
        string selectionPath = CreateSelectedJson("test-model", "test-org/test-repo",
            "1234567890123456789012345678901234567890", relativePath, sha256);

        await provisioner.EnsureModelsAsync(selectionPath, modelRoot: _tempDir);
        Assert.Equal(0, downloads);
        Assert.True(File.Exists(fullPath + ".verified"));

        await File.WriteAllBytesAsync(fullPath, Encoding.UTF8.GetBytes("tampered content goes here"));
        await provisioner.EnsureModelsAsync(selectionPath, modelRoot: _tempDir);

        Assert.Equal(1, downloads);
        Assert.Equal(validPayload, await File.ReadAllBytesAsync(fullPath));
    }

    [Fact]
    public async Task EnsureModelsAsync_WhenDownloadedHashMismatches_ThrowsAndCleansUp()
    {
        byte[] corruptedPayload = Encoding.UTF8.GetBytes("corrupted payload");
        string expectedSha256 = "0000000000000000000000000000000000000000000000000000000000000000";

        string relativePath = "sub/model.gguf";
        string selectionPath = CreateSelectedJson("test-model", "test-org/test-repo", "1234567890123456789012345678901234567890", relativePath, expectedSha256);

        var handler = new MockHttpMessageHandler(_ =>
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(corruptedPayload)
            };
        });

        using var client = new HttpClient(handler);
        using var provisioner = new ModelProvisioner(client);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() =>
            provisioner.EnsureModelsAsync(selectionPath, modelRoot: _tempDir));

        Assert.Contains("hash mismatch", ex.Message);
        string targetPath = Path.Combine(_tempDir, relativePath);
        Assert.False(File.Exists(targetPath));
        Assert.False(File.Exists(targetPath + ".downloading"));
    }

    [Fact]
    public async Task EnsureModelsAsync_WhenExistingFileHashMismatches_ReDownloads()
    {
        byte[] validPayload = Encoding.UTF8.GetBytes("valid content from server");
        string validSha256 = Convert.ToHexStringLower(SHA256.HashData(validPayload));

        string relativePath = "sub/model.gguf";
        string fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, Encoding.UTF8.GetBytes("corrupted old file"));

        string selectionPath = CreateSelectedJson("test-model", "test-org/test-repo", "1234567890123456789012345678901234567890", relativePath, validSha256);

        int downloadCalls = 0;
        var handler = new MockHttpMessageHandler(_ =>
        {
            downloadCalls++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(validPayload)
            };
        });

        using var client = new HttpClient(handler);
        using var provisioner = new ModelProvisioner(client);

        await provisioner.EnsureModelsAsync(selectionPath, modelRoot: _tempDir);

        Assert.Equal(1, downloadCalls);
        byte[] updatedBytes = await File.ReadAllBytesAsync(fullPath);
        Assert.Equal(validPayload, updatedBytes);
    }

    private sealed class Collector : IProgress<ModelProvisionProgress>
    {
        public List<ModelProvisionProgress> Items { get; } = [];
        public void Report(ModelProvisionProgress value) => Items.Add(value);
    }

    [Fact]
    public async Task EnsureModelsAsync_WhenPartialDownloadExists_ResumesWithRange()
    {
        byte[] payload = Encoding.UTF8.GetBytes("0123456789abcdefghijklmnopqrstuvwxyz");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        string relativePath = "sub/model.gguf";
        string fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        int split = 12;
        await File.WriteAllBytesAsync(fullPath + ".downloading", payload[..split]);
        string selectionPath = CreateSelectedJson("test-model", "test-org/test-repo",
            "1234567890123456789012345678901234567890", relativePath, sha256);

        string? range = null;
        int calls = 0;
        var handler = new MockHttpMessageHandler(request =>
        {
            calls++;
            range = request.Headers.Range?.ToString();
            byte[] remaining = payload[split..];
            HttpResponseMessage response = new(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(remaining)
            };
            response.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(split, payload.Length - 1, payload.Length);
            response.Content.Headers.ContentLength = remaining.Length;
            return response;
        });

        using var client = new HttpClient(handler);
        using var provisioner = new ModelProvisioner(client);
        Collector progress = new();
        await provisioner.EnsureModelsAsync(selectionPath, modelRoot: _tempDir, progress);

        Assert.Equal(1, calls);
        Assert.Equal($"bytes={split}-", range);
        Assert.Equal(payload, await File.ReadAllBytesAsync(fullPath));
        Assert.False(File.Exists(fullPath + ".downloading"));
        Assert.True(File.Exists(fullPath + ".verified"));
        Assert.Contains(progress.Items, item =>
            item.Status == ModelProvisionStatus.Downloading
            && item.BytesTransferred == payload.Length
            && item.TotalBytes == payload.Length);
    }

    [Fact]
    public async Task EnsureModelsAsync_WhenResumeRequestFails_KeepsPartialFile()
    {
        byte[] payload = Encoding.UTF8.GetBytes("0123456789abcdefghijklmnopqrstuvwxyz");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        string relativePath = "sub/model.gguf";
        string fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        byte[] partial = payload[..12];
        await File.WriteAllBytesAsync(fullPath + ".downloading", partial);
        string selectionPath = CreateSelectedJson("test-model", "test-org/test-repo",
            "1234567890123456789012345678901234567890", relativePath, sha256);

        var handler = new MockHttpMessageHandler(_ => throw new HttpRequestException("connection lost"));
        using var client = new HttpClient(handler);
        using var provisioner = new ModelProvisioner(client);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            provisioner.EnsureModelsAsync(selectionPath, modelRoot: _tempDir));

        Assert.Equal(partial, await File.ReadAllBytesAsync(fullPath + ".downloading"));
        Assert.False(File.Exists(fullPath));
    }

    [Fact]
    public async Task EnsureModelsAsync_WhenServerIgnoresRange_RestartsFromZero()
    {
        byte[] payload = Encoding.UTF8.GetBytes("0123456789abcdefghijklmnopqrstuvwxyz");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        string relativePath = "sub/model.gguf";
        string fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        int split = 12;
        await File.WriteAllBytesAsync(fullPath + ".downloading", payload[..split]);
        string selectionPath = CreateSelectedJson("test-model", "test-org/test-repo",
            "1234567890123456789012345678901234567890", relativePath, sha256);

        string? range = null;
        var handler = new MockHttpMessageHandler(request =>
        {
            range = request.Headers.Range?.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        });

        using var client = new HttpClient(handler);
        using var provisioner = new ModelProvisioner(client);
        await provisioner.EnsureModelsAsync(selectionPath, modelRoot: _tempDir);

        Assert.Equal($"bytes={split}-", range);
        Assert.Equal(payload, await File.ReadAllBytesAsync(fullPath));
        Assert.False(File.Exists(fullPath + ".downloading"));
    }

    [Fact]
    public async Task EnsureModelsAsync_WhenPartialIsAlreadyComplete_VerifiesAndMoves()
    {
        byte[] payload = Encoding.UTF8.GetBytes("0123456789abcdefghijklmnopqrstuvwxyz");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        string relativePath = "sub/model.gguf";
        string fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath + ".downloading", payload);
        string selectionPath = CreateSelectedJson("test-model", "test-org/test-repo",
            "1234567890123456789012345678901234567890", relativePath, sha256);

        string? range = null;
        var handler = new MockHttpMessageHandler(request =>
        {
            range = request.Headers.Range?.ToString();
            HttpResponseMessage response = new((HttpStatusCode)416)
            {
                Content = new ByteArrayContent([])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(payload.Length);
            return response;
        });

        using var client = new HttpClient(handler);
        using var provisioner = new ModelProvisioner(client);
        await provisioner.EnsureModelsAsync(selectionPath, modelRoot: _tempDir);

        Assert.Equal($"bytes={payload.Length}-", range);
        Assert.Equal(payload, await File.ReadAllBytesAsync(fullPath));
        Assert.False(File.Exists(fullPath + ".downloading"));
        Assert.True(File.Exists(fullPath + ".verified"));
    }

    [Fact]
    public async Task EnsureModelsAsync_WhenResumedHashMismatches_DeletesPartial()
    {
        byte[] payload = Encoding.UTF8.GetBytes("0123456789abcdefghijklmnopqrstuvwxyz");
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(payload));
        string relativePath = "sub/model.gguf";
        string fullPath = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        int split = 12;
        await File.WriteAllBytesAsync(fullPath + ".downloading", Encoding.UTF8.GetBytes("xxxxxxxxxxxx"));
        string selectionPath = CreateSelectedJson("test-model", "test-org/test-repo",
            "1234567890123456789012345678901234567890", relativePath, sha256);

        string? range = null;
        var handler = new MockHttpMessageHandler(request =>
        {
            range = request.Headers.Range?.ToString();
            byte[] remaining = payload[split..];
            HttpResponseMessage response = new(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(remaining)
            };
            response.Content.Headers.ContentRange =
                new ContentRangeHeaderValue(split, payload.Length - 1, payload.Length);
            response.Content.Headers.ContentLength = remaining.Length;
            return response;
        });

        using var client = new HttpClient(handler);
        using var provisioner = new ModelProvisioner(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provisioner.EnsureModelsAsync(selectionPath, modelRoot: _tempDir));

        Assert.Equal($"bytes={split}-", range);
        Assert.False(File.Exists(fullPath + ".downloading"));
        Assert.False(File.Exists(fullPath));
    }

    [Fact]
    public async Task EnsureModelsAsync_WhenPathEscapesRoot_Throws()
    {
        string relativePath = "../escaped.gguf";
        string selectionPath = CreateSelectedJson("test-model", "test-org/test-repo", "1234567890123456789012345678901234567890", relativePath, "abc");

        using var client = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        using var provisioner = new ModelProvisioner(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provisioner.EnsureModelsAsync(selectionPath, modelRoot: _tempDir));
    }
}

