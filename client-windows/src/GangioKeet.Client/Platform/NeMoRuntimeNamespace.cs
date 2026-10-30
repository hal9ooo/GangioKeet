using System.IO;

namespace GangioKeet.Client.Platform;

/// <summary>
/// NeMo-Speech ships its own ggml/llama build. The app already owns a
/// different ggml (transcribe) and llama (LLamaSharp) runtime in the same
/// process, so NeMo's modules are moved to a private, equal-length namespace.
/// This is the same technique used for the LLamaSharp runtime (lgml).
/// </summary>
internal static class NeMoRuntimeNamespace
{
    internal const string Revision = "ngml-nlama-v1";

    private static readonly (byte[] From, byte[] To)[] Replacements =
    [
        ("ggml-"u8.ToArray(), "ngml-"u8.ToArray()),
        ("ggml.dll"u8.ToArray(), "ngml.dll"u8.ToArray()),
        ("ggml\0"u8.ToArray(), "ngml\0"u8.ToArray()),
        ("llama.dll"u8.ToArray(), "nlama.dll"u8.ToArray())
    ];

    internal static byte[] Patch(ReadOnlySpan<byte> data)
    {
        byte[] patched = data.ToArray();
        foreach ((byte[] from, byte[] to) in Replacements)
        {
            if (from.Length != to.Length)
                throw new InvalidOperationException("Namespace replacements must preserve length.");
            int index = 0;
            while (index <= patched.Length - from.Length)
            {
                int found = patched.AsSpan(index).IndexOf(from);
                if (found < 0) break;
                int at = index + found;
                to.CopyTo(patched, at);
                index = at + to.Length;
            }
        }
        return patched;
    }

    internal static void Prepare(string binDirectory)
    {
        foreach (string file in Directory.EnumerateFiles(binDirectory, "*.dll"))
        {
            string name = Path.GetFileName(file);
            if (!name.StartsWith("nemo_speech_", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("llama", StringComparison.OrdinalIgnoreCase)
                && !name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase))
                continue;
            byte[] original = File.ReadAllBytes(file);
            byte[] patched = Patch(original);
            if (!patched.AsSpan().SequenceEqual(original)) File.WriteAllBytes(file, patched);
        }

        foreach (string file in Directory.EnumerateFiles(binDirectory, "ggml*.dll").ToArray())
        {
            string name = Path.GetFileName(file);
            File.Move(file, Path.Combine(binDirectory, "ngml" + name[4..]), overwrite: true);
        }

        string llama = Path.Combine(binDirectory, "llama.dll");
        if (File.Exists(llama))
            File.Move(llama, Path.Combine(binDirectory, "nlama.dll"), overwrite: true);
    }
}
