using System.Buffers;
using GangioKeet.Client.Protocol;
using GangioKeet.Inference;

namespace GangioKeet.Client.Controller;

public sealed class LocalDictationTurn : IActiveDictationTurn
{
    private readonly IInferencePipeline _pipeline;
    private readonly StartMessage _start;
    private readonly Func<bool> _isReady;
    private readonly int _maxBytes;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _abort = new();
    private readonly TaskCompletionSource<DictationOutcome> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ArrayBufferWriter<byte>? _audio;
    private Task? _running;
    private Task? _disposeTask;
    private bool _started, _aborted, _disposed;
    public string BackendName => "Locale";
    public Task<DictationOutcome> Completion => _completion.Task;

    public LocalDictationTurn(IInferencePipeline pipeline, StartMessage start, int maxBytes = 67108864, Func<bool>? isReady = null)
    {
        if (maxBytes < 2) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        _pipeline = pipeline; _start = start; _maxBytes = maxBytes; _isReady = isReady ?? (() => true);
        _audio = new(Math.Min(maxBytes, 16384));
    }

    public Task<TurnStartOutcome> StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            if (_aborted || _completion.Task.IsCompleted) return Task.FromResult(new TurnStartOutcome(false, new("aborted", "Dettatura annullata", false)));
            if (!_isReady())
            {
                DictationError error = new("local_not_ready", "Modelli locali non pronti", true);
                Fail(error);
                return Task.FromResult(new TurnStartOutcome(false, error));
            }
            _started = true;
            return Task.FromResult(new TurnStartOutcome(true, null));
        }
    }

    public bool TryEnqueueAudio(ReadOnlyMemory<byte> ownedPcm)
    {
        lock (_gate)
        {
            if (_disposed || !_started || _running is not null || _completion.Task.IsCompleted || _audio is null) return false;
            if (ownedPcm.Length % 2 != 0) { Fail(new("invalid_audio", "Audio PCM16 non valido", false)); return false; }
            if (ownedPcm.Length > _maxBytes - _audio.WrittenCount)
            { Fail(new("audio_too_large", "Limite audio raggiunto", true)); return false; }
            _audio.Write(ownedPcm.Span);
            return true;
        }
    }

    public Task<DictationOutcome> FinishAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_completion.Task.IsCompleted) return _completion.Task;
            if (!_started) { Fail(new("not_started", "Dettatura non avviata", false)); return _completion.Task; }
            if (_running is null)
            {
                ReadOnlyMemory<byte> pcm = _audio!.WrittenMemory;
                _audio = null; // transfer the backing array to the operation, without another full copy
                if (pcm.IsEmpty) { Fail(new("empty_audio", "Audio vuoto", false)); return _completion.Task; }
                _running = RunAsync(pcm);
            }
            return AwaitFinishAsync(ct);
        }
    }

    private async Task<DictationOutcome> AwaitFinishAsync(CancellationToken ct)
    {
        using CancellationTokenRegistration registration = ct.Register(RequestAbort);
        return await _completion.Task.ConfigureAwait(false);
    }

    private async Task RunAsync(ReadOnlyMemory<byte> pcm)
    {
        try
        {
            _abort.Token.ThrowIfCancellationRequested();
            PipelineResult result = await _pipeline.ProcessAsync(new(pcm, _start.Profile), _abort.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_aborted) PublishAborted();
                else _completion.TrySetResult(new(DictationStatus.Result, new ResultMessage(result.RawText, result.FinalText,
                    string.IsNullOrEmpty(result.RawText), new(result.SttMs, result.LlmMs, result.TotalMs), result.Warnings), null));
            }
        }
        catch (OperationCanceledException) { lock (_gate) PublishAborted(); }
        catch (Exception) { lock (_gate) Fail(new("local_inference_failed", "Elaborazione locale non riuscita", true)); }
    }

    private void Fail(DictationError error)
    {
        _audio = null;
        _completion.TrySetResult(new(DictationStatus.Error, null, error));
    }

    private void PublishAborted() => _completion.TrySetResult(new(DictationStatus.Aborted, null, new("aborted", "Dettatura annullata", false)));

    private void RequestAbort()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _aborted = true;
            _audio = null;
            _abort.Cancel();
            if (_running is null) PublishAborted();
        }
    }

    public async Task AbortAsync(CancellationToken ct)
    {
        RequestAbort();
        Task? running;
        lock (_gate) running = _running;
        // Cancellation of the caller does not permit freeing an active native operation.
        if (running is not null) await running.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await AbortAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_gate) { _disposed = true; _audio = null; _abort.Dispose(); }
    }
}
