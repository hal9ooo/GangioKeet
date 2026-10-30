using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using GangioKeet.Client.Platform;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

public sealed class NeMoSpeechRuntimeProvisionerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetNeMoRuntime-").FullName;

    private sealed class Handler(byte[] payload) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            ByteArrayContent content = new(payload);
            content.Headers.ContentLength = payload.Length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class Collector : IProgress<ModelProvisionProgress>
    {
        public List<ModelProvisionProgress> Items { get; } = [];
        public void Report(ModelProvisionProgress value) => Items.Add(value);
    }

    private static byte[] Archive()
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);
                using Stream entryStream = entry.Open();
                byte[] data = System.Text.Encoding.ASCII.GetBytes(text);
                entryStream.Write(data);
            }

            const string prefix = "nemo-speech-nightly-windows-x86_64-vulkan/";
            Add(prefix + "bin/nemo_speech_asr_c.dll", "MZ\0llama.dll\0");
            Add(prefix + "bin/ggml.dll", "MZ\0ggml-vulkan.dll\0");
            Add(prefix + "bin/ggml-vulkan.dll", "MZ\0ggml-base.dll\0");
            Add(prefix + "bin/llama.dll", "MZ\0ggml.dll\0");
            Add(prefix + "README.md", "not part of the runtime");
        }
        return stream.ToArray();
    }

    private (NeMoSpeechRuntimeProvisioner Provisioner, Handler Handler) Create(byte[]? payload = null,
        string? sha = null)
    {
        byte[] bytes = payload ?? Archive();
        Handler handler = new(bytes);
        string hash = sha ?? Convert.ToHexStringLower(SHA256.HashData(bytes));
        NeMoSpeechRuntimeProvisioner provisioner = new(new HttpClient(handler), _root,
            "https://example.invalid/nemo.zip", hash);
        return (provisioner, handler);
    }

    [Fact]
    public async Task DownloadsExtractsAndNamespacesTheRuntimeOnce()
    {
        (NeMoSpeechRuntimeProvisioner provisioner, Handler handler) = Create();
        Collector progress = new();

        string bin = await provisioner.EnsureAsync(NeMoSpeechRuntimeProvisioner.RuntimeId, progress, default);

        Assert.Equal(1, handler.Calls);
        Assert.True(File.Exists(Path.Combine(bin, "nemo_speech_asr_c.dll")));
        Assert.True(File.Exists(Path.Combine(bin, "ngml.dll")));
        Assert.True(File.Exists(Path.Combine(bin, "ngml-vulkan.dll")));
        Assert.True(File.Exists(Path.Combine(bin, "nlama.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "ggml.dll")));
        Assert.False(File.Exists(Path.Combine(bin, "llama.dll")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(bin)!, "README.md")));
        Assert.Contains("nlama.dll", System.Text.Encoding.ASCII.GetString(
            File.ReadAllBytes(Path.Combine(bin, "nemo_speech_asr_c.dll"))));
        Assert.Contains(progress.Items, item => item.Status == ModelProvisionStatus.Downloading);
        Assert.Contains(progress.Items, item => item.Status == ModelProvisionStatus.Completed);

        string again = await provisioner.EnsureAsync(NeMoSpeechRuntimeProvisioner.RuntimeId, null, default);
        Assert.Equal(bin, again);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RejectsAnArchiveWithTheWrongHash()
    {
        (NeMoSpeechRuntimeProvisioner provisioner, Handler handler) = Create(sha: new string('0', 64));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provisioner.EnsureAsync(NeMoSpeechRuntimeProvisioner.RuntimeId, null, default));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RejectsAnUnknownRuntime()
    {
        (NeMoSpeechRuntimeProvisioner provisioner, _) = Create();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provisioner.EnsureAsync("unknown", null, default));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
