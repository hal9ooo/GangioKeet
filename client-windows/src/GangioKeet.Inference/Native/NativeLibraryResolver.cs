using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace GangioKeet.Inference.Native;

public static class NativeLibraryResolver
{
    private static readonly object Gate = new();
    private static nint _transcribe;
    private static string? _directory;

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
