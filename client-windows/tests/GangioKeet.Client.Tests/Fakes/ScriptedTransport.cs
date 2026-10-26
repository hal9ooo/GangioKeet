using System.Threading.Channels;
using GangioKeet.Client.Protocol;

namespace GangioKeet.Client.Tests.Fakes;

/// <summary>
/// Transport in-memory pilotato dai test: registra ogni operazione nell'ordine esatto,
/// accetta frame ed eccezioni pre-caricati nel canale di ricezione,può sospendere (hang) un
/// send o una close fino a un rilascio esplicito e conta la concorrenza massima osservata
/// per tipo di operazione.
/// Un solo hang alla volta per test: il gate di rilascio è condiviso.
/// </summary>
public sealed class ScriptedTransport : IDictationTransport
{
    private static readonly string StopMarker = "\"type\":\"stop\"";

    private readonly Channel<object> _inbound = Channel.CreateUnbounded<object>();
    private readonly object _gate = new();
    private readonly TaskCompletionSource _releaseGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopSentGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _binarySendHungGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private bool _hangNextSendText;
    private bool _hangNextSendBinary;
    private bool _hangNextClose;

    private int _concurrentTextSends;
    private int _concurrentBinarySends;
    private int _concurrentReceives;
    private int _maxConcurrentTextSends;
    private int _maxConcurrentBinarySends;
    private int _maxConcurrentReceives;

    /// <summary>Registro di ogni operazione nell'ordine esatto di avvio:
    /// "connect", "text:&lt;json&gt;", "binary:&lt;n&gt;", "receive:&lt;kind&gt;", "close", "abort", "dispose".</summary>
    public List<string> Order { get; } = new();

    /// <summary>Copie dell'audio effettivamente inviato (l'ownership è della copia).</summary>
    public List<byte[]> SentAudio { get; } = new();

    public Uri? ConnectedUri { get; private set; }

    public string? ConnectedToken { get; private set; }

    /// <summary>Se impostato, ConnectAsync fallisce con questa eccezione.</summary>
    public Exception? ConnectException { get; set; }

    /// <summary>Frame consegnati al canale di ricezione subito dopo l'invio effettivo di "stop".</summary>
    public List<ServerFrame> DeliverAfterStop { get; } = new();

    public bool StopSent { get; private set; }

    /// <summary>Completa quando lo stop è stato consegnato al wire (delivery-after-stop incluso).</summary>
    public Task WhenStopSent => _stopSentGate.Task;

    /// <summary>Completa quando un send binario è entrato nell'hang.</summary>
    public Task WhenBinarySendHangs => _binarySendHungGate.Task;

    public int CloseCount { get; private set; }

    public int AbortCount { get; private set; }

    public int DisposeCount { get; private set; }

    public int MaxConcurrentTextSends => Volatile.Read(ref _maxConcurrentTextSends);

    public int MaxConcurrentBinarySends => Volatile.Read(ref _maxConcurrentBinarySends);

    public int MaxConcurrentReceives => Volatile.Read(ref _maxConcurrentReceives);

    /// <summary>Incontra il frame successivo lato ricezione (consumato in ordine FIFO dal receiver e dello stato)</summary>
    public void Deliver(ServerFrame frame) => DeliverItem(frame);

    public void DeliverException(Exception exception) => DeliverItem(exception);

    public void HangNextSendText() => HangNext(1);

    public void HangNextSendBinary() => HangNext(2);

    public void HangNextClose() => HangNext(3);

    private void HangNext(int slot)
    {
        lock (_gate)
        {
            _ = slot switch
            {
                1 => _hangNextSendText = true,
                2 => _hangNextSendBinary = true,
                _ => _hangNextClose = true,
            };
        }
    }

    /// <summary>Sblocca un hang già in attesa (il gate è unico per test).</summary>
    public void ReleasePendingSend()
    {
        lock (_gate)
        {
            _releaseGate.TrySetResult();
        }
    }

    public Task ConnectAsync(Uri uri, string? bearerToken, CancellationToken ct)
    {
        lock (_gate)
        {
            ConnectedUri = uri;
            ConnectedToken = bearerToken;
            Order.Add("connect");
            return ConnectException is { } failure
                ? Task.FromException(failure)
                : Task.CompletedTask;
        }
    }

    public async Task SendTextAsync(string json, CancellationToken ct)
    {
        TrackEntry(ref _concurrentTextSends, ref _maxConcurrentTextSends);
        try
        {
            bool hang;
            bool isStop;
            lock (_gate)
            {
                Order.Add("text:" + json);
                hang = _hangNextSendText;
                _hangNextSendText = false;
                isStop = json.Contains(StopMarker, StringComparison.Ordinal);
            }

            // Primo yield: una sessione con send paralleli sovrapporrebbe gli entry-point
            // e i contatori di concorrenza lo vedrebbero.
            await Task.Yield();

            if (isStop && !hang)
            {
                lock (_gate)
                {
                    StopSent = true;
                    foreach (ServerFrame frame in DeliverAfterStop)
                    {
                        DeliverItem(frame);
                    }
                }
                _stopSentGate.TrySetResult();
            }

            if (hang)
            {
                await HangUntilReleasedOrCancelledAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentTextSends);
        }
    }

    public async Task SendBinaryAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        TrackEntry(ref _concurrentBinarySends, ref _maxConcurrentBinarySends);
        try
        {
            byte[] copy = pcm.ToArray();
            bool hang;
            lock (_gate)
            {
                hang = _hangNextSendBinary;
                _hangNextSendBinary = false;
            }

            await Task.Yield();

            if (hang)
            {
                _binarySendHungGate.TrySetResult();
                await HangUntilReleasedOrCancelledAsync(ct).ConfigureAwait(false);
            }

            lock (_gate)
            {
                SentAudio.Add(copy);
                Order.Add("binary:" + copy.Length);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentBinarySends);
        }
    }

    public async Task<ServerFrame> ReceiveAsync(CancellationToken ct)
    {
        TrackEntry(ref _concurrentReceives, ref _maxConcurrentReceives);
        try
        {
            object item = await _inbound.Reader.ReadAsync(ct).ConfigureAwait(false);

            ServerFrame frame = item switch
            {
                ServerFrame scriptedFrame => scriptedFrame,
                Exception failure => throw failure,
                _ => throw new InvalidOperationException("Elemento sconosciuto nella coda scripted."),
            };

            lock (_gate)
            {
                Order.Add("receive:" + DescribeFrame(frame));
            }
            return frame;
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentReceives);
        }
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        bool hang;
        lock (_gate)
        {
            CloseCount++;
            Order.Add("close");
            hang = _hangNextClose;
            _hangNextClose = false;
        }

        if (hang)
        {
            await HangUntilReleasedOrCancelledAsync(ct).ConfigureAwait(false);
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            AbortCount++;
            Order.Add("abort");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            DisposeCount++;
            Order.Add("dispose");
        }
        return ValueTask.CompletedTask;
    }

    private void DeliverItem(object item)
    {
        if (!_inbound.Writer.TryWrite(item))
        {
            throw new InvalidOperationException("La coda scripted di ricezione è già completata.");
        }
    }

    private static string DescribeFrame(ServerFrame frame) => frame switch
    {
        ResultFrame => "result",
        ErrorFrame error => error.Code,
        _ => "unknown",
    };

    private static void TrackEntry(ref int current, ref int maximum)
    {
        int observed = Interlocked.Increment(ref current);
        int snapshot = Volatile.Read(ref maximum);
        while (observed > snapshot)
        {
            int previous = Interlocked.CompareExchange(ref maximum, observed, snapshot);
            if (previous == snapshot)
            {
                break;
            }
            snapshot = previous;
        }
    }

    /// <summary>Resta appeso finché il gate non rilascia o il token non cancella. Nessun timer reale.</summary>
    private async Task HangUntilReleasedOrCancelledAsync(CancellationToken ct)
    {
        Task delayTask = Task.Delay(Timeout.InfiniteTimeSpan, ct);
        Task winner = await Task.WhenAny(_releaseGate.Task, delayTask).ConfigureAwait(false);
        if (winner != _releaseGate.Task)
        {
            throw new OperationCanceledException(ct);
        }
    }
}
