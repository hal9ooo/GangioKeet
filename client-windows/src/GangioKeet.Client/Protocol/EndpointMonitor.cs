using GangioKeet.Client.Configuration;

namespace GangioKeet.Client.Protocol;

/// <summary>
/// Unico loop periodico di monitoraggio: prova tutti gli endpoint in sequenza,
/// applica i risultati al selettore e lascia che sia lui a decidere l'attivo.
/// </summary>
public sealed class EndpointMonitor : IDisposable
{
    private readonly EndpointProbe _probe;
    private readonly EndpointSelector _selector;
    private readonly TimeProvider _time;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly SemaphoreSlim _roundGate = new(1, 1);

    private Task? _loop;
    private bool _disposed;

    public EndpointMonitor(
        EndpointProbe probe,
        EndpointSelector selector,
        TimeProvider time,
        TimeSpan interval,
        TimeSpan timeout)
    {
        _probe = probe;
        _selector = selector;
        _time = time;
        _interval = interval;
        _timeout = timeout;
    }

    public Task RunAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _loop ??= RunLoopAsync(
            CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token).Token);
    }

    /// <summary>Esegue subito un round, per il "verifica adesso" dopo un salvataggio.</summary>
    public Task ProbeOnceAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return ProbeOnceCoreAsync(ct);
    }

    private async Task ProbeOnceCoreAsync(CancellationToken ct)
    {
        // Il token di dispose vale anche per i round ad-hoc: chiudere l'app
        // mentre un probe è in volo non deve continuare a lavorare.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposeCts.Token);
        await ProbeRoundAsync(linked.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await RunRoundSafeAsync(ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_interval, _time, ct).ConfigureAwait(false);
                await RunRoundSafeAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown normale
        }
    }

    /// <summary>
    /// Un round che esplode (per esempio su un HttpClient già disposto) non deve
    /// fermare il monitor per sempre: il round successivo riprova.
    /// </summary>
    private async Task RunRoundSafeAsync(CancellationToken ct)
    {
        try
        {
            await ProbeRoundAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
        }
    }

    private async Task ProbeRoundAsync(CancellationToken ct)
    {
        await _roundGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (EndpointStats stats in _selector.Snapshot())
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                ProbeResult result = await _probe
                    .ProbeAsync(stats.Endpoint, _timeout, ct)
                    .ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                _selector.Apply(stats.Endpoint, result);
            }
        }
        finally
        {
            _roundGate.Release();
        }
    }
}
