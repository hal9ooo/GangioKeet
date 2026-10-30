using GangioKeet.Inference.Cleaning;

namespace GangioKeet.Inference;

public sealed class InferencePipeline : IInferencePipeline
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private readonly CleanupPolicy _policy;
    private readonly TimeProvider _time;
    private ISttEngine _stt;
    private ITextCleaner? _cleaner;
    private bool _refinementEnabled;
    private bool _warmed;
    private int _disposed;
    private Task? _disposeTask;

    public InferencePipeline(ISttEngine stt, ITextCleaner? cleaner, CleanupPolicy policy,
        TimeProvider time, bool refinementEnabled = true)
    {
        _stt = stt ?? throw new ArgumentNullException(nameof(stt));
        _cleaner = cleaner;
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _refinementEnabled = refinementEnabled;
    }

    public bool RefinementEnabled
    {
        get => Volatile.Read(ref _refinementEnabled);
        set => Volatile.Write(ref _refinementEnabled, value);
    }

    public bool HasCleaner => Volatile.Read(ref _cleaner) is not null;

    public async Task WarmUpAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_warmed) return;
            await _stt.LoadAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            ITextCleaner? cleaner = Volatile.Read(ref _cleaner);
            bool refine = Volatile.Read(ref _refinementEnabled);
            if (refine && cleaner is not null)
            {
                await cleaner.LoadAsync(linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
            }
            await _stt.TranscribeAsync(new byte[32000], linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            // Shader compilation is cold-start work. Do not count it as a turn or
            // apply a per-dictation cleaner deadline to initialization.
            if (refine && cleaner is not null)
            {
                await cleaner.CleanAsync("prova di riscaldamento", "default", linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
            }
            _warmed = true;
        }
        finally { _gate.Release(); }
    }

    public async Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        long totalStart = _time.GetTimestamp();
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_warmed) throw new InvalidOperationException("The pipeline is not ready; warm up first.");
            ISttEngine stt = _stt;
            ITextCleaner? cleaner = Volatile.Read(ref _cleaner);
            bool refine = Volatile.Read(ref _refinementEnabled);
            long start = _time.GetTimestamp();
            SpeechTranscript transcript = await stt.TranscribeAsync(request.Pcm16, linked.Token).ConfigureAwait(false);
            double sttMs = _time.GetElapsedTime(start).TotalMilliseconds;
            linked.Token.ThrowIfCancellationRequested();
            string raw = transcript.Text.Trim();
            string final = "";
            double llmMs = 0;
            EngineIdentity? cleanerIdentity = null;
            List<string> warnings = [];
            if (raw.Length > 0)
            {
                if (refine && cleaner is not null)
                {
                    using CancellationTokenSource deadline = new(_policy.LlmTimeout, _time);
                    using CancellationTokenSource cleaning = CancellationTokenSource.CreateLinkedTokenSource(linked.Token, deadline.Token);
                    start = _time.GetTimestamp();
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
                        final = GlossaryNormalizer.Normalize(raw, _policy, request.Profile);
                        warnings.Add("llm_timeout");
                    }
                    catch (Exception) when (!linked.IsCancellationRequested)
                    {
                        final = GlossaryNormalizer.Normalize(raw, _policy, request.Profile);
                        warnings.Add("llm_failed");
                    }
                    finally { llmMs = _time.GetElapsedTime(start).TotalMilliseconds; }
                }
                else
                {
                    final = raw;
                }
            }
            linked.Token.ThrowIfCancellationRequested();
            return new(raw, final, sttMs, llmMs, _time.GetElapsedTime(totalStart).TotalMilliseconds,
                warnings, transcript.Engine, cleanerIdentity);
        }
        finally { _gate.Release(); }
    }

    public Task SwapSttAsync(ISttEngine stt, CancellationToken ct) =>
        SwapEnginesAsync(stt, null, RefinementEnabled, ct);

    public Task SwapCleanerAsync(ITextCleaner? cleaner, CancellationToken ct) =>
        SwapEnginesAsync(null, cleaner, RefinementEnabled, ct);

    public async Task SwapEnginesAsync(ISttEngine? stt, ITextCleaner? cleaner,
        bool refinementEnabled, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (refinementEnabled && cleaner is null && Volatile.Read(ref _cleaner) is null)
            throw new InvalidOperationException("Refinement requires a cleaner.");

        try
        {
            if (stt is not null)
            {
                await stt.LoadAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                await stt.TranscribeAsync(new byte[32000], ct).ConfigureAwait(false);
            }
            if (cleaner is not null)
            {
                await cleaner.LoadAsync(ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                await cleaner.CleanAsync("prova di riscaldamento", "default", ct).ConfigureAwait(false);
            }
        }
        catch
        {
            await DisposeNewAsync(stt, cleaner).ConfigureAwait(false);
            throw;
        }

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await DisposeNewAsync(stt, cleaner).ConfigureAwait(false);
            throw;
        }

        bool committed = false;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ISttEngine oldStt = _stt;
            ITextCleaner? oldCleaner = Volatile.Read(ref _cleaner);
            if (stt is not null) _stt = stt;
            if (cleaner is not null) Volatile.Write(ref _cleaner, cleaner);
            Volatile.Write(ref _refinementEnabled, refinementEnabled);
            committed = true;
            if (stt is not null && !ReferenceEquals(oldStt, stt))
                await oldStt.DisposeAsync().ConfigureAwait(false);
            if (cleaner is not null && !ReferenceEquals(oldCleaner, cleaner) && oldCleaner is not null)
                await oldCleaner.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            if (!committed) await DisposeNewAsync(stt, cleaner).ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    private static async Task DisposeNewAsync(ISttEngine? stt, ITextCleaner? cleaner)
    {
        if (stt is not null)
        {
            try { await stt.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
        }
        if (cleaner is not null)
        {
            try { await cleaner.DisposeAsync().ConfigureAwait(false); } catch { /* best effort */ }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _lifetime.Cancel();
            ITextCleaner? cleaner = Volatile.Read(ref _cleaner);
            if (cleaner is not null) await cleaner.DisposeAsync().ConfigureAwait(false);
            await _stt.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
        }
        finally { _gate.Release(); }
    }
}
