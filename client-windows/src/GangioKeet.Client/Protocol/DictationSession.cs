using System.Threading.Channels;
using GangioKeet.Client.Controller;

namespace GangioKeet.Client.Protocol;

/// <summary>
/// Orchestra una sessione di dettato sopra un <see cref="IDictationTransport"/>: un pump riceve i
/// frame del server, un pump invia audio e stop — mai invii in parallelo, mai un secondo canale.
/// <para>
/// Il fallimento di connessione è restituito subito da <see cref="ConnectAndStartAsync"/>, non
/// nascosto fino a <see cref="FinishAsync"/>. Errori del server e chiusura del socket emergono
/// da <see cref="Completion"/> già durante la registrazione. La deadline di risposta parte solo
/// dopo l'invio effettivo di stop; ogni invio ha un timeout di 2 s così che un peer morto non
/// possa bloccare lo stop. La sessione non sa nulla di policy del controller (incolla, retry, UI):
/// questo livello non ritenta mai.
/// </para>
/// </summary>
public sealed class DictationSession : IDictationSession
{
    /// <summary>Frame testuale "stop" del contratto WebSocket (README §Messaggi client).</summary>
    public const string StopMessageJson = "{\"type\":\"stop\"}";

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan AbortGracePeriod = TimeSpan.FromMilliseconds(250);

    private static readonly DictationError AbortedError =
        new("aborted", "Dictation session aborted before a result was published.", Retryable: false);

    private static readonly DictationError SendTimeoutError =
        new("send_failed", "Send did not complete within the 2 s send timeout.", Retryable: true);

    private readonly IDictationTransport _transport;
    private readonly TimeProvider _time;
    private readonly TimeSpan _responseTimeout;

    private readonly Channel<byte[]> _audioQueue;
    private readonly TaskCompletionSource<DictationOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _pumpsCts = new();

    private volatile Task _receiverPump = Task.CompletedTask;
    private volatile Task _senderPump = Task.CompletedTask;

    private ITimer? _responseDeadlineTimer;

    private int _terminalPublished;
    private int _connectAttempted;
    private int _finishRequested;
    private int _pumpsStopped;
    private volatile bool _started;

    public DictationSession(IDictationTransport transport, TimeProvider time, TimeSpan responseTimeout, int sendQueueCapacity = 20)
    {
        _transport = transport;
        _time = time;
        _responseTimeout = responseTimeout;

        _audioQueue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(sendQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    /// <summary>
    /// Terminale della sessione, leggibile già durante la registrazione; si completa una sola
    /// volta. Se la connessione fallisce il terminale resta aperto finché il controller non chiama
    /// <see cref="AbortAsync"/>/Dispose o <see cref="FinishAsync"/> (che ritornano comunque).
    /// </summary>
    public Task<DictationOutcome> Completion => _completion.Task;

    /// <summary>
    /// Connette (con <paramref name="connectTimeout"/>) e invia lo start. Il receiver parte dopo
    /// l'handshake e prima dello start. Un fallimento è restituito con
    /// <see cref="SessionConnectOutcome.Connected"/>=false (e in tal caso anche via
    /// <see cref="Completion"/>). La cancellazione del chiamante (<paramref name="ct"/>) è
    /// comunque propagata; eccezioni di transport diverse da <see cref="TransportConnectException"/>
    /// (contratto violato) restano in carico al chiamante.
    /// </summary>
    public async Task<SessionConnectOutcome> ConnectAndStartAsync(Uri uri, StartMessage start, string? token, TimeSpan connectTimeout, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _connectAttempted, 1, 0) != 0)
        {
            throw new InvalidOperationException("ConnectAndStartAsync can be called only once per session.");
        }

        ConnectFailure? connectFailure;
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using ITimer timer = CreateCancellingTimer(linked, connectTimeout);
            await _transport.ConnectAsync(uri, token, linked.Token).ConfigureAwait(false);
            return await StartRecordingAsync(start, ct).ConfigureAwait(false);
        }
        catch (TransportConnectException ex)
        {
            connectFailure = ex.Failure;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            connectFailure = new ConnectFailure(
                "connect_failed",
                "WebSocket handshake did not complete within the connect timeout.",
                null);
        }

        PublishTerminal(new DictationOutcome(
            DictationStatus.Error,
            Result: null,
            new DictationError(connectFailure.Code, connectFailure.Message, Retryable: false, connectFailure.HttpStatus)));
        return new SessionConnectOutcome(false, connectFailure);
    }

    /// <summary>
    /// Accoda un chunk audio. Accettabile solo dopo lo start riuscito; dopo una FinishAsync solleva
    /// <see cref="InvalidOperationException"/>. La coda possiede la sua copia: il buffer del caller
    /// (es. riusato da NAudio) non viene mai referenziato internamente. Ritorna false quando la
    /// coda è piena (capacità 20): nessun drop silenzioso, il controller segnala l'errore locale
    /// <c>backpressure</c>.
    /// </summary>
    public bool TryEnqueueAudio(ReadOnlyMemory<byte> ownedPcm)
    {
        if (!_started)
        {
            throw new InvalidOperationException("Audio can be enqueued only after ConnectAndStartAsync succeeded.");
        }

        if (Volatile.Read(ref _finishRequested) != 0)
        {
            throw new InvalidOperationException("Audio can no longer be enqueued after FinishAsync has started.");
        }

        byte[] copy = ownedPcm.ToArray();
        return _audioQueue.Writer.TryWrite(copy);
    }

    /// <summary>
    /// Idempotente: chiude il canale in scrittura, drena i chunk ancora in coda nell'ordine di
    /// arrivo, invia esattamente uno stop e attende il terminale. Se la sessione non è mai
    /// partita (connessione fallita), chiude il terminale in Aborted/Error senza inviare stop.
    /// </summary>
    public async Task<DictationOutcome> FinishAsync(CancellationToken ct)
    {
        bool firstFinish = Interlocked.CompareExchange(ref _finishRequested, 1, 0) == 0;
        if (firstFinish)
        {
            _audioQueue.Writer.TryComplete();
        }

        if (!_started)
        {
            if (firstFinish)
            {
                PublishTerminal(new DictationOutcome(DictationStatus.Aborted, Result: null, AbortedError));
            }

            return await _completion.Task.ConfigureAwait(false);
        }

        await _completion.Task.WaitAsync(ct).ConfigureAwait(false);
        await JoinPumpsAsync().ConfigureAwait(false);

        try
        {
            await _transport.CloseAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // best-effort: la close post-terminale non fa parte del contratto
        }

        return await _completion.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Abort best-effort: prova la close graziosa (massimo 250 ms), poi abbandona il socket e
    /// cancella/attende i pump. Non attende né result né ack.
    /// </summary>
    public async Task AbortAsync(CancellationToken ct)
    {
        // Riserva subito il terminale: un result in corsa non può mai sovrascriverlo.
        PublishTerminal(new DictationOutcome(DictationStatus.Aborted, Result: null, AbortedError));

        if (_started)
        {
            try
            {
                using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                using ITimer timer = CreateCancellingTimer(linked, AbortGracePeriod);
                await _transport.CloseAsync(linked.Token).ConfigureAwait(false);
            }
            catch
            {
                // best-effort: la close non è un prerequisito dell'abort
            }
        }

        StopPumps(abortSocket: true);
        await JoinPumpsAsync().ConfigureAwait(false);
    }

    /// <summary>Cancella e attende i pump senza hang; idempotente; non dispose il transport. </summary>
    public async ValueTask DisposeAsync()
    {
        PublishTerminal(new DictationOutcome(DictationStatus.Aborted, Result: null, AbortedError));
        StopPumps(abortSocket: true);
        await JoinPumpsAsync().ConfigureAwait(false);
        _pumpsCts.Dispose();
        DisposeResponseDeadlineTimer();
    }

    private async Task<SessionConnectOutcome> StartRecordingAsync(StartMessage start, CancellationToken ct)
    {
        // Il receiver parte adesso, prima dello start a filo: nessuna risposta del server
        // (ack d'errore di protocollo compreso) può perdersi.
        StartReceiverPump();

        try
        {
            Task SendStart(CancellationToken sendToken) => _transport.SendTextAsync(ProtocolJson.SerializeStart(start), sendToken);
            await SendWithTimeoutAsync(SendStart, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return FailStart("connect_failed", "The start message was not sent within the 2 s send timeout.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return FailStart("connect_failed", "Failed to send the start message: " + ex.Message);
        }

        _started = true;
        StartSenderPump();
        return new SessionConnectOutcome(true, null);

        SessionConnectOutcome FailStart(string code, string message)
        {
            ConnectFailure failure = new(code, message, null);
            PublishTerminal(new DictationOutcome(
                DictationStatus.Error,
                Result: null,
                new DictationError(failure.Code, failure.Message, Retryable: false, failure.HttpStatus)));
            StopPumps(abortSocket: true);
            return new SessionConnectOutcome(false, failure);
        }
    }

    private void StartReceiverPump()
    {
        _receiverPump = RunReceiverPumpAsync(_pumpsCts.Token);
    }

    private void StartSenderPump()
    {
        _senderPump = RunSenderPumpAsync(_pumpsCts.Token);
    }

    private async Task RunReceiverPumpAsync(CancellationToken pumpCt)
    {
        try
        {
            while (true)
            {
                ServerFrame frame = await _transport.ReceiveAsync(pumpCt).ConfigureAwait(false);

                if (frame is ResultFrame resultFrame)
                {
                    PublishTerminal(new DictationOutcome(DictationStatus.Result, resultFrame.Result, Error: null));
                    return;
                }

                if (frame is ErrorFrame errorFrame)
                {
                    PublishTerminal(new DictationOutcome(
                        DictationStatus.Error,
                        Result: null,
                        new DictationError(errorFrame.Code, errorFrame.Message, errorFrame.Retryable)));
                    return;
                }

                // Frame di tipo imprevisto (non può nascere da ProtocolJson): niente rischio di loop.
                PublishTerminal(new DictationOutcome(
                    DictationStatus.Error,
                    Result: null,
                    new DictationError("protocol", "The server sent an unexpected frame type.", Retryable: false)));
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // cancel di abort/dispose (o chiusura inattesa del source): nessun result atteso
            PublishTerminal(new DictationOutcome(DictationStatus.Aborted, Result: null, AbortedError));
        }
        catch (TransportClosedException ex)
        {
            PublishTerminal(new DictationOutcome(
                DictationStatus.Error,
                Result: null,
                new DictationError("closed", DescribeClosed(ex), Retryable: false)));
        }
        catch (ProtocolFrameException ex)
        {
            PublishTerminal(new DictationOutcome(
                DictationStatus.Error,
                Result: null,
                new DictationError("protocol", ex.Message, Retryable: false)));
        }
        catch (Exception ex)
        {
            PublishTerminal(new DictationOutcome(
                DictationStatus.Error,
                Result: null,
                new DictationError("receive_failed", ex.Message, Retryable: true)));
        }
    }

    private async Task RunSenderPumpAsync(CancellationToken pumpCt)
    {
        ChannelReader<byte[]> reader = _audioQueue.Reader;

        try
        {
            while (await reader.WaitToReadAsync(pumpCt).ConfigureAwait(false))
            {
                while (reader.TryRead(out byte[]? chunk))
                {
                    Task SendAudio(CancellationToken sendToken) => _transport.SendBinaryAsync(chunk, sendToken);
                    await SendWithTimeoutAsync(SendAudio, pumpCt).ConfigureAwait(false);
                }
            }

            // Il canale è chiuso e vuoto: i chunk sono già stati inviati tutti, ora lo stop.
            Task SendStop(CancellationToken sendToken) => _transport.SendTextAsync(StopMessageJson, sendToken);
            await SendWithTimeoutAsync(SendStop, pumpCt).ConfigureAwait(false);
            ArmResponseDeadline();
        }
        catch (OperationCanceledException) when (pumpCt.IsCancellationRequested)
        {
            // stop di abort/dispose: il terminale è già pubblicato da chi ha cancellato
        }
        catch (Exception)
        {
            // compreso l'OCE del send-timeout di 2 s (pumpCt non cancellato): peer morto
            PublishTerminal(new DictationOutcome(DictationStatus.Error, Result: null, SendTimeoutError));
            StopPumps(abortSocket: true);
        }
    }

    private async Task SendWithTimeoutAsync(Func<CancellationToken, Task> send, CancellationToken callerCancelled)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(callerCancelled);
        using ITimer timer = CreateCancellingTimer(linked, SendTimeout);
        await send(linked.Token).ConfigureAwait(false);
    }

    private ITimer CreateCancellingTimer(CancellationTokenSource linked, TimeSpan dueTime)
    {
        return _time.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            linked,
            dueTime,
            Timeout.InfiniteTimeSpan);
    }

    private void ArmResponseDeadline()
    {
        DisposeResponseDeadlineTimer();
        if (Volatile.Read(ref _terminalPublished) != 0)
        {
            return;
        }

        ITimer timer = _time.CreateTimer(
            OnResponseDeadlineElapsed,
            (object?)null,
            _responseTimeout,
            Timeout.InfiniteTimeSpan);
        ITimer? previous = Interlocked.Exchange(ref _responseDeadlineTimer, timer);
        previous?.Dispose();
    }

    private void OnResponseDeadlineElapsed(object? state)
    {
        bool published = PublishTerminal(new DictationOutcome(
            DictationStatus.Error,
            Result: null,
            new DictationError("timeout", "The server did not deliver a result within the response deadline.", Retryable: true)));
        if (published)
        {
            StopPumps(abortSocket: true);
        }
        else
        {
            DisposeResponseDeadlineTimer();
        }
    }

    private static string DescribeClosed(TransportClosedException ex)
    {
        if (ex.CloseCode is not int code)
        {
            return "WebSocket closed unexpectedly.";
        }

        return string.IsNullOrEmpty(ex.CloseReason)
            ? $"WebSocket closed by the server with code {code}."
            : $"WebSocket closed by the server with code {code} ({ex.CloseReason}).";
    }

    private bool PublishTerminal(DictationOutcome outcome)
    {
        if (Interlocked.CompareExchange(ref _terminalPublished, 1, 0) != 0)
        {
            return false;
        }

        DisposeResponseDeadlineTimer();
        _completion.TrySetResult(outcome);
        return true;
    }

    private void StopPumps(bool abortSocket)
    {
        if (abortSocket)
        {
            _transport.Abort();
        }

        if (Interlocked.Exchange(ref _pumpsStopped, 1) == 0)
        {
            _pumpsCts.Cancel();
        }
    }

    private async Task JoinPumpsAsync()
    {
        await _senderPump.ConfigureAwait(false);
        await _receiverPump.ConfigureAwait(false);
    }

    private void DisposeResponseDeadlineTimer()
    {
        Interlocked.Exchange(ref _responseDeadlineTimer, null)?.Dispose();
    }
}
