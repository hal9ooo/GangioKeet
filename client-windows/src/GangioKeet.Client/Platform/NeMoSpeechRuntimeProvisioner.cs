using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;

namespace GangioKeet.Client.Platform;

public interface INativeRuntimeProvisioner
{
    Task<string> EnsureAsync(string runtimeId, IProgress<ModelProvisionProgress>? progress, CancellationToken ct);
}

public sealed class NeMoSpeechRuntimeProvisioner : INativeRuntimeProvisioner, IDisposable
{
    public const string RuntimeId = "nemo-speech-vulkan";
    public const string Version = "0.2.0+nightly.66d50d9";
    public const string ArchiveUrl =
        "https://github.com/NVIDIA/NeMo-Speech.cpp/releases/download/nightly/nemo-speech-nightly-windows-x86_64-vulkan.zip";
    public const string ArchiveSha256 = "fddd6be11c47e98855bac10106fee64dcdc255e596e232023c76b120a9597ab0";

    private readonly HttpClient _http;
    private readonly bool _disposeClient;
    private readonly string _root;
    private readonly string _archiveUrl;
    private readonly string _archiveSha256;

    public NeMoSpeechRuntimeProvisioner(HttpClient? httpClient = null, string? root = null,
        string? archiveUrl = null, string? archiveSha256 = null)
    {
        if (httpClient is null)
        {
            _http = new HttpClient();
            _disposeClient = true;
        }
        else
        {
            _http = httpClient;
            _disposeClient = false;
        }
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GangioKeet", "runtimes", "nemo-speech");
        _archiveUrl = archiveUrl ?? ArchiveUrl;
        _archiveSha256 = archiveSha256 ?? ArchiveSha256;
    }

    public async Task<string> EnsureAsync(string runtimeId,
        IProgress<ModelProvisionProgress>? progress, CancellationToken ct)
    {
        if (runtimeId != RuntimeId)
            throw new InvalidOperationException($"Unknown native runtime: {runtimeId}.");

        string runtimeRoot = Path.Combine(_root, _archiveSha256);
        string bin = Path.Combine(runtimeRoot, "bin");
        string marker = Path.Combine(runtimeRoot, ".ready");
        string dll = Path.Combine(bin, "nemo_speech_asr_c.dll");
        string ready = $"{Version}+{NeMoRuntimeNamespace.Revision}";
        if (File.Exists(marker) && File.Exists(dll) && File.ReadAllText(marker) == ready)
            return bin;

        Directory.CreateDirectory(runtimeRoot);
        string archive = Path.Combine(runtimeRoot, "nemo-speech.zip");
        if (!File.Exists(archive) || !HashMatches(archive))
        {
            await DownloadAsync(archive, progress, ct).ConfigureAwait(false);
        }
        if (!HashMatches(archive))
            throw new InvalidDataException("NeMo-Speech runtime archive hash mismatch.");

        using ZipArchive zip = ZipFile.OpenRead(archive);
        ZipArchiveEntry? probe = zip.Entries.FirstOrDefault(entry =>
            entry.FullName.EndsWith("/bin/nemo_speech_asr_c.dll", StringComparison.OrdinalIgnoreCase));
        if (probe is null)
            throw new InvalidDataException("NeMo-Speech runtime archive has no bin/nemo_speech_asr_c.dll.");
        string prefix = probe.FullName[..^"bin/nemo_speech_asr_c.dll".Length];
        Directory.CreateDirectory(bin);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(prefix + "bin/", StringComparison.OrdinalIgnoreCase)
                || entry.FullName.EndsWith("/", StringComparison.Ordinal))
                continue;
            string relative = entry.FullName[(prefix.Length + "bin/".Length)..]
                .Replace('/', Path.DirectorySeparatorChar);
            string destination = Path.GetFullPath(Path.Combine(bin, relative));
            if (!destination.StartsWith(Path.GetFullPath(bin) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Runtime archive entry escapes the runtime root.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
        if (!File.Exists(dll))
            throw new InvalidDataException("NeMo-Speech runtime extraction did not produce the C ABI DLL.");

        NeMoRuntimeNamespace.Prepare(bin);
        File.WriteAllText(marker, ready);
        return bin;
    }

    private async Task DownloadAsync(string destination, IProgress<ModelProvisionProgress>? progress,
        CancellationToken ct)
    {
        progress?.Report(new ModelProvisionProgress("nemo-speech-runtime", "runtime.zip",
            ModelProvisionStatus.Checking, 0, null));
        using HttpResponseMessage response = await _http.GetAsync(_archiveUrl,
            HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        string temp = destination + ".downloading";
        try
        {
            await using Stream source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            long done = 0;
            await using (FileStream target = new(temp, FileMode.Create, FileAccess.Write, FileShare.None,
                1024 * 1024, useAsync: true))
            {
                byte[] buffer = new byte[256 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    progress?.Report(new ModelProvisionProgress("nemo-speech-runtime", "runtime.zip",
                        ModelProvisionStatus.Downloading, done, total));
                }
                await target.FlushAsync(ct).ConfigureAwait(false);
            }
            File.Move(temp, destination, overwrite: true);
            progress?.Report(new ModelProvisionProgress("nemo-speech-runtime", "runtime.zip",
                ModelProvisionStatus.Completed, done, total));
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private bool HashMatches(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexStringLower(SHA256.HashData(stream)) == _archiveSha256;
        }
        catch (IOException) { return false; }
    }

    public void Dispose()
    {
        if (_disposeClient) _http.Dispose();
    }
}
