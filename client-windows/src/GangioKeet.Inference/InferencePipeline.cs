using GangioKeet.Inference.Cleaning;

namespace GangioKeet.Inference;

public sealed class InferencePipeline(ISttEngine stt, ITextCleaner cleaner, CleanupPolicy policy,
    TimeProvider time) : IInferencePipeline
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private bool _warmed;
    private int _disposed;
    private Task? _disposeTask;

    public async Task WarmUpAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_warmed) return;
            await stt.LoadAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            await cleaner.LoadAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            await stt.TranscribeAsync(new byte[32000], linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            // Shader compilation is cold-start work. Do not count it as a turn or
            // apply a per-dictation cleaner deadline to initialization.
            await cleaner.CleanAsync("prova di riscaldamento", "default", linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            _warmed = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        long totalStart = time.GetTimestamp();
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!_warmed) throw new InvalidOperationException("The pipeline is not ready; warm up first.");
            long start = time.GetTimestamp();
            SpeechTranscript transcript = await stt.TranscribeAsync(request.Pcm16, linked.Token).ConfigureAwait(false);
            double sttMs = time.GetElapsedTime(start).TotalMilliseconds;
            linked.Token.ThrowIfCancellationRequested();
            string raw = transcript.Text.Trim();
            string final = "";
            double llmMs = 0;
            EngineIdentity? cleanerIdentity = null;
            List<string> warnings = [];
            if (raw.Length > 0)
            {
                using CancellationTokenSource deadline = new(policy.LlmTimeout, time);
                using CancellationTokenSource cleaning = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, deadline.Token);
                start = time.GetTimestamp();
                try
                {
                    CleanedText result = await cleaner.CleanAsync(raw, request.Profile, cleaning.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    deadline.Token.ThrowIfCancellationRequested();
                    final = result.Text;
                    cleanerIdentity = result.Engine;
                }
                catch (OperationCanceledException) when (!linked.IsCancellationRequested && deadline.IsCancellationRequested)
                {
                    final = GlossaryNormalizer.Normalize(raw, policy);
                    warnings.Add("llm_timeout");
                }
                catch (Exception) when (!linked.IsCancellationRequested)
                {
                    final = GlossaryNormalizer.Normalize(raw, policy);
                    warnings.Add("llm_failed");
                }
                finally { llmMs = time.GetElapsedTime(start).TotalMilliseconds; }
            }
            linked.Token.ThrowIfCancellationRequested();
            return new(raw, final, sttMs, llmMs, time.GetElapsedTime(totalStart).TotalMilliseconds,
                warnings, transcript.Engine, cleanerIdentity);
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            try { await cleaner.DisposeAsync().ConfigureAwait(false); }
            finally { await stt.DisposeAsync().ConfigureAwait(false); }
            _lifetime.Dispose();
        }
        finally { _gate.Release(); }
    }
}
