using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace GangioKeet.Inference.Native;

public static class NativeLibraryResolver
{
    private static readonly object Gate = new();
    private static nint _transcribe;
    private static string? _directory;

    public static void VerifyTranscribeRuntime(string directory, string lockPath)
    {
        string root = Path.GetFullPath(directory);
        string prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(lockPath));
        JsonElement runtime = document.RootElement.GetProperty("runtimes").EnumerateArray()
            .Single(x => x.GetProperty("id").GetString() == "transcribe-vulkan");
        if (runtime.GetProperty("version").GetString() != "0.4.1")
            throw new InvalidDataException("Unexpected transcribe runtime version.");
        foreach (JsonElement item in runtime.GetProperty("files").EnumerateArray())
        {
            string name = item.GetProperty("path").GetString()!;
            if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains(':'))
                throw new InvalidDataException("Transcribe runtime files must be relative.");
            string path = Path.GetFullPath(name, root);
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Transcribe runtime file escapes the runtime root.");
            using FileStream file = File.OpenRead(path);
            if (Convert.ToHexStringLower(SHA256.HashData(file)) != item.GetProperty("sha256").GetString())
                throw new InvalidDataException("Transcribe runtime hash mismatch: " + name);
        }
    }

    public static void LoadTranscribe(string directory)
    {
        string absolute = Path.GetFullPath(directory);
        lock (Gate)
        {
            if (_transcribe != 0)
            {
                if (!string.Equals(_directory, absolute, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A different transcribe runtime is already loaded in this process.");
                return;
            }
            using JsonDocument contract = JsonDocument.Parse(File.ReadAllText(Path.Combine(absolute, "contract.json")));
            if (contract.RootElement.GetProperty("version").GetString() != "0.4.1")
                throw new InvalidDataException("The transcribe ABI requires the pinned 0.4.1 native package.");
            nint loaded = NativeLibrary.Load(Path.Combine(absolute, "transcribe.dll"),
                typeof(NativeLibraryResolver).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories);
            // The runtime and its registration callbacks are process-scoped. Do not
            // unload DLLs while ggml may still hold backend/function pointers.
            NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly,
                (name, _, _) => name == "gk-transcribe" ? _transcribe : 0);
            _transcribe = loaded;
            _directory = absolute;
        }
    }
}
