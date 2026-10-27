using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using LLama.Native;

namespace GangioKeet.Inference.Native;

public static class LlamaRuntime
{
    private static readonly object Gate = new();
    private static string? _directory;
    private static readonly List<nint> Libraries = [];

    public static void Initialize(string directory, string lockPath)
    {
        lock (Gate)
        {
            string root = Path.GetFullPath(directory);
            if (_directory is not null)
            {
                if (!string.Equals(root, _directory, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The process already owns a different llama runtime.");
                return;
            }
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(lockPath));
            JsonElement runtime = document.RootElement.GetProperty("runtimes").EnumerateArray()
                .Single(x => x.GetProperty("id").GetString() == "llama-vulkan");
            if (runtime.GetProperty("version").GetString() != "0.30.0") throw new InvalidDataException("Unexpected llama runtime version.");
            foreach (JsonElement item in runtime.GetProperty("files").EnumerateArray())
            {
                string name = item.GetProperty("path").GetString()!;
                if (Path.GetFileName(name) != name) throw new InvalidDataException("Native runtime files must be basenames.");
                using FileStream file = File.OpenRead(Path.Combine(root, name));
                if (Convert.ToHexStringLower(SHA256.HashData(file)) != item.GetProperty("sha256").GetString())
                    throw new InvalidDataException("Native runtime hash mismatch: " + name);
                if (name == "LLamaSharp.dll")
                {
                    using FileStream managed = File.OpenRead(typeof(NativeApi).Assembly.Location);
                    if (Convert.ToHexStringLower(SHA256.HashData(managed)) != item.GetProperty("sha256").GetString())
                        throw new InvalidDataException("LLamaSharp must use the pinned private ggml namespace; run the native bootstrap and rebuild.");
                }
            }
            // The namespaced dependencies are process-owned, just like transcribe's backends.
            foreach (string name in new[] { "lgml-base.dll", "lgml.dll", "lgml-cpu.dll", "lgml-vulkan.dll", "llama.dll" })
                Libraries.Add(NativeLibrary.Load(Path.Combine(root, name), Assembly.GetExecutingAssembly(),
                    DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories));
            NativeLibraryConfig.LLama.WithLibrary(Path.Combine(root, "llama.dll")).WithAutoFallback(false);
            _directory = root;
        }
    }
}
