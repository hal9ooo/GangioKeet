using System.Text.Json;
using GangioKeet.Client.Protocol;
using Xunit;

namespace GangioKeet.Client.Tests.Protocol;

public sealed class ProtocolJsonTests
{
    private const string LegacyResultJson =
        """{"type":"result","raw_text":"raw speech","final_text":"RAW SPEECH.","fast_path":true,"metrics":{"stt_ms":12.5,"llm_ms":0,"total_ms":12.5}}""";

    [Fact]
    public void SerializeStart_WritesExactSnakeCaseMembers()
    {
        string json = ProtocolJson.SerializeStart(new StartMessage("demo", "Terminal Window", "chat"));

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        Assert.Equal(JsonValueKind.Object, root.ValueKind);

        HashSet<string> members = [.. root.EnumerateObject().Select(property => property.Name)];
        Assert.Equal(new HashSet<string>
        {
            "type",
            "app_name",
            "window_title",
            "profile",
            "selected_text",
            "bypass_llm",
        }, members);

        Assert.Equal("start", root.GetProperty("type").GetString());
        Assert.Equal("demo", root.GetProperty("app_name").GetString());
        Assert.Equal("Terminal Window", root.GetProperty("window_title").GetString());
        Assert.Equal("chat", root.GetProperty("profile").GetString());
        Assert.Equal("", root.GetProperty("selected_text").GetString());
        Assert.False(root.GetProperty("bypass_llm").GetBoolean());
    }

    [Fact]
    public void ParseServerFrame_ParsesLegacyResultWithoutWarnings()
    {
        ServerFrame frame = ProtocolJson.ParseServerFrame(LegacyResultJson);

        ResultFrame resultFrame = Assert.IsType<ResultFrame>(frame);
        Assert.Equal("raw speech", resultFrame.Result.RawText);
        Assert.Equal("RAW SPEECH.", resultFrame.Result.FinalText);
        Assert.True(resultFrame.Result.FastPath);
        Assert.NotNull(resultFrame.Result.Warnings);
        Assert.Empty(resultFrame.Result.Warnings);
        Assert.Equal(12.5, resultFrame.Result.Metrics.SttMs);
        Assert.Equal(0, resultFrame.Result.Metrics.LlmMs);
        Assert.Equal(12.5, resultFrame.Result.Metrics.TotalMs);
    }

    [Fact]
    public void ParseServerFrame_ParsesResultWithWarnings()
    {
        string json = """
            {"type":"result","raw_text":"raw","final_text":"final","fast_path":false,
             "metrics":{"stt_ms":100.25,"llm_ms":17.75,"total_ms":118},
             "warnings":["short_input","recording_noisy"]}
            """;

        ServerFrame frame = ProtocolJson.ParseServerFrame(json);

        ResultFrame resultFrame = Assert.IsType<ResultFrame>(frame);
        Assert.False(resultFrame.Result.FastPath);
        Assert.Equal(100.25, resultFrame.Result.Metrics.SttMs);
        Assert.Equal(17.75, resultFrame.Result.Metrics.LlmMs);
        Assert.Equal(118, resultFrame.Result.Metrics.TotalMs);
        Assert.Equal(["short_input", "recording_noisy"], resultFrame.Result.Warnings);
    }

    [Fact]
    public void ParseServerFrame_IgnoresAdditiveMembers()
    {
        string json = """
            {"type":"result","raw_text":"raw","final_text":"final","fast_path":true,
             "metrics":{"stt_ms":1,"llm_ms":0,"total_ms":1,"gpu_ms":3.3},
             "warnings":[],"model":"parakeet-v3","session_id":"abc"}
            """;

        ServerFrame frame = ProtocolJson.ParseServerFrame(json);

        ResultFrame resultFrame = Assert.IsType<ResultFrame>(frame);
        Assert.Equal("raw", resultFrame.Result.RawText);
        Assert.True(resultFrame.Result.FastPath);
        Assert.Empty(resultFrame.Result.Warnings);
    }

    [Fact]
    public void ParseServerFrame_ParsesErrorFrameWithUnknownCode()
    {
        string json = """{"type":"error","code":"llm_quota_exhausted","message":"Quota exhausted for today."}""";

        ServerFrame frame = ProtocolJson.ParseServerFrame(json);

        ErrorFrame errorFrame = Assert.IsType<ErrorFrame>(frame);
        Assert.Equal("llm_quota_exhausted", errorFrame.Code);
        Assert.Equal("Quota exhausted for today.", errorFrame.Message);
        Assert.False(errorFrame.Retryable);
    }

    [Fact]
    public void ParseServerFrame_ParsesErrorFrameWithRetryableFlag()
    {
        string json = """{"type":"error","code":"internal","message":"boom","retryable":true}""";

        ServerFrame frame = ProtocolJson.ParseServerFrame(json);

        ErrorFrame errorFrame = Assert.IsType<ErrorFrame>(frame);
        Assert.Equal("internal", errorFrame.Code);
        Assert.True(errorFrame.Retryable);
    }

    [Fact]
    public void ParseServerFrame_RejectsInvalidJson()
    {
        Exception exception = Record.Exception(() => ProtocolJson.ParseServerFrame("""{"type":"result","raw_text":"""));

        ProtocolFrameException protocolError = Assert.IsType<ProtocolFrameException>(exception);
        Assert.False(string.IsNullOrWhiteSpace(protocolError.Message));
    }

    [Fact]
    public void ParseServerFrame_RejectsUnknownFrameType()
    {
        Exception exception = Record.Exception(() => ProtocolJson.ParseServerFrame("""{"type":"telemetry","cpu":0.5}"""));

        ProtocolFrameException protocolError = Assert.IsType<ProtocolFrameException>(exception);
        Assert.Contains("telemetry", protocolError.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseServerFrame_RejectsFrameWithoutType()
    {
        Exception exception = Record.Exception(() => ProtocolJson.ParseServerFrame("""{"raw_text":"orphan"}"""));

        Assert.IsType<ProtocolFrameException>(exception);
    }

    [Fact]
    public void ParseServerFrame_RejectsResultWithoutRequiredMembers()
    {
        Exception exception = Record.Exception(() => ProtocolJson.ParseServerFrame("""{"type":"result"}"""));

        Assert.IsType<ProtocolFrameException>(exception);
    }

    [Fact]
    public void ParseServerFrame_RejectsErrorWithoutCode()
    {
        Exception exception = Record.Exception(() => ProtocolJson.ParseServerFrame("""{"type":"error","message":"no code"}"""));

        Assert.IsType<ProtocolFrameException>(exception);
    }
}
