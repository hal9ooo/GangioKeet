namespace GangioKeet.Client.Api;

using System.Diagnostics;
using System.IO;
using GangioKeet.Inference;

/// <summary>
/// Esito di una richiesta passata al <see cref="DictationExecutor"/>: o il risultato della
/// pipeline, o l'errore da servire. Le due sole factory impediscono gli stati mezzi (un
/// successo con un errore, un successo senza risultato), così chi consuma un esito non deve
/// difendersi da combinazioni che non esistono.
/// </summary>
public sealed record ExecutorOutcome
{
    private ExecutorOutcome()
    {
    }

    /// <summary>Vero quando <see cref="Result"/> è presente e c'è qualcosa da restituire.</summary>
    public bool IsSuccess { get; private init; }

    /// <summary>Risultato della pipeline, presente solo in caso di successo.</summary>
    public PipelineResult? Result { get; private init; }

    /// <summary>Errore da servire, presente solo in caso di fallimento.</summary>
    public ApiError? Error { get; private init; }

    /// <summary>
    /// PCM16 mono 16 kHz già decodificato da questa richiesta: è lo stesso buffer passato
    /// a <see cref="IInferencePipeline.ProcessAsync"/>, non una copia. Vuoto in caso di
    /// fallimento, dove non esiste audio da registrare. Nessuno lo copia qui: vive quanto la
    /// richiesta che lo ha prodotto, e chi lo trattiene (la coda delle registrazioni) ne fa
    /// la propria copia.
    /// </summary>
    public ReadOnlyMemory<byte> Pcm16 { get; private init; }

    /// <summary>
    /// La richiesta è andata a buon fine: c'è un risultato da restituire e il PCM che lo ha
    /// prodotto, così chi registra il turno non deve decodificare l'audio una seconda volta.
    /// </summary>
    public static ExecutorOutcome Success(PipelineResult result, ReadOnlyMemory<byte> pcm16)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ExecutorOutcome { IsSuccess = true, Result = result, Pcm16 = pcm16 };
    }

    /// <summary>La richiesta è stata rifiutata o è fallita: c'è un errore da servire.</summary>
    public static ExecutorOutcome Failure(ApiError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new ExecutorOutcome { Error = error, Pcm16 = ReadOnlyMemory<byte>.Empty };
    }
}

/// <summary>
/// Esegue una richiesta sulla pipeline locale con le regole che un server HTTP deve tenere
/// anche quando il motore è uno solo: <em>una richiesta alla volta</em>, un'attesa breve e
/// limitata mentre i modelli si caricano, e i problemi dell'audio (formato, dimensione,
/// durata) tenuti distinti dai guasti interni. Un chiamante che sbaglia riceve 413 o 415,
/// non un 500; un guasto vero riceve 500 con un messaggio generico, senza stack trace né
/// messaggi di eccezione, perché chi chiama non deve imparare com'è fatto il server.
///
/// Il gate interno resta anche se la pipeline ha già la sua serializzazione: qui serve a
/// tenere la coda HTTP in un ordine prevedibile e a rendere vera la proprietà "mai due alla
/// volta" qualunque cosa faccia domani l'implementazione della pipeline. Le richieste ammesse
/// hanno un tetto che vale all'ingresso, <em>prima</em> della decodifica: così il limite copre
/// anche il warm-up, dove una richiesta terrebbe in memoria il proprio PCM per tutta l'attesa
/// dei modelli. Oltre il tetto la richiesta riceve 503 <c>busy</c> subito, senza decodificare
/// e senza restare appesa.
///
/// L'annullamento del chiamante non è un errore: l'operazione lancia
/// <see cref="OperationCanceledException"/> e non produce alcun esito, perché non c'è più
/// nessuno a cui rispondere. Il gate viene rilasciato anche su quel percorso.
/// </summary>
public sealed class DictationExecutor
{
    /// <summary>Passo dell'attesa di disponibilità: abbastanza fine per non perdere il warm-up.</summary>
    private const int ReadinessPollMs = 50;

    /// <summary>Byte di PCM16 mono 16 kHz in un secondo: è il denominatore della durata.</summary>
    private const int PcmBytesPerSecond = 32000;

    /// <summary>
    /// Quante richieste possono essere ammesse all'executor: una che decodifica o tiene il
    /// gate, e due in attesa dietro di lei. Ogni richiesta ammessa arriva a tenere in memoria
    /// il proprio PCM decodificato (fino a 26 MB in produzione), e durante il warm-up lo tiene
    /// per tutta la finestra di attesa dei modelli: per questo il conteggio si fa all'ingresso
    /// di <see cref="RunAsync"/>, prima della decodifica. Oltre questo numero la richiesta
    /// riceve 503 <c>busy</c> e non entra.
    /// </summary>
    private const int MaxAdmittedRequests = 3;

    private readonly Func<IInferencePipeline?> _pipeline;
    private readonly Func<bool> _isReady;
    private readonly Func<bool> _isWarmingUp;
    private readonly double _waitSeconds;
    private readonly double _maxAudioSeconds;
    private readonly long _maxInputBytes;
    private readonly Action<string, string>? _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Richieste ammesse all'executor, contate all'ingresso di <see cref="RunAsync"/> prima
    /// della decodifica: così il tetto vale sul percorso intero (decodifica, warm-up, coda del
    /// gate) e non solo sull'ultima attesa. Si decrementa su ogni via d'uscita, rifiuti
    /// previsti e annullamento compresi.
    /// </summary>
    private int _admitted;

    /// <summary>
    /// <paramref name="pipeline"/> è una funzione perché la pipeline nasce quando i modelli
    /// finiscono di caricarsi: una richiesta che arriva prima trova <c>null</c> e riceve un
    /// 503 da riprovare, non un guasto.
    /// <paramref name="isReady"/> dice se i modelli sono pronti, <paramref name="isWarmingUp"/>
    /// se c'è un caricamento in corso (cioè se vale la pena aspettare).
    /// <paramref name="waitSeconds"/> è il tetto dell'attesa di disponibilità.
    /// <paramref name="maxInputBytes"/> è iniettabile perché i test del 413 non devono
    /// allocare 26 MB; il valore predefinito è <see cref="AudioPcmDecoder.DefaultMaxInputBytes"/>.
    /// <paramref name="log"/> riceve le anomalie che nella risposta non si possono
    /// raccontare: oggi solo il 500 <c>internal_error</c>, con il tipo e il messaggio
    /// dell'eccezione. Va al log dell'app (il file), mai al chiamante, che continua a
    /// leggere un messaggio generico; <c>null</c> significa «nessun log da raggiungere» ed
    /// è lecito nei test. I rifiuti previsti (413, 415, 503) non passano di qui: non sono
    /// incidenti.
    /// </summary>
    public DictationExecutor(Func<IInferencePipeline?> pipeline, Func<bool> isReady,
        Func<bool> isWarmingUp, double waitSeconds, double maxAudioSeconds = 300,
        long maxInputBytes = AudioPcmDecoder.DefaultMaxInputBytes, Action<string, string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(isReady);
        ArgumentNullException.ThrowIfNull(isWarmingUp);
        ArgumentOutOfRangeException.ThrowIfNegative(waitSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAudioSeconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxInputBytes);

        _pipeline = pipeline;
        _isReady = isReady;
        _isWarmingUp = isWarmingUp;
        _waitSeconds = waitSeconds;
        _maxAudioSeconds = maxAudioSeconds;
        _maxInputBytes = maxInputBytes;
        _log = log;
    }

    /// <summary>
    /// Porta <paramref name="audio"/> al contratto della pipeline ed esegue una dettatura
    /// con <paramref name="profile"/>. Restituisce sempre qualcosa da servire, tranne quando
    /// il chiamante annulla: in quel caso l'operazione viene annullata e non c'è esito.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> è stato annullato.</exception>
    public async Task<ExecutorOutcome> RunAsync(Stream audio, string profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(profile);
        ct.ThrowIfCancellationRequested();

        // Il tetto si applica qui, all'ingresso e prima della decodifica: decodificare un
        // audio significa tenerne in memoria fino a 26 MB di PCM, e durante il warm-up quella
        // memoria resta occupata per tutta la finestra di attesa dei modelli. Contare solo la
        // coda del gate lascerebbe senza tetto proprio la parte in cui la memoria cresce.
        //
        // Il conteggio sale prima di ogni lavoro e scende nel finally: vale quindi per i
        // rifiuti previsti (413, 415), per il 503 di caricamento, per l'annullamento e per il
        // guasto. Un contatore che non torna indietro chiuderebbe il server dopo il primo
        // incidente.
        if (Interlocked.Increment(ref _admitted) > MaxAdmittedRequests)
        {
            Interlocked.Decrement(ref _admitted);
            return ExecutorOutcome.Failure(ApiError.Busy(
                "Il server è occupato: riprova fra poco."));
        }

        bool acquired = false;
        try
        {
            byte[] pcm;
            try
            {
                // Il decoder misura in int; il tetto reale in produzione è 26 MB, quindi la
                // conversione non cambia nulla e nessun valore legittimo viene tagliato.
                int decoderLimit = (int)Math.Min(_maxInputBytes, int.MaxValue);
                pcm = AudioPcmDecoder.ToPcm16Mono16k(audio, decoderLimit, out _);
            }
            catch (UnsupportedAudioFormatException error)
            {
                // Messaggio del decoder, già scritto per chi legge: nomina i formati ammessi.
                return ExecutorOutcome.Failure(ApiError.UnsupportedFormat(error.Message));
            }
            catch (AudioTooLargeException)
            {
                // Il messaggio HTTP lo scrive l'executor dal proprio limite: quello del decoder
                // è la sua rete di sicurezza, non il numero che il chiamante deve leggere.
                return ExecutorOutcome.Failure(
                    ApiError.TooLarge($"Audio troppo grande: il limite è {DescribeBytes(_maxInputBytes)}."));
            }

            // Prima la validità del buffer, poi la durata: un byte in più sposta anche il
            // calcolo della durata, quindi un PCM dispari va rifiutato come formato. Il motore
            // ASR lo rifiuterebbe con ArgumentException, cioè un 500; è la stessa classe di
            // difetto della guardia sul payload vuoto chiusa nel decoder.
            if (pcm.Length % 2 != 0)
            {
                return ExecutorOutcome.Failure(ApiError.UnsupportedFormat(
                    "Audio PCM con un numero dispari di byte: non è un flusso 16 bit valido."));
            }

            if (pcm.Length / (double)PcmBytesPerSecond > _maxAudioSeconds)
            {
                return ExecutorOutcome.Failure(
                    ApiError.TooLarge($"Audio troppo lungo: il limite è {_maxAudioSeconds:0.#} secondi."));
            }

            if (!await WaitForReadyAsync(ct).ConfigureAwait(false))
            {
                return ExecutorOutcome.Failure(ApiError.Loading(
                    "I modelli sono ancora in caricamento: riprova fra poco."));
            }

            IInferencePipeline? pipeline = _pipeline();
            if (pipeline is null)
            {
                // Pronto ma senza pipeline è uno stato di passaggio del caricamento: resta un
                // 503 da riprovare, non un 500 che direbbe al chiamante di non riprovare.
                return ExecutorOutcome.Failure(ApiError.Loading(
                    "I modelli non sono ancora pronti: riprova fra poco."));
            }

            // Il gate si prende dopo la decodifica: decodificare non tocca i pesi, quindi non
            // ha senso mettere in fila un lavoro che può procedere in parallelo. Il tetto,
            // invece, è già stato applicato all'ingresso: qui si aspetta solo il proprio turno.
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            PipelineResult result = await pipeline
                .ProcessAsync(new PipelineRequest(pcm, profile), ct)
                .ConfigureAwait(false);
            // Stesso buffer, nessuna copia: il PCM che la pipeline ha appena usato è quello
            // che serve alla registrazione del turno.
            return ExecutorOutcome.Success(result, pcm);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Il chiamante se n'è andato: nessun esito da servire.
            throw;
        }
        catch (Exception error)
        {
            // Nessun dettaglio nella risposta: né stack trace né messaggio dell'eccezione.
            // Il dettaglio va nel log dell'app, perché un 500 senza traccia non si diagnostica.
            try
            {
                _log?.Invoke("api_internal_error",
                    $"Trascrizione non riuscita: {error.GetType().Name}: {error.Message}");
            }
            catch
            {
                // Il log è best-effort: se il delegate lancia, il chiamante deve comunque
                // ricevere il 500 invece di un'eccezione non gestita.
            }

            return ExecutorOutcome.Failure(ApiError.Internal(
                "Trascrizione non riuscita per un errore interno del server."));
        }
        finally
        {
            // Se l'attesa è stata annullata il gate non è mai stato preso: rilasciarlo
            // sbloccherebbe una seconda richiesta oltre il tetto del semaforo.
            if (acquired)
            {
                _gate.Release();
            }

            Interlocked.Decrement(ref _admitted);
        }
    }

    /// <summary>
    /// Aspetta che i modelli siano pronti, a passi di <see cref="ReadinessPollMs"/> e non
    /// oltre <see cref="_waitSeconds"/>. Si aspetta solo se c'è davvero un caricamento in
    /// corso: senza, il modello è già pronto, è fallito o non è mai partito, e far attendere
    /// il chiamante per l'intera finestra sarebbe una bugia.
    /// </summary>
    private async Task<bool> WaitForReadyAsync(CancellationToken ct)
    {
        if (_isReady())
        {
            return true;
        }

        if (!_isWarmingUp())
        {
            return false;
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed.TotalSeconds < _waitSeconds)
        {
            // Task.Delay con il token: l'annullamento del chiamante esce subito, senza
            // aspettare la fine della finestra.
            await Task.Delay(ReadinessPollMs, ct).ConfigureAwait(false);
            if (_isReady())
            {
                return true;
            }

            if (!_isWarmingUp())
            {
                // Il caricamento è finito senza che il modello diventasse pronto: è fallito,
                // e aspettare il resto della finestra non lo farebbe tornare.
                return false;
            }
        }

        return _isReady();
    }

    /// <summary>
    /// Limite leggibile nei messaggi: megabyte quando sono tanti, byte quando sono pochi
    /// (i test abbassano il tetto a 1 KB, e "0 MB" non direbbe niente).
    /// </summary>
    private static string DescribeBytes(long bytes) => bytes >= 1024 * 1024
        ? $"{bytes / (1024.0 * 1024.0):0.#} MB"
        : $"{bytes} byte";
}
