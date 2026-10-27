using GangioKeet.Inference;

namespace GangioKeet.Client.Platform;

/// <summary>
/// Possiede il ciclo di vita dei modelli locali: caricamento e warm-up fuori
/// dal thread UI, stato osservabile e arresto che attende il ritorno delle
/// chiamate native prima del dispose. Non crea servizi desktop.
/// </summary>
public sealed class AppRuntime : IAsyncDisposable
{
    private readonly IInferencePipeline _pipeline;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task? _startTask;
    private Task? _stopTask;
    private RuntimeState _state = RuntimeState.Loading;

    public AppRuntime(IInferencePipeline pipeline) => _pipeline = pipeline;

    public IInferencePipeline Pipeline => _pipeline;

    public RuntimeState State
    {
        get { lock (_gate) return _state; }
    }

    public Exception? Failure { get; private set; }

    public bool IsReady => State == RuntimeState.Ready;

    public Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_stopTask is not null || _state == RuntimeState.Stopped)
                throw new InvalidOperationException("The runtime has already stopped.");
            return _startTask ??= StartCoreAsync(ct);
        }
    }

    private async Task StartCoreAsync(CancellationToken ct)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        try
        {
            await _pipeline.WarmUpAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            lock (_gate) _state = RuntimeState.Ready;
        }
        catch (OperationCanceledException error)
        {
            if (!_lifetime.IsCancellationRequested)
            {
                Failure = error;
                lock (_gate) _state = RuntimeState.Failed;
            }
        }
        catch (Exception error)
        {
            Failure = error;
            lock (_gate) _state = RuntimeState.Failed;
        }
    }

    public Task StopAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            _stopTask ??= StopCoreAsync();
            return _stopTask;
        }
    }

    private async Task StopCoreAsync()
    {
        lock (_gate) _state = RuntimeState.Stopping;
        _lifetime.Cancel();

        Task? starting;
        lock (_gate) starting = _startTask;
        if (starting is not null)
        {
            try { await starting.ConfigureAwait(false); }
            catch (Exception) { /* StartCoreAsync records its own failure. */ }
        }

        try
        {
            // DisposeAsync waits for an active native decode before releasing
            // the model. Do not pass a cancellation token here: a stop request
            // must not abandon memory still used by the native call.
            await _pipeline.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Failure = error;
        }
        finally
        {
            lock (_gate) _state = RuntimeState.Stopped;
            _lifetime.Dispose();
        }
    }

    public ValueTask DisposeAsync() => new(StopAsync(CancellationToken.None));
}
