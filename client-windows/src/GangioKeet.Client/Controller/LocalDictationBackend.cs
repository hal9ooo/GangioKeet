using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recording;
using GangioKeet.Inference;

namespace GangioKeet.Client.Controller;

public sealed class LocalDictationBackend(IInferencePipeline pipeline, Func<bool>? isReady = null,
    IRecordingSink? recordings = null, Func<bool>? isWarmingUp = null,
    Func<Exception?>? failure = null) : IDictationBackend
{
    public bool IsReady => isReady?.Invoke() ?? true;
    public bool AllowsEarlyAudio => isWarmingUp?.Invoke() == true;
    public bool IsFailed => failure?.Invoke() is not null;

    public IActiveDictationTurn CreateTurn(StartMessage start) => new LocalDictationTurn(pipeline, start,
        isReady: isReady, recordings: recordings, allowEarlyAudio: AllowsEarlyAudio, failure: failure);

    public async Task<DictationOutcome> RetryAsync(ReadOnlyMemory<byte> pcm, StartMessage start, CancellationToken ct)
    {
        await using IActiveDictationTurn turn = CreateTurn(start);
        TurnStartOutcome outcome = await turn.StartAsync(ct).ConfigureAwait(false);
        if (!outcome.Ready) return await turn.Completion.ConfigureAwait(false);
        if (!turn.TryEnqueueAudio(pcm)) return await turn.Completion.ConfigureAwait(false);
        return await turn.FinishAsync(ct).ConfigureAwait(false);
    }
}
