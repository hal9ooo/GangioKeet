using System.IO;
using GangioKeet.Inference.Native;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class NativeRuntimeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("GangioKeetNative-").FullName;

    [Fact]
    public void TranscribeRuntimeVerificationRejectsMismatchedHash()
    {
        File.WriteAllText(Path.Combine(_root, "transcribe.dll"), "abc");
        string lockPath = Path.Combine(_root, "runtime-lock.json");
        File.WriteAllText(lockPath, """
            {
              "runtimes": [
                {
                  "id": "transcribe-vulkan",
                  "version": "0.4.1",
                  "files": [
                    { "path": "transcribe.dll", "sha256": "deadbeef" }
                  ]
                }
              ]
            }
            """);

        Assert.Throws<InvalidDataException>(() =>
            NativeLibraryResolver.VerifyTranscribeRuntime(_root, lockPath));
    }

    [Fact]
    public void TranscribeRuntimeVerificationRejectsTraversal()
    {
        string outside = Path.Combine(_root, "..", "outside.dll");
        File.WriteAllText(outside, "abc");
        string lockPath = Path.Combine(_root, "runtime-lock.json");
        File.WriteAllText(lockPath, """
            {
              "runtimes": [
                {
                  "id": "transcribe-vulkan",
                  "version": "0.4.1",
                  "files": [
                    { "path": "../outside.dll", "sha256": "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad" }
                  ]
                }
              ]
            }
            """);

        Assert.Throws<InvalidDataException>(() =>
            NativeLibraryResolver.VerifyTranscribeRuntime(_root, lockPath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
