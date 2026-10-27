namespace GangioKeet.Inference;

public sealed record EngineIdentity(string ModelId, string Revision, string ArtifactSha256,
    string RuntimeVersion, string Backend, string Precision, string DeviceName);
public sealed record SpeechTranscript(string Text, EngineIdentity Engine);
public sealed record CleanedText(string Text, EngineIdentity Engine);
public sealed record PipelineRequest(ReadOnlyMemory<byte> Pcm16, string Profile);
public sealed record PipelineResult(string RawText, string FinalText, double SttMs,
    double LlmMs, double TotalMs, IReadOnlyList<string> Warnings,
    EngineIdentity Stt, EngineIdentity? Cleaner);

public interface ISttEngine : IAsyncDisposable
{
    Task LoadAsync(CancellationToken ct);
    Task<SpeechTranscript> TranscribeAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct);
}

public interface ITextCleaner : IAsyncDisposable
{
    Task LoadAsync(CancellationToken ct);
    Task<CleanedText> CleanAsync(string raw, string profile, CancellationToken ct);
}

public interface IInferencePipeline : IAsyncDisposable
{
    Task WarmUpAsync(CancellationToken ct);
    Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct);
}
