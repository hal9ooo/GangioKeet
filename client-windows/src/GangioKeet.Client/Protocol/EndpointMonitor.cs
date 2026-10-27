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
    public Task ProbeOnceAsync(CancellationToken ct) => ProbeRoundAsync(ct);

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
            await ProbeRoundAsync(ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_interval, _time, ct).ConfigureAwait(false);
                await ProbeRoundAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown normale
        }
    }

    private async Task ProbeRoundAsync(CancellationToken ct)
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
}
