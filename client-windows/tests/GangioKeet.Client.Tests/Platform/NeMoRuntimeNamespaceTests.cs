using GangioKeet.Client.Platform;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

public sealed class NeMoRuntimeNamespaceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetNeMoNs-").FullName;

    [Fact]
    public void PatchMovesModuleNamesWithoutTouchingExportedFunctions()
    {
        byte[] original = "MZ\0ggml-base.dll\0ggml-vulkan.dll\0ggml.dll\0llama.dll\0ggml_backend_load\0"u8.ToArray();
        byte[] patched = NeMoRuntimeNamespace.Patch(original);

        Assert.Equal(original.Length, patched.Length);
        string text = System.Text.Encoding.ASCII.GetString(patched);
        Assert.Contains("ngml-base.dll", text);
        Assert.Contains("ngml-vulkan.dll", text);
        Assert.Contains("ngml.dll", text);
        Assert.Contains("nlama.dll", text);
        Assert.Contains("ggml_backend_load", text);
        Assert.DoesNotContain("ggml-vulkan.dll", text);
        Assert.DoesNotContain("llama.dll", text);
    }

    [Fact]
    public void PrepareRenamesRuntimeFilesAndRewritesTheirImports()
    {
        string dll = Path.Combine(_root, "ggml.dll");
        File.WriteAllBytes(dll, "MZ\0ggml-vulkan.dll\0"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_root, "ggml-vulkan.dll"), "MZ\0ggml-base.dll\0"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_root, "llama.dll"), "MZ\0ggml.dll\0"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_root, "nemo_speech_asr_c.dll"), "MZ\0llama.dll\0"u8.ToArray());
        File.WriteAllBytes(Path.Combine(_root, "msvcp140.dll"), "MZ\0ggml.dll\0"u8.ToArray());

        NeMoRuntimeNamespace.Prepare(_root);

        Assert.True(File.Exists(Path.Combine(_root, "ngml.dll")));
        Assert.True(File.Exists(Path.Combine(_root, "ngml-vulkan.dll")));
        Assert.True(File.Exists(Path.Combine(_root, "nlama.dll")));
        Assert.False(File.Exists(Path.Combine(_root, "ggml.dll")));
        Assert.False(File.Exists(Path.Combine(_root, "ggml-vulkan.dll")));
        Assert.False(File.Exists(Path.Combine(_root, "llama.dll")));
        Assert.Contains("ngml-vulkan.dll",
            System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(_root, "ngml.dll"))));
        Assert.Contains("nlama.dll",
            System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(_root, "nemo_speech_asr_c.dll"))));
        Assert.Equal("MZ\0ggml.dll\0",
            System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Path.Combine(_root, "msvcp140.dll"))));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
