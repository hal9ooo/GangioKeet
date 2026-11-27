using System.Runtime.InteropServices;
using GangioKeet.Client.Api;
using GangioKeet.Inference;
using NAudio.Wave;
using Xunit;

namespace GangioKeet.Client.Tests.Api;

public sealed class DictationExecutorTests
{
    // WAV 16 kHz mono 16-bit valido, riempito con rumore deterministico.
    private static byte[] Wav16k(double seconds)
    {
        using MemoryStream stream = new();
        WaveFormat format = new(16000, 16, 1);
        using (WaveFileWriter writer = new(stream, format))
        {
            byte[] buffer = new byte[(int)(format.AverageBytesPerSecond * seconds)];
            new Random(7).NextBytes(buffer);
            writer.Write(buffer, 0, buffer.Length);
        }
        return stream.ToArray();
    }

    // WAV il cui chunk data dichiara una lunghezza DISPARI e la contiene: la premessa del
    // brief («il decoder lo lascia passare») è falsa, e il test sotto la misura. NAudio
    // riscrive il WAV in forma canonica e aggiunge il byte di padding che il RIFF prevede per
    // i chunk dispari, quindi all'executor arriva un PCM pari di 802 byte, cioè audio valido:
    // nessun 415, nessun 500.
    private static byte[] OddLengthWav()
    {
        const int payload = 801; // dispari, ~25 ms a 16 kHz mono 16-bit
        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(36 + payload); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)1);
            writer.Write(16000); writer.Write(32000); writer.Write((short)2); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(payload);
            writer.Write(new byte[payload]);
        }
        return stream.ToArray();
    }

    // Supera il tetto del decoder senza allocare 26 MB: l'executor accetta
    // maxInputBytes come parametro, quindi il test usa un limite di 1 KB.
    private static byte[] TooLargeStream() => new byte[4096];

    [Fact]
    public async Task NeverRunsTwoRequestsAtTheSameTime()
    {
        SlowPipeline pipeline = new();
        DictationExecutor executor = new(() => pipeline, () => true, () => false, 5);
        // Tre richieste: una dentro e due in coda. Il tetto delle richieste ammesse è tre,
        // quindi una quarta riceverebbe busy prima di arrivare alla pipeline.
        await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => executor.RunAsync(new MemoryStream(Wav16k(0.5)), "default", default)));
        Assert.Equal(1, pipeline.MaximumConcurrent);
        Assert.Equal(3, pipeline.Calls);
    }

    [Fact]
    public async Task WaitsForWarmUpThenReportsLoadingWhenItExpires()
    {
        DictationExecutor executor = new(() => new SlowPipeline(), () => false, () => true, 0.2);
        ExecutorOutcome outcome = await executor.RunAsync(new MemoryStream(Wav16k(0.5)), "default", default);
        Assert.Equal("model_loading", outcome.Error!.Error);
        Assert.Equal(503, outcome.Error.Status);
    }

    [Fact]
    public async Task RejectsAudioLongerThanTheLimit()
    {
        DictationExecutor executor = new(() => new SlowPipeline(), () => true, () => false, 1,
            maxAudioSeconds: 1);
        ExecutorOutcome outcome = await executor.RunAsync(new MemoryStream(Wav16k(3)), "default", default);
        Assert.Equal(413, outcome.Error!.Status);
    }

    [Fact]
    public async Task UnsupportedFormatIsNotAnInternalError()
    {
        DictationExecutor executor = new(() => new SlowPipeline(), () => true, () => false, 1);
        ExecutorOutcome outcome = await executor.RunAsync(
            new MemoryStream("non audio"u8.ToArray()), "default", default);
        Assert.Equal("unsupported_audio_format", outcome.Error!.Error);
        Assert.Equal(415, outcome.Error.Status);
    }

    [Fact]
    public async Task AnOddLengthDataChunkArrivesAsEvenPcmAndSucceeds()
    {
        // Il chunk data dichiara 801 byte, dispari. TranscribeSttEngine rifiuta il PCM
        // dispari con ArgumentException, cioè un 500 se arrivasse fin lì. Il decoder però
        // non lo consegna com'è: riscrive il WAV in forma canonica e NAudio aggiunge il byte
        // di padding che il RIFF prevede per i chunk dispari, quindi il PCM che arriva ha
        // 802 byte. Questo test pinna entrambe le cose: la premessa (il decoder emette un
        // PCM pari) e il requisito (un chunk dispari non è mai un errore interno).
        // La guardia su pcm.Length % 2 resta nell'executor come difesa per un percorso che
        // un giorno consegnasse PCM dispari: è l'ASR, non il decoder, a rifiutarlo.
        byte[] pcm = AudioPcmDecoder.ToPcm16Mono16k(new MemoryStream(OddLengthWav()), out _);
        Assert.Equal(0, pcm.Length % 2);

        DictationExecutor executor = new(() => new SlowPipeline(), () => true, () => false, 1);
        ExecutorOutcome outcome = await executor.RunAsync(new MemoryStream(OddLengthWav()), "default", default);
        Assert.NotEqual("internal_error", outcome.Error?.Error);
        Assert.True(outcome.IsSuccess);
    }

    [Fact]
    public async Task AudioTooLargeIs413WithItsOwnCode()
    {
        DictationExecutor executor = new(() => new SlowPipeline(), () => true, () => false, 1,
            maxInputBytes: 1024);
        ExecutorOutcome outcome = await executor.RunAsync(new MemoryStream(TooLargeStream()), "default", default);
        Assert.Equal("audio_too_large", outcome.Error!.Error);
        Assert.Equal(413, outcome.Error.Status);
    }

    [Fact]
    public async Task AFinishedRequestLeavesTheQueueFree()
    {
        SlowPipeline pipeline = new();
        DictationExecutor executor = new(() => pipeline, () => true, () => false, 5);
        await executor.RunAsync(new MemoryStream(Wav16k(0.5)), "default", default);
        ExecutorOutcome second = await executor.RunAsync(new MemoryStream(Wav16k(0.5)), "default", default);
        Assert.True(second.IsSuccess);
        Assert.Equal(2, pipeline.Calls);
    }

    // ---- Il PCM decodificato esce dall'esito: chi registra il turno non decodifica di nuovo.

    [Fact]
    public async Task ASuccessfulRunExposesTheDecodedPcmWithoutCopyingIt()
    {
        // Il WAV si decodifica una volta sola, nel test, per sapere quale lunghezza deve
        // avere il PCM esposto: è la stessa decodifica che l'executor fa per la pipeline.
        byte[] wav = Wav16k(0.5);
        int decodedLength = AudioPcmDecoder.ToPcm16Mono16k(new MemoryStream(wav), out _).Length;

        CapturingPipeline pipeline = new();
        DictationExecutor executor = new(() => pipeline, () => true, () => false, 5);
        ExecutorOutcome outcome = await executor.RunAsync(new MemoryStream(wav), "default", default);

        Assert.True(outcome.IsSuccess);
        Assert.False(outcome.Pcm16.IsEmpty);
        Assert.Equal(decodedLength, outcome.Pcm16.Length);

        // Nessuna copia: l'esito porta lo stesso array che la pipeline ha ricevuto. Se
        // qualcuno reintroducesse una copia (o una seconda decodifica) questo confronto
        // smetterebbe di essere vero.
        Assert.True(MemoryMarshal.TryGetArray(outcome.Pcm16, out ArraySegment<byte> exposed));
        Assert.True(MemoryMarshal.TryGetArray(pipeline.Received, out ArraySegment<byte> passed));
        Assert.Same(passed.Array, exposed.Array);
    }

    [Fact]
    public async Task AFailedRunCarriesNoPcm()
    {
        DictationExecutor executor = new(() => new SlowPipeline(), () => true, () => false, 1);
        ExecutorOutcome outcome = await executor.RunAsync(
            new MemoryStream("non audio"u8.ToArray()), "default", default);

        Assert.False(outcome.IsSuccess);
        Assert.True(outcome.Pcm16.IsEmpty);
    }

    // ---- Il gate deve tornare libero su OGNI via d'uscita, non solo su quella felice.

    [Fact]
    public async Task ACancelledRequestLeavesTheQueueFree()
    {
        SignallingPipeline pipeline = new();
        DictationExecutor executor = new(() => pipeline, () => true, () => false, 5);
        using CancellationTokenSource cancel = new();

        Task<ExecutorOutcome> cancelled = executor.RunAsync(
            new MemoryStream(Wav16k(0.5)), "default", cancel.Token);
        await pipeline.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        cancel.Cancel();

        // Il chiamante se n'è andato: nessun esito da servire, non un 500.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelled.WaitAsync(TimeSpan.FromSeconds(5)));

        ExecutorOutcome next = await executor.RunAsync(
            new MemoryStream(Wav16k(0.5)), "default", default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(next.IsSuccess);
        Assert.Equal(2, pipeline.Calls);
    }

    [Fact]
    public async Task APipelineFailureIsA500WithoutInternalsAndFreesTheQueue()
    {
        FailOncePipeline pipeline = new();
        DictationExecutor executor = new(() => pipeline, () => true, () => false, 5);

        ExecutorOutcome failed = await executor.RunAsync(
            new MemoryStream(Wav16k(0.5)), "default", default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(failed.IsSuccess);
        Assert.Equal("internal_error", failed.Error!.Error);
        Assert.Equal(500, failed.Error.Status);

        // Nessun dettaglio interno: né il messaggio dell'eccezione né il nome del file.
        Assert.DoesNotContain(FailOncePipeline.Secret, failed.Error.Message);
        Assert.DoesNotContain("Exception", failed.Error.Message);

        ExecutorOutcome next = await executor.RunAsync(
            new MemoryStream(Wav16k(0.5)), "default", default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(next.IsSuccess);
        Assert.Equal(2, pipeline.Calls);
    }

    [Fact]
    public async Task AFailureDoesNotStrandQueuedRequests()
    {
        FailOncePipeline pipeline = new();
        DictationExecutor executor = new(() => pipeline, () => true, () => false, 5);

        ExecutorOutcome[] outcomes = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(_ => executor.RunAsync(new MemoryStream(Wav16k(0.5)), "default", default)))
            .WaitAsync(TimeSpan.FromSeconds(10));

        // Un solo guasto, due successi: il gate è stato rilasciato anche dal percorso
        // che lancia, quindi la coda non resta appesa.
        Assert.Single(outcomes, outcome => !outcome.IsSuccess);
        Assert.Equal(2, outcomes.Count(outcome => outcome.IsSuccess));
        Assert.Equal(3, pipeline.Calls);
    }

    // ---- Le richieste ammesse hanno un tetto: una di troppo riceve 503 busy e non entra.

    [Fact]
    public async Task AFullQueueAnswersBusyInsteadOfGrowingWithoutBound()
    {
        HoldPipeline pipeline = new();
        DictationExecutor executor = new(() => pipeline, () => true, () => false, 5);

        // Otto richieste avviate di fila: la prima prende il gate e resta dentro, la
        // seconda e la terza aspettano, le altre trovano il conteggio oltre il tetto e
        // ricevono busy subito. Avviandole in ordine il risultato è deterministico: il
        // percorso è sincrono fino all'attesa del gate.
        Task<ExecutorOutcome>[] running = Enumerable.Range(0, 8)
            .Select(_ => executor.RunAsync(new MemoryStream(Wav16k(0.5)), "default", default))
            .ToArray();
        await pipeline.Entered.WaitAsync(TimeSpan.FromSeconds(5));

        // Le rifiutate sono già concluse: nessuna ha toccato il gate né la pipeline.
        ExecutorOutcome[] rejected = running
            .Where(task => task.IsCompleted)
            .Select(task => task.Result)
            .ToArray();
        Assert.NotEmpty(rejected);
        Assert.All(rejected, outcome =>
        {
            Assert.False(outcome.IsSuccess);
            Assert.Equal("busy", outcome.Error!.Error);
            Assert.Equal(503, outcome.Error.Status);
        });

        // Al gate è arrivata solo la prima: le altre due ammesse stanno aspettando il gate
        // e non hanno ancora toccato la pipeline.
        Assert.Equal(1, pipeline.Calls);

        pipeline.Release();
        ExecutorOutcome[] outcomes = await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(outcomes, outcome => outcome.IsSuccess);
        Assert.Equal(rejected.Length, outcomes.Count(outcome => outcome.Error?.Error == "busy"));

        // Le tre ammesse sono passate davvero dalla pipeline: il tetto non ha ammesso
        // richieste che poi non sono state servite.
        Assert.Equal(3, pipeline.Calls);

        // Contatore e gate sono entrambi tornati a posto: se il contatore fosse rimasto
        // alto questa richiesta sarebbe rifiutata, se il gate fosse rimasto preso
        // resterebbe appesa fino al timeout.
        ExecutorOutcome after = await executor
            .RunAsync(new MemoryStream(Wav16k(0.5)), "default", default)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(after.IsSuccess);
        Assert.Equal(4, pipeline.Calls);
    }

    [Fact]
    public async Task AFullQueueAnswersBusyEvenDuringWarmUp()
    {
        // Modelli non pronti con un caricamento in corso: l'attesa di disponibilità dura
        // tutta la finestra, qui cinque secondi. Se il tetto contasse solo la coda del gate,
        // tutte e otto le richieste verrebbero ammesse e ognuna resterebbe in memoria con il
        // proprio PCM decodificato per tutto il warm-up: nessun tetto proprio dove la memoria
        // cresce. Il rifiuto invece arriva all'ingresso, prima di decodificare.
        int ready = 0;
        SlowPipeline pipeline = new();
        DictationExecutor executor = new(
            () => pipeline, () => Volatile.Read(ref ready) != 0, () => true, 5);

        Task<ExecutorOutcome>[] running = Enumerable.Range(0, 8)
            .Select(_ => executor.RunAsync(new MemoryStream(Wav16k(0.5)), "default", default))
            .ToArray();

        // Le rifiutate sono già concluse mentre le ammesse stanno ancora aspettando i
        // modelli: è la prova che il tetto è scattato prima della decodifica e prima
        // dell'attesa di disponibilità, non dopo.
        ExecutorOutcome[] rejected = running
            .Where(task => task.IsCompleted)
            .Select(task => task.Result)
            .ToArray();
        Assert.NotEmpty(rejected);
        Assert.Contains(running, task => !task.IsCompleted);
        Assert.All(rejected, outcome =>
        {
            Assert.False(outcome.IsSuccess);
            Assert.Equal("busy", outcome.Error!.Error);
            Assert.Equal(503, outcome.Error.Status);
        });

        // I modelli diventano pronti: le tre ammesse escono dal warm-up e passano dalla
        // pipeline, nessuna resta appesa a un contatore che non torna.
        Volatile.Write(ref ready, 1);
        ExecutorOutcome[] outcomes = await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(5, rejected.Length);
        Assert.Equal(5, outcomes.Count(outcome => outcome.Error?.Error == "busy"));
        Assert.Equal(3, outcomes.Count(outcome => outcome.IsSuccess));
        Assert.Equal(3, pipeline.Calls);
    }

    // ---- Il 500 lascia una traccia nel log; i rifiuti previsti no.

    [Fact]
    public async Task AnInternalErrorIsLoggedButTheResponseStaysGeneric()
    {
        List<(string Code, string Message)> entries = [];
        ThrowingPipeline pipeline = new(new InvalidDataException("boom"));
        DictationExecutor executor = new(() => pipeline, () => true, () => false, 1,
            log: (code, message) => entries.Add((code, message)));

        ExecutorOutcome outcome = await executor
            .RunAsync(new MemoryStream(Wav16k(0.5)), "default", default)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("internal_error", outcome.Error!.Error);
        Assert.Equal(500, outcome.Error.Status);
        Assert.DoesNotContain("boom", outcome.Error.Message);
        Assert.DoesNotContain("InvalidDataException", outcome.Error.Message);

        (string code, string message) = Assert.Single(entries);
        Assert.Equal("api_internal_error", code);
        Assert.Contains("InvalidDataException", message);
        Assert.Contains("boom", message);
    }

    [Fact]
    public async Task AFormatRejectionIsNotLogged()
    {
        List<(string Code, string Message)> entries = [];
        DictationExecutor executor = new(() => new SlowPipeline(), () => true, () => false, 1,
            log: (code, message) => entries.Add((code, message)));

        ExecutorOutcome outcome = await executor.RunAsync(
            new MemoryStream("non audio"u8.ToArray()), "default", default);

        Assert.Equal(415, outcome.Error!.Status);
        Assert.Empty(entries);
    }

    [Fact]
    public async Task ATooLargeAndALoadingRejectionAreNotLoggedEither()
    {
        List<(string Code, string Message)> entries = [];
        DictationExecutor tooLarge = new(() => new SlowPipeline(), () => true, () => false, 1,
            maxInputBytes: 1024, log: (code, message) => entries.Add((code, message)));
        // Nessun caricamento in corso: la richiesta riceve il 503 senza attendere.
        DictationExecutor notReady = new(() => new SlowPipeline(), () => false, () => false, 1,
            log: (code, message) => entries.Add((code, message)));

        // 413 e 503 sono esiti previsti come il 415. Oggi il log sta solo sul ramo del 500,
        // ma un refactor che spostasse la registrazione più a monte comincerebbe a scrivere
        // nel file di log il traffico normale: questi due casi lo impediscono in silenzio.
        ExecutorOutcome tooLargeOutcome = await tooLarge
            .RunAsync(new MemoryStream(TooLargeStream()), "default", default);
        ExecutorOutcome loadingOutcome = await notReady
            .RunAsync(new MemoryStream(Wav16k(0.5)), "default", default);

        Assert.Equal(413, tooLargeOutcome.Error!.Status);
        Assert.Equal(503, loadingOutcome.Error!.Status);
        Assert.Empty(entries);
    }

    /// <summary>Pipeline che trattiene il buffer ricevuto: serve a provare che l'esito porta lo stesso.</summary>
    private sealed class CapturingPipeline : IInferencePipeline
    {
        public ReadOnlyMemory<byte> Received { get; private set; }

        public Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct)
        {
            Received = request.Pcm16;
            return Task.FromResult(Canned());
        }

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Pipeline che conta le chiamate concorrenti: serve a provare il gate.</summary>
    private sealed class SlowPipeline : IInferencePipeline
    {
        private int _running;
        private int _calls;
        private int _maximum;

        public int Calls => Volatile.Read(ref _calls);

        public int MaximumConcurrent => Volatile.Read(ref _maximum);

        public async Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            int running = Interlocked.Increment(ref _running);
            try
            {
                TrackMaximum(running);
                await Task.Delay(50, ct);
                return Canned();
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void TrackMaximum(int running)
        {
            int seen = Volatile.Read(ref _maximum);
            while (running > seen)
            {
                int original = Interlocked.CompareExchange(ref _maximum, running, seen);
                if (original == seen)
                {
                    return;
                }

                seen = original;
            }
        }
    }

    /// <summary>
    /// Pipeline che segnala quando è entrata e poi aspetta: serve a cancellare una
    /// richiesta mentre tiene il gate.
    /// </summary>
    private sealed class SignallingPipeline : IInferencePipeline
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task Entered => _entered.Task;

        public int Calls => Volatile.Read(ref _calls);

        public async Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _entered.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Canned();
        }

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Pipeline che fallisce alla prima chiamata e poi funziona: serve a provare che
    /// un'eccezione diventa un 500 generico e non lascia il gate occupato.
    /// </summary>
    private sealed class FailOncePipeline : IInferencePipeline
    {
        /// <summary>Testo che non deve comparire nella risposta: simula un dettaglio interno.</summary>
        public const string Secret = "transcribe.cpp:42 asserted";

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public async Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                await Task.Delay(20, ct);
                throw new InvalidOperationException(Secret);
            }

            return Canned();
        }

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Pipeline che tiene occupato il gate finché il test non apre la porta: serve a
    /// riempire la coda e a provare il rifiuto per coda piena. Le chiamate successive alla
    /// prima tornano subito, così dopo il rilascio le richieste in attesa finiscono.
    /// </summary>
    private sealed class HoldPipeline : IInferencePipeline
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public Task Entered => _entered.Task;

        public int Calls => Volatile.Read(ref _calls);

        /// <summary>Apre la porta: la richiesta che tiene il gate può uscire.</summary>
        public void Release() => _release.TrySetResult();

        public async Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                _entered.TrySetResult();
                await _release.Task.WaitAsync(ct);
            }

            return Canned();
        }

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Pipeline che lancia sempre l'eccezione indicata: serve a provare cosa finisce nel
    /// log (il dettaglio) e cosa resta nella risposta (niente).
    /// </summary>
    private sealed class ThrowingPipeline(Exception error) : IInferencePipeline
    {
        public Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct) =>
            Task.FromException<PipelineResult>(error);

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static PipelineResult Canned() => new("ciao", "ciao", 10, 5, 20, [],
        new EngineIdentity("stt", "rev", "sha", "runtime", "cpu", "q8", "cpu"), Cleaner: null);
}
