using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GangioKeet.Client.Platform;
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
        byte[] writtenBytes = await File.ReadAllBytesAsync(targetPath);
        Assert.Equal(payload, writtenBytes);
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

