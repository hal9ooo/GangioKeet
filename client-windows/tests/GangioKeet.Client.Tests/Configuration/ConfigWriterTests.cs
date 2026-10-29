using GangioKeet.Client.Configuration;
using Xunit;

namespace GangioKeet.Client.Tests.Configuration;

public sealed class ConfigWriterTests
{
    [Fact]
    public void ReplacesExistingValue_PreservingCommentsAndOrder()
    {
        string path = TempFile("""
# commento iniziale
audio:
  input_device: null   # microfono
  sample_rate: 16000
server:
  host: "192.168.1.62"
""");

        Assert.True(ConfigWriter.SetAudioInputDevice(path, "mic-123"));

        string text = File.ReadAllText(path);
        Assert.Contains("# commento iniziale", text);
        Assert.Contains("input_device: \"mic-123\"   # microfono", text);
        Assert.Contains("sample_rate: 16000", text);
        Assert.Contains("host: \"192.168.1.62\"", text);
    }

    [Fact]
    public void InsertsTheKey_WhenTheAudioSectionExists()
    {
        string path = TempFile("audio:\n  sample_rate: 16000\n");

        Assert.True(ConfigWriter.SetAudioInputDevice(path, "mic-123"));

        Assert.Contains("  input_device: \"mic-123\"", File.ReadAllText(path));
    }

    [Fact]
    public void AppendsTheSection_WhenMissing()
    {
        string path = TempFile("ui:\n  pill_position: \"cursor\"\n");

        Assert.True(ConfigWriter.SetAudioInputDevice(path, "mic-123"));

        string text = File.ReadAllText(path);
        Assert.Contains("audio:", text);
        Assert.Contains("input_device: \"mic-123\"", text);
    }

    [Fact]
    public void Null_WritesAnUnquotedNull()
    {
        string path = TempFile("audio:\n  input_device: \"mic-123\"\n");

        Assert.True(ConfigWriter.SetAudioInputDevice(path, null));

        Assert.Contains("input_device: null", File.ReadAllText(path));
    }

    [Fact]
    public void MissingDirectory_ReturnsFalse()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "client_config.yaml");

        Assert.False(ConfigWriter.SetAudioInputDevice(path, "mic-123"));
    }

    [Fact]
    public void InsertsTheKey_UsingTheExistingFourSpaceIndentation()
    {
        string path = TempFile("audio:\n    sample_rate: 16000\n    channels: 1\n");

        Assert.True(ConfigWriter.SetAudioInputDevice(path, "mic-123"));

        string text = File.ReadAllText(path);
        Assert.Contains("    input_device: \"mic-123\"", text);
    }

    [Fact]
    public void SetModelSelectionPath_WritesUnderInferenceSection()
    {
        string path = TempFile("inference:\n  mode: local\n");
        Assert.True(ConfigWriter.SetModelSelectionPath(path, "models/selected-parakeet-qwen.json"));
        string text = File.ReadAllText(path);
        Assert.Contains("model_selection_path: \"models/selected-parakeet-qwen.json\"", text);
    }

    [Fact]
    public void AudioSectionWithInlineComment_IsStillRecognized()
    {
        string path = TempFile("audio:   # sezione audio\n  input_device: null\n");

        Assert.True(ConfigWriter.SetAudioInputDevice(path, "mic-123"));

        string text = File.ReadAllText(path);
        Assert.Contains("audio:   # sezione audio", text);
        Assert.Contains("input_device: \"mic-123\"", text);
    }

    private static string TempFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gangio-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, content);
        return path;
    }
}
