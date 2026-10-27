using GangioKeet.Client.Protocol;
using GangioKeet.Inference;

namespace GangioKeet.Client.Recording;

public interface IRecordingWriter
{
    Task<string?> RecordAsync(ReadOnlyMemory<byte> pcm, PipelineResult result,
        StartMessage start, CancellationToken ct);
}

public interface IRecordingSink
{
    bool TryEnqueue(ReadOnlyMemory<byte> pcm, PipelineResult result, StartMessage start);
}

public sealed record RecordingEntry(
    string Schema,
    string Id,
    string AudioPath,
    string AudioSha256,
    int AudioBytes,
    double DurationSeconds,
    int SampleRate,
    int Channels,
    int SampleWidth,
    string CreatedAt,
    string Transport,
    string Profile,
    string SttModel,
    string? LlmModel,
    string RawText,
    string FinalText,
    IReadOnlyList<string> Warnings,
    double SttMs,
    double LlmMs,
    double TotalMs,
    string ReferenceRaw,
    string ReferenceFinal,
    string ReferenceStatus,
    string Category,
    IReadOnlyList<string> TechnicalTerms,
    bool IsSilence,
    EngineIdentity SttEngine,
    EngineIdentity? CleanerEngine);
