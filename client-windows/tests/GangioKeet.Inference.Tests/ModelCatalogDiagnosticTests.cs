using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class ModelCatalogDiagnosticTests
{
    private const string InstalledCatalogWithExtraFileField = """
        {"schema_version":1,"candidates":[
          {"id":"x","family":"f","display_name":"X","source_checkpoint":"o/r","revision":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","files":[{"path":"x/y.gguf","sha256":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"}],"precision":"F16","license":"MIT","runtime_id":"transcribe-vulkan","status":"downloaded","artifact_repository":"o/r"},
          {"id":"punct","family":"onnx","display_name":"P","source_checkpoint":"h/p","revision":"cccccccccccccccccccccccccccccccccccccccc","files":[{"path":"p/m.onnx","sha256":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd","remote_path":"onnx/m.onnx"}],"precision":"INT8","license":"Apache-2.0","runtime_id":"onnx-cpu","status":"available","artifact_repository":"h/p"}
        ]}
        """;

    [Fact]
    public void LoadsInstalledCatalogWithRemotePathOnAnOnnxFile()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, InstalledCatalogWithExtraFileField);
            ModelCatalog catalog = ModelCatalog.Load(path);
            Assert.Equal("downloaded", catalog.Get("x").Status);
        }
        finally { File.Delete(path); }
    }
}
