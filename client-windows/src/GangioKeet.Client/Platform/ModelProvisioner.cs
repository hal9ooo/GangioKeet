using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using GangioKeet.Inference;

namespace GangioKeet.Client.Platform;

public enum ModelProvisionStatus
{
    Checking,
    Downloading,
    Completed
}

public sealed record ModelProvisionProgress(
    string ModelId,
    string RelativePath,
    ModelProvisionStatus Status,
    long BytesTransferred,
    long? TotalBytes
);

public interface IModelProvisioner
{
    Task EnsureModelsAsync(
        string selectionPath,
        string? modelRoot = null,
        IProgress<ModelProvisionProgress>? progress = null,
        CancellationToken ct = default);

    Task EnsureModelsAsync(
        IReadOnlyList<ModelArtifact> artifacts,
        string modelRoot,
        IProgress<ModelProvisionProgress>? progress = null,
        CancellationToken ct = default);
}

public sealed class ModelProvisioner : IModelProvisioner, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _disposeClient;

    public ModelProvisioner(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _httpClient = new HttpClient();
            _disposeClient = true;
        }
        else
        {
            _httpClient = httpClient;
            _disposeClient = false;
        }
    }

    public async Task EnsureModelsAsync(
        string selectionPath,
        string? modelRoot = null,
        IProgress<ModelProvisionProgress>? progress = null,
        CancellationToken ct = default)
    {
        string fullSelectionPath = Path.GetFullPath(selectionPath);
        if (!File.Exists(fullSelectionPath))
        {
            throw new FileNotFoundException($"Model selection file not found: {fullSelectionPath}");
        }

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(fullSelectionPath, ct).ConfigureAwait(false));
        JsonElement root = document.RootElement;
        string rootDir = modelRoot ?? Path.GetDirectoryName(fullSelectionPath) ?? AppContext.BaseDirectory;

        if (root.TryGetProperty("stt", out JsonElement sttElement))
        {
            await EnsureModelArtifactAsync(sttElement, rootDir, progress, ct).ConfigureAwait(false);
        }

        if (root.TryGetProperty("cleaner", out JsonElement cleanerElement))
        {
            await EnsureModelArtifactAsync(cleanerElement, rootDir, progress, ct).ConfigureAwait(false);
        }
    }

    public async Task EnsureModelsAsync(
        IReadOnlyList<ModelArtifact> artifacts,
        string modelRoot,
        IProgress<ModelProvisionProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(artifacts);
        foreach (ModelArtifact artifact in artifacts)
        {
            ct.ThrowIfCancellationRequested();
            await EnsureModelArtifactAsync(artifact.Id, artifact.ArtifactRepository, artifact.Revision,
                artifact.Files, modelRoot, progress, ct).ConfigureAwait(false);
        }
    }

    private async Task EnsureModelArtifactAsync(
        JsonElement modelElement,
        string rootDir,
        IProgress<ModelProvisionProgress>? progress,
        CancellationToken ct)
    {
        string candidateId = modelElement.TryGetProperty("candidate_id", out JsonElement idElem) ? idElem.GetString() ?? "unknown" : "unknown";
        string? repo = modelElement.TryGetProperty("artifact_repository", out JsonElement repoElem) ? repoElem.GetString() : null;
        string? revision = modelElement.TryGetProperty("revision", out JsonElement revElem) ? revElem.GetString() : null;

        if (!modelElement.TryGetProperty("files", out JsonElement filesElem) || filesElem.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        List<ModelFile> files = [];
        foreach (JsonElement fileElem in filesElem.EnumerateArray())
        {
            string? relativePath = fileElem.TryGetProperty("path", out JsonElement pElem) ? pElem.GetString() : null;
            string? expectedSha256 = fileElem.TryGetProperty("sha256", out JsonElement shaElem) ? shaElem.GetString() : null;
            if (!string.IsNullOrWhiteSpace(relativePath) && !string.IsNullOrWhiteSpace(expectedSha256))
            {
                files.Add(new ModelFile(relativePath, expectedSha256));
            }
        }

        await EnsureModelArtifactAsync(candidateId, repo, revision, files, rootDir, progress, ct).ConfigureAwait(false);
    }

    private async Task EnsureModelArtifactAsync(
        string candidateId,
        string? repo,
        string? revision,
        IReadOnlyList<ModelFile> files,
        string rootDir,
        IProgress<ModelProvisionProgress>? progress,
        CancellationToken ct)
    {
        foreach (ModelFile file in files)
        {
            ct.ThrowIfCancellationRequested();
            string relativePath = file.Path;
            string expectedSha256 = file.Sha256;
            string fullPath = ResolveInsideRoot(rootDir, relativePath);

            // 1. Check if file already exists and hash matches
            if (File.Exists(fullPath))
            {
                long existingLength = new FileInfo(fullPath).Length;
                progress?.Report(new ModelProvisionProgress(candidateId, relativePath, ModelProvisionStatus.Checking, 0, existingLength));
                bool valid = await CheckHashAsync(fullPath, expectedSha256, ct).ConfigureAwait(false);
                if (valid)
                {
                    progress?.Report(new ModelProvisionProgress(candidateId, relativePath, ModelProvisionStatus.Completed, existingLength, existingLength));
                    continue;
                }
            }

            // 2. Download model file
            if (string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(revision))
            {
                throw new InvalidOperationException($"Cannot download model '{candidateId}': missing repository or revision in selected.json.");
            }

            string fileName = Path.GetFileName(relativePath);
            string downloadUrl = $"https://huggingface.co/{repo}/resolve/{revision}/{fileName}";

            await DownloadAndVerifyAsync(downloadUrl, fullPath, candidateId, relativePath, expectedSha256, progress, ct).ConfigureAwait(false);
        }
    }

    private async Task DownloadAndVerifyAsync(
        string url,
        string destinationPath,
        string modelId,
        string relativePath,
        string expectedSha256,
        IProgress<ModelProvisionProgress>? progress,
        CancellationToken ct)
    {
        string? destinationDir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDir))
        {
            Directory.CreateDirectory(destinationDir);
        }

        string tempPath = destinationPath + ".downloading";
        if (File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { /* ignore */ }
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            long bytesDownloaded = 0;

            await using (Stream contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (FileStream fileStream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
            using (IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[256 * 1024];
                int read;
                while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    hasher.AppendData(buffer.AsSpan(0, read));
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    bytesDownloaded += read;

                    progress?.Report(new ModelProvisionProgress(modelId, relativePath, ModelProvisionStatus.Downloading, bytesDownloaded, totalBytes));
                }

                await fileStream.FlushAsync(ct).ConfigureAwait(false);

                string actualHash = Convert.ToHexStringLower(hasher.GetHashAndReset());
                if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"Model file '{relativePath}' hash mismatch after download. Expected {expectedSha256}, got {actualHash}.");
                }
            }

            File.Move(tempPath, destinationPath, overwrite: true);
            progress?.Report(new ModelProvisionProgress(modelId, relativePath, ModelProvisionStatus.Completed, bytesDownloaded, totalBytes));
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore */ }
            }
        }
    }

    private static async Task<bool> CheckHashAsync(string path, string expectedSha256, CancellationToken ct)
    {
        try
        {
            await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
            byte[] hashBytes = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
            string actualHash = Convert.ToHexStringLower(hashBytes);
            return string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveInsideRoot(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
        {
            throw new InvalidDataException("Model file paths must be relative to the model root.");
        }

        string fullRoot = Path.GetFullPath(root);
        string path = Path.GetFullPath(relative, fullRoot);
        string prefix = Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Model file escapes root: {relative}");
        }

        return path;
    }

    public void Dispose()
    {
        if (_disposeClient)
        {
            _httpClient.Dispose();
        }
    }
}

