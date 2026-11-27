namespace GangioKeet.Client.Api;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recording;
using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Il server HTTP compatibile OpenAI che gira dentro l'app di tray: Kestrel sullo stesso
/// processo della pipeline, così una dettatura da un altro programma costa lo stesso
/// modello già caricato invece di una seconda copia dei pesi.
///
/// Il server non decide niente da solo: i limiti arrivano da <see cref="ApiConfig"/>, il
/// token da <see cref="ApiTokenPolicy"/> (qui arriva già risolto: questo file non legge
/// l'ambiente), le regole del contratto da <see cref="DictationContract"/> e il lavoro
/// sulla pipeline da <see cref="DictationExecutor"/>. Quello che vive solo qui è ciò che
/// esiste solo avendo una porta: la forma delle risposte HTTP, il controllo del token e
/// l'indirizzo reale scelto dal sistema operativo.
///
/// L'avvio non è mai obbligatorio per l'app: se la porta è occupata o l'indirizzo non è
/// valido, <see cref="Start"/> lancia e il chiamante registra l'errore e prosegue senza
/// server. La dettatura locale è il lavoro dell'app; l'API è un di più.
/// </summary>
public sealed class DictationServer : IAsyncDisposable
{
    /// <summary>L'unica rotta senza token: dice se il server c'è e se i modelli sono pronti.</summary>
    private const string HealthPath = "/health";

    /// <summary>Elenco dei modelli: c'è un solo alias, quello configurato.</summary>
    private const string ModelsPath = "/v1/models";

    /// <summary>Trascrizione nel formato dei client OpenAI (multipart con <c>file</c>).</summary>
    private const string TranscriptionPath = "/v1/audio/transcriptions";

    /// <summary>Dettatura completa: audio nel corpo, profilo di pulizia facoltativo.</summary>
    private const string DictationPath = "/v1/dictation";

    /// <summary>
    /// Correzione testo→testo: le stesse classi deterministiche della pipeline locale, per
    /// le app che dettano con un proprio motore e non possono mandare audio.
    /// </summary>
    private const string CorrectPath = "/v1/correct";

    /// <summary>Tipo delle risposte JSON. Il charset esplicito evita che un client indovini.</summary>
    private const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>Tipo della risposta con <c>response_format=text</c>.</summary>
    private const string TextContentType = "text/plain; charset=utf-8";

    /// <summary>Intestazione con il profilo di pulizia di <see cref="DictationPath"/>.</summary>
    private const string ProfileHeader = "X-Dictation-Profile";

    /// <summary>Intestazione alternativa al bearer, quella dei client che usano <c>api_key</c>.</summary>
    private const string ApiKeyHeader = "X-Api-Key";

    /// <summary>Schema <c>Authorization</c> standard; il confronto sullo schema ignora il caso.</summary>
    private const string BearerPrefix = "Bearer ";

    /// <summary>Codice dei rifiuti che non hanno una factory propria (corpo o multipart illeggibile).</summary>
    private const string InvalidRequestCode = ApiError.InvalidRequestCode;

    /// <summary>Byte del BOM UTF-8: un client che lo manda non ha scritto un JSON sbagliato.</summary>
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Decodificatore che rifiuta i byte non validi invece di sostituirli con U+FFFD: una
    /// dettatura corrotta deve essere un 400, non un 200 con un testo che nessuno ha scritto.
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Quanto Kestrel aspetta le richieste in corso allo spegnimento. Corto di proposito:
    /// una dettatura interrotta è meglio di un'app che non si chiude, e il chiamante HTTP
    /// riceve comunque la connessione chiusa.
    /// </summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Margine del limite del multipart sopra <see cref="ApiConfig.MaxRequestBytes"/>. I due
    /// limiti misurano quasi la stessa cosa (il corpo della richiesta), quindi lasciarli
    /// uguali renderebbe indecidibile quale scatta per primo: con il limite del form più
    /// alto vince sempre Kestrel, e l'upload troppo grande è un 413 deterministico invece
    /// di un 413 oppure un 400 a seconda del giro.
    /// </summary>
    private const long MultipartLimitSlack = 1024;

    /// <summary>Opzioni di serializzazione delle risposte d'errore: nomi minuscoli, come il resto dell'API.</summary>
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private readonly WebApplication _application;
    private readonly Action<string, string> _log;
    private readonly object _gate = new();
    private Task? _stopTask;

    private DictationServer(WebApplication application, string baseUrl, Action<string, string> log)
    {
        _application = application;
        BaseUrl = baseUrl;
        _log = log;
    }

    /// <summary>
    /// Indirizzo con cui raggiungere il server, per esempio <c>http://127.0.0.1:54321</c>.
    /// Con <see cref="ApiConfig.Port"/> a zero la porta la sceglie il sistema e questo è
    /// l'unico posto dove si può leggerla: viene da <c>app.Urls</c> dopo l'avvio, mai dalla
    /// configurazione. Non contiene mai il token.
    /// </summary>
    public string BaseUrl { get; }

    /// <summary>
    /// Avvia Kestrel e restituisce il server già in ascolto. Bloccante: il chiamante è il
    /// thread UI di WPF e non c'è un posto dove mettere un'operazione asincrona senza
    /// rischiare un deadlock sullo startup del tray.
    /// </summary>
    /// <param name="token">
    /// Token già risolto da <see cref="ApiTokenPolicy"/>. <c>null</c> significa «ascolto
    /// solo su loopback»: nessuna autenticazione, perché non c'è rete da proteggere.
    /// </param>
    /// <param name="pipeline">
    /// La pipeline corrente, letta a ogni richiesta e non catturata: il runtime che la
    /// possiede può essere sostituito (o smontato) mentre il server resta in ascolto, e un
    /// riferimento fissato qui diventerebbe quello di un oggetto già liberato.
    /// <c>null</c> significa «modelli non ancora caricati» ed è un 503 da riprovare.
    /// </param>
    /// <param name="recordings">Destinazione delle registrazioni; <c>null</c> per non registrarle.</param>
    /// <param name="policy">
    /// La policy di pulizia già caricata dal chiamante: contiene glossario, override fonetici
    /// ed elisioni, cioè le regole che <c>/v1/correct</c> applica. Il server non la carica e
    /// non la rilegge: un file che cambia sotto una rotta in ascolto renderebbe due richieste
    /// dello stesso testo diverse senza che nessuno lo abbia chiesto.
    /// </param>
    /// <param name="log">Riceve codice e messaggio dei log dell'app (il file).</param>
    /// <exception cref="ArgumentException">La configurazione non contiene indirizzi validi.</exception>
    /// <exception cref="InvalidOperationException">
    /// L'avvio è stato rifiutato (un indirizzo non loopback senza token), oppure è fallito
    /// (porta occupata, indirizzo non disponibile) o Kestrel non ha riportato alcun
    /// indirizzo. Il chiamante deve proseguire senza server.
    /// </exception>
    public static DictationServer Start(ApiConfig config, Func<IInferencePipeline?> pipeline,
        Func<bool> isReady, Func<bool> isWarmingUp, string? token, IRecordingSink? recordings,
        CleanupPolicy policy, Action<string, string> log)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(isReady);
        ArgumentNullException.ThrowIfNull(isWarmingUp);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(log);
        if (config.Listen.Length == 0)
        {
            throw new ArgumentException("api.listen non contiene alcun indirizzo.", nameof(config));
        }

        // Una porta oltre il loopback senza token esporrebbe il microfono e il motore ASR
        // a chiunque sia in rete. ApiTokenPolicy lo impedisce già al chiamante, ma questa
        // classe non può fidarsi dell'argomento che riceve: un chiamante futuro che saltasse
        // la regola troverebbe qui il rifiuto, prima che Kestrel apra il socket.
        if (string.IsNullOrEmpty(token) && config.Listen.Any(address => !IsLoopback(address)))
        {
            throw new InvalidOperationException(
                "api.listen apre la rete senza autenticazione: un indirizzo non loopback "
                + "richiede un token. Nessuna porta viene aperta.");
        }

        Action<string, string> safeLog = (code, message) => WriteLog(log, code, message);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            // Mai Development: la pagina degli errori di sviluppo mostrerebbe gli interni
            // al chiamante HTTP, che qui può stare su un'altra macchina.
            EnvironmentName = Environments.Production,
        });

        // L'app è WinExe: nessuna console e nessun provider di default. I log di Kestrel e
        // di ASP.NET passano dallo stesso file dell'app, come tutto il resto; quelli sotto
        // Warning (ogni richiesta) sono rumore e resterebbero fuori da ogni diagnosi.
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddProvider(new ForwardingLoggerProvider(safeLog));

        // Il tetto del corpo vale due volte: Kestrel lo applica alla richiesta (compreso un
        // upload chunked senza Content-Length), il lettore del multipart a un valore appena
        // più alto, così è sempre Kestrel a fermare un corpo troppo grande (413) e non il
        // lettore del form (400).
        builder.Services.Configure<FormOptions>(options =>
            options.MultipartBodyLengthLimit = config.MaxRequestBytes + MultipartLimitSlack);

        long bodyLimit = config.MaxRequestBytes;
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = bodyLimit;
            foreach (string address in config.Listen)
            {
                options.Listen(ParseAddress(address), config.Port);
            }
        });

        WebApplication application = builder.Build();
        DictationExecutor executor = new(pipeline, isReady, isWarmingUp, config.WaitSeconds,
            config.MaxAudioSeconds, config.MaxRequestBytes, safeLog);

        string alias = config.ModelAlias;
        CancellationToken lifetime = application.Lifetime.ApplicationStopping;
        long maxBytes = config.MaxRequestBytes;

        // Ultima rete: un'eccezione che sfugge a un gestore deve uscire come il JSON
        // d'errore documentato, non come un 500 dal corpo vuoto che nessun client sa
        // leggere. Sta prima del controllo del token, così copre anche quel percorso.
        application.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception error) when (error is not OutOfMemoryException
                && !(error is OperationCanceledException && context.RequestAborted.IsCancellationRequested))
            {
                WriteLog(safeLog, "api_unhandled_error",
                    $"Richiesta API non gestita: {error.GetType().Name}: {error.Message}");
                if (context.Response.HasStarted)
                {
                    // La risposta è già partita: non c'è più un corpo da sostituire.
                    throw;
                }

                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = JsonContentType;
                await context.Response.WriteAsync(JsonSerializer.Serialize(ApiError.Internal(
                    "Richiesta non riuscita per un errore interno del server."), WebJson))
                    .ConfigureAwait(false);
            }
        });

        if (token is not null)
        {
            application.Use(async (context, next) =>
            {
                // Solo /health è pubblica: è la domanda «ci sei?» di chi non ha ancora la
                // chiave, e la risposta non contiene audio, testo né percorsi. Una sola
                // barra finale non cambia la rotta (un client con la base URL che finisce
                // con "/" chiede "/health/") e non deve trasformare il probe in un 401.
                if (IsHealthPath(context.Request.Path))
                {
                    await next();
                    return;
                }

                if (IsAuthorized(context.Request, token))
                {
                    await next();
                    return;
                }

                // Nel log va chi ha chiamato, mai l'intestazione presentata: un token
                // sbagliato è quasi sempre quello vero con un carattere di troppo.
                safeLog("api_unauthorized",
                    $"Richiesta rifiutata senza token valido da {RemoteAddress(context)}.");
                await RejectAsync(context, ApiError.Unauthorized(
                    "Token mancante o non valido: usa 'Authorization: Bearer <token>' "
                    + "oppure 'X-Api-Key'."));
            });
        }

        application.MapGet(HealthPath, () => Health(isReady, isWarmingUp, alias));
        application.MapGet(ModelsPath, () => Models(alias));
        // I gestori sono delegati tipizzati e non lambda nude: un lambda che prende solo
        // HttpContext è convertibile anche a RequestDelegate (Task<IResult> è un Task), e
        // quella scelta scarterebbe l'IResult rispondendo 200 con il corpo vuoto.
        Func<HttpContext, Task<IResult>> transcribe = context => TranscribeAsync(
            context, alias, executor, recordings, maxBytes, safeLog, lifetime);
        Func<HttpContext, Task<IResult>> dictate = context => DictateAsync(
            context, alias, executor, recordings, maxBytes, safeLog, lifetime);
        // La correzione non prende né l'executor né la pipeline: non tocca i pesi, quindi non
        // consuma la coda di inferenza né aspetta una trascrizione in corso.
        Func<HttpContext, Task<IResult>> correct = context => CorrectAsync(
            context, policy, maxBytes, safeLog);
        application.MapPost(TranscriptionPath, transcribe);
        application.MapPost(DictationPath, dictate);
        application.MapPost(CorrectPath, correct);

        try
        {
            // Task.Run: StartAsync non deve poter riprendere sul contesto di
            // sincronizzazione del thread UI, dove il chiamante sta bloccando.
            Task.Run(() => application.StartAsync()).GetAwaiter().GetResult();
        }
        catch
        {
            // Un server che non è partito non deve restare mezzo vivo con i suoi servizi.
            // Se anche la pulizia fallisce, l'errore che conta è quello dell'avvio.
            try
            {
                application.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }

            throw;
        }

        string baseUrl = BaseUrlFrom(application);
        safeLog("api_listening", $"Server API in ascolto su {baseUrl}.");
        return new DictationServer(application, baseUrl, safeLog);
    }

    /// <summary>
    /// Ferma Kestrel e libera la porta. Idempotente: la via di emergenza di <c>OnExit</c>
    /// può arrivare dopo lo spegnimento normale, e un secondo stop non deve lanciare.
    /// </summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    /// <summary>Come <see cref="StopAsync"/>: il server si ferma quando finisce lo scope.</summary>
    public ValueTask DisposeAsync() => new(StopAsync());

    private async Task StopCoreAsync()
    {
        try
        {
            // Fermare il server PRIMA del runtime dei modelli è responsabilità del
            // chiamante: qui si chiude solo l'ingresso HTTP e si attendono le richieste
            // in corso, così nessuna può più toccare una pipeline in smontaggio.
            await _application.StopAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            WriteLog(_log, "api_stop_error", $"Arresto del server API non riuscito: "
                + $"{error.GetType().Name}: {error.Message}");
        }

        try
        {
            // Il dispose libera i servizi e il socket. Anche qui: lo spegnimento
            // dell'app non deve mai fallire per colpa del server HTTP, ma il guasto
            // resta nel log.
            await _application.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            WriteLog(_log, "api_dispose_error",
                $"Rilascio del server API non riuscito: {error.GetType().Name}: {error.Message}");
        }
    }

    private static IResult Health(Func<bool> isReady, Func<bool> isWarmingUp, string alias) =>
        Results.Json(new
        {
            status = "ok",
            ready = isReady(),
            warming_up = isWarmingUp(),
            model = alias,
        });

    /// <summary>
    /// Vero per <see cref="HealthPath"/> con o senza una barra finale. Si toglie una sola
    /// barra: <c>/health//</c> non è una scorciatoia che vale la pena accettare, e comunque
    /// non è una rotta mappata.
    /// </summary>
    private static bool IsHealthPath(PathString path)
    {
        string? value = path.Value;
        if (value is { Length: > 1 } && value[^1] == '/')
        {
            value = value[..^1];
        }

        return string.Equals(value, HealthPath, StringComparison.OrdinalIgnoreCase);
    }

    private static IResult Models(string alias) => Results.Json(new
    {
        @object = "list",
        data = new[] { new { id = alias, @object = "model", owned_by = "chattyneko" } },
    });

    /// <summary>
    /// <c>POST /v1/audio/transcriptions</c>. Il multipart si legge con
    /// <c>ReadFormAsync</c> e non con il binding <c>[FromForm]</c>: in .NET 8+ quel binding
    /// passa dal middleware antiforgery e senza token CSRF fallisce, il che per un'API con
    /// token proprio sarebbe solo un altro modo di dire 400.
    /// </summary>
    private static async Task<IResult> TranscribeAsync(HttpContext context, string alias,
        DictationExecutor executor, IRecordingSink? recordings, long maxBytes,
        Action<string, string> log, CancellationToken lifetime)
    {
        // ReadFormAsync lancerebbe InvalidOperationException su un corpo non multipart, e
        // senza questa guardia il chiamante riceverebbe un 500 dal corpo vuoto invece del
        // JSON documentato. La rotta gemella accetta il corpo binario; questa no, e il 400
        // deve dirlo.
        if (!context.Request.HasFormContentType)
        {
            return Error(ApiError.BadRequest(InvalidRequestCode,
                "Questa rotta vuole 'multipart/form-data' con il campo 'file'. "
                + "Il corpo binario è accettato da /v1/dictation."));
        }

        IFormCollection form;
        try
        {
            form = await context.Request.ReadFormAsync(context.RequestAborted);
        }
        catch (Exception error) when (IsRequestFailure(error))
        {
            return Error(RequestFailure(error, maxBytes));
        }

        // Le chiavi presenti, non il contenuto: il contratto decide cosa ammettere.
        string[] keys = [.. form.Keys];
        ApiError? invalid = DictationContract.ValidateTranscription(alias, keys, key => form[key].ToString());
        if (invalid is not null)
        {
            return Error(invalid);
        }

        IFormFile? file = form.Files.GetFile("file");
        if (file is null)
        {
            return Error(ApiError.BadRequest(InvalidRequestCode,
                "Manca il campo 'file' con l'audio da trascrivere."));
        }

        byte[] audio;
        try
        {
            using Stream source = file.OpenReadStream();
            audio = await ReadBoundedAsync(source, maxBytes, context.RequestAborted);
        }
        catch (Exception error) when (IsRequestFailure(error))
        {
            return Error(RequestFailure(error, maxBytes));
        }

        // Il profilo non è un parametro di questa rotta: la forma OpenAI non lo prevede e
        // un profilo in più sarebbe un parametro che il client crede di controllare.
        ExecutorOutcome? outcome = await RunAsync(executor, audio, "default", context, lifetime);
        if (outcome is null)
        {
            // Solo il chiamante andato via: non c'è nessuno a cui rispondere. Lo
            // spegnimento dell'app arriva qui come 503 model_loading, non come 200 vuoto.
            return Results.Empty;
        }

        if (!outcome.IsSuccess)
        {
            return Error(outcome.Error!);
        }

        PipelineResult result = outcome.Result!;
        Record(recordings, outcome.Pcm16, result, "default", context, log);
        WriteLog(log, "api_turn",
            $"Trascrizione servita in {result.TotalMs:0} ms (profilo default, {audio.Length} byte).");

        if (DictationContract.WantsPlainText(keys, key => form[key].ToString()))
        {
            return Results.Text(result.FinalText, TextContentType);
        }

        return Results.Text(
            DictationContract.ToJson(result.FinalText, DictationContract.NoSpeech(result.RawText)),
            JsonContentType);
    }

    /// <summary>
    /// <c>POST /v1/dictation</c>: audio come corpo binario oppure multipart con <c>file</c>,
    /// profilo facoltativo. Precedenza delle fonti del profilo: campo multipart
    /// <c>profile</c> (il più esplicito), poi l'intestazione <see cref="ProfileHeader"/>,
    /// poi <c>?profile=</c>; vince la prima che porta un valore. Il profilo non viaggia nel
    /// corpo JSON perché il corpo è occupato dall'audio.
    /// Con l'audio multipart valgono gli stessi rifiuti di <c>/v1/audio/transcriptions</c>
    /// (<see cref="DictationContract.ValidateDictation"/>): un <c>model</c>, <c>language</c>,
    /// <c>prompt</c> o <c>temperature</c> che questa rotta non applica è un 400, non un 200.
    /// </summary>
    private static async Task<IResult> DictateAsync(HttpContext context, string alias,
        DictationExecutor executor, IRecordingSink? recordings, long maxBytes,
        Action<string, string> log, CancellationToken lifetime)
    {
        string? requestedProfile = context.Request.Headers[ProfileHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(requestedProfile))
        {
            requestedProfile = context.Request.Query["profile"].FirstOrDefault();
        }

        byte[] audio;
        if (context.Request.HasFormContentType)
        {
            IFormCollection form;
            try
            {
                form = await context.Request.ReadFormAsync(context.RequestAborted);
            }
            catch (Exception error) when (IsRequestFailure(error))
            {
                return Error(RequestFailure(error, maxBytes));
            }

            // Gli stessi rifiuti dell'altra rotta: i campi che cambierebbero il
            // comportamento venivano letti e buttati via, e il 200 diceva al chiamante che
            // la sua scelta era passata. `profile` è il campo proprio di questa rotta.
            ApiError? invalid = DictationContract.ValidateDictation(alias, [.. form.Keys],
                key => form[key].ToString());
            if (invalid is not null)
            {
                return Error(invalid);
            }

            IFormFile? file = form.Files.GetFile("file");
            if (file is null)
            {
                return Error(ApiError.BadRequest(InvalidRequestCode,
                    "Manca il campo 'file' con l'audio (oppure manda l'audio come corpo binario)."));
            }

            string? fromForm = form["profile"].ToString();
            if (!string.IsNullOrWhiteSpace(fromForm))
            {
                requestedProfile = fromForm;
            }

            try
            {
                using Stream source = file.OpenReadStream();
                audio = await ReadBoundedAsync(source, maxBytes, context.RequestAborted);
            }
            catch (Exception error) when (IsRequestFailure(error))
            {
                return Error(RequestFailure(error, maxBytes));
            }
        }
        else
        {
            try
            {
                audio = await ReadBoundedAsync(context.Request.Body, maxBytes, context.RequestAborted);
            }
            catch (Exception error) when (IsRequestFailure(error))
            {
                return Error(RequestFailure(error, maxBytes));
            }
        }

        string profile;
        try
        {
            profile = DictationContract.ProfileFrom(requestedProfile);
        }
        catch (ArgumentException error)
        {
            // Il messaggio nomina i profili ammessi e il valore ricevuto: è la correzione.
            return Error(ApiError.UnsupportedProfile(error.Message));
        }

        ExecutorOutcome? outcome = await RunAsync(executor, audio, profile, context, lifetime);
        if (outcome is null)
        {
            // Solo il chiamante andato via: non c'è nessuno a cui rispondere. Lo
            // spegnimento dell'app arriva qui come 503 model_loading, non come 200 vuoto.
            return Results.Empty;
        }

        if (!outcome.IsSuccess)
        {
            return Error(outcome.Error!);
        }

        PipelineResult result = outcome.Result!;
        Record(recordings, outcome.Pcm16, result, profile, context, log);
        WriteLog(log, "api_turn",
            $"Dettatura servita in {result.TotalMs:0} ms (profilo {profile}, {audio.Length} byte).");

        return Results.Text(DictationContract.ToDictationJson(result), JsonContentType);
    }

    /// <summary>
    /// <c>POST /v1/correct</c>: testo nel corpo JSON, testo corretto nella risposta. Le fasi
    /// sono quelle della pipeline locale nello stesso ordine — <see cref="LightCleanup.Apply"/>,
    /// <see cref="GlossaryNormalizer.Normalize"/>, <see cref="DictationCorrections.Apply"/> —
    /// così le due strade non divergono. Le regole di profilo restano dentro quelle classi:
    /// qui si valida il nome del profilo e lo si passa, senza duplicare nessuna regola.
    /// Non tocca i pesi e non passa da <see cref="DictationExecutor"/>: non consuma la coda di
    /// inferenza e non aspetta una trascrizione in corso, quindi risponde anche mentre i
    /// modelli stanno caricando (o non ci sono affatto).
    /// Il corpo si rifiuta prima di copiarlo quando il <c>Content-Length</c> dichiara più del
    /// caso peggiore UTF-8 del limite, e si decodifica in modo rigoroso: un corpo non UTF-8 è
    /// un 400, non un testo con dei U+FFFD al posto dei byte rotti.
    /// </summary>
    private static async Task<IResult> CorrectAsync(HttpContext context, CleanupPolicy policy,
        long maxBytes, Action<string, string> log)
    {
        // Pre-controllo sul Content-Length: un corpo oltre il caso peggiore di JSON escapato
        // (\uXXXX, sei byte per carattere) non può contenere un testo ammesso, e leggerlo
        // significherebbe copiare e analizzare fino a 25 MB per poi rispondere 400. Una
        // richiesta chunked non ha questo numero e segue il percorso normale.
        if (context.Request.ContentLength is long declared
            && declared > DictationContract.MaxCorrectBodyBytes)
        {
            return Error(ApiError.BadRequest(InvalidRequestCode,
                $"Il corpo supera il limite di {DictationContract.MaxCorrectBodyBytes} byte "
                + $"per {DictationContract.MaxCorrectTextChars} caratteri di testo."));
        }

        byte[] body;
        try
        {
            body = await ReadBoundedAsync(context.Request.Body, maxBytes, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (IsRequestFailure(error))
        {
            // Il tetto dei byte lo applica Kestrel; questo è il caso difensivo in cui la
            // lettura fallisce comunque. Il codice è quello del contratto: `audio_too_large`
            // parlerebbe di un audio che questa rotta non riceve.
            return Error(ApiError.BadRequest(InvalidRequestCode,
                "Corpo della richiesta illeggibile o troppo grande."));
        }

        if (!TryDecodeUtf8(body, out string decoded))
        {
            return Error(ApiError.BadRequest(InvalidRequestCode,
                "Il corpo non è UTF-8 valido: il testo deve essere UTF-8."));
        }

        ApiError? invalid = DictationContract.CorrectFrom(decoded, out CorrectRequest? request);
        if (invalid is not null)
        {
            return Error(invalid);
        }

        // Le tre fasi della pipeline locale, nell'ordine in cui InferencePipeline le chiama:
        // tre passaggi espliciti così l'ordine si confronta con il brief a colpo d'occhio.
        string cleaned = LightCleanup.Apply(request!.Text);
        string normalized = GlossaryNormalizer.Normalize(cleaned, policy, request.Profile);
        string corrected = DictationCorrections.Apply(normalized, policy, request.Profile);

        // Nel log va la forma della richiesta, mai il testo: è la dettatura di qualcuno.
        WriteLog(log, "api_correct",
            $"Correzione servita (profilo {request.Profile}, {request.Text.Length} caratteri).");

        return Results.Text(DictationContract.ToCorrectJson(corrected), JsonContentType);
    }

    /// <summary>
    /// Decodifica il corpo in UTF-8 rigoroso, togliendo un BOM iniziale. Due casi che la
    /// decodifica tollerante nasconderebbe: il BOM, lasciato nella stringa, arriva a
    /// <c>JsonDocument.Parse</c> come primo carattere e fa fallire un JSON che è invece
    /// corretto; i byte non validi diventano U+FFFD e il chiamante riceve un 200 con un
    /// testo che non ha mai scritto. Il primo si toglie, il secondo è un errore del chiamante.
    /// </summary>
    private static bool TryDecodeUtf8(byte[] body, out string text)
    {
        ReadOnlySpan<byte> bytes = body;
        if (bytes.StartsWith(Utf8Bom))
        {
            bytes = bytes[Utf8Bom.Length..];
        }

        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    /// <summary>
    /// Esegue il turno con il token di annullamento del chiamante legato a quello
    /// dell'app: allo spegnimento la richiesta in corso si chiude invece di tenere fermo
    /// Kestrel fino al timeout. <c>null</c> significa «il chiamante se n'è andato»: non è
    /// un errore da servire, non c'è nessuno a cui rispondere. L'annullamento dell'app non è
    /// la stessa cosa: il chiamante è ancora lì ad aspettare, quindi riceve un 503
    /// <c>model_loading</c> invece di un 200 dal corpo vuoto.
    /// </summary>
    private static async Task<ExecutorOutcome?> RunAsync(DictationExecutor executor, byte[] audio,
        string profile, HttpContext context, CancellationToken lifetime)
    {
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime);
        try
        {
            return await executor.RunAsync(new MemoryStream(audio), profile, linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Lo spegnimento ha annullato il turno: la richiesta non è stata servita, e un
            // 200 vuoto direbbe al chiamante che la dettatura è riuscita.
            if (lifetime.IsCancellationRequested)
            {
                return ExecutorOutcome.Failure(ApiError.Loading(
                    "Il server è in spegnimento: la richiesta non è stata servita."));
            }

            return null;
        }
    }

    /// <summary>
    /// Mette il turno riuscito nella coda delle registrazioni, distinguendolo da una
    /// dettatura locale con <c>AppName == "api"</c>. Il PCM arriva dall'esito dell'executor:
    /// è lo stesso buffer che il turno ha già decodificato e passato alla pipeline, quindi
    /// registrare non costa una seconda decodifica. La registrazione non deve mai far
    /// fallire la risposta: il turno è già riuscito.
    /// </summary>
    private static void Record(IRecordingSink? recordings, ReadOnlyMemory<byte> pcm16,
        PipelineResult result, string profile, HttpContext context, Action<string, string> log)
    {
        if (recordings is null)
        {
            return;
        }

        try
        {
            // La provenienza HTTP sta in AppName; il titolo della finestra non esiste per
            // una richiesta di rete, quindi porta l'indirizzo di chi ha chiamato.
            StartMessage start = new("api", RemoteAddress(context), profile);
            recordings.TryEnqueue(pcm16, result, start);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            WriteLog(log, "api_recording_failed",
                $"Registrazione del turno API non riuscita: {error.GetType().Name}: {error.Message}");
        }
    }

    /// <summary>
    /// Copia il corpo a blocchi e si ferma appena supera il tetto: un upload chunked senza
    /// <c>Content-Length</c> non deve poter crescere in memoria fino all'esaurimento. Il
    /// limite vero resta quello di Kestrel; questo è il punto in cui il server può ancora
    /// rispondere con il proprio 413 invece di una connessione chiusa a metà.
    /// </summary>
    private static async Task<byte[]> ReadBoundedAsync(Stream source, long limit, CancellationToken ct)
    {
        using MemoryStream buffer = new();
        byte[] chunk = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new AudioTooLargeException($"Corpo troppo grande: il limite è {limit} byte.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Vero per il token giusto, presentato come <c>Authorization: Bearer</c> o come
    /// <c>X-Api-Key</c>. Il confronto è a tempo costante sui byte UTF-8: un confronto
    /// normale direbbe a chi prova quanti caratteri iniziali ha indovinato.
    /// </summary>
    private static bool IsAuthorized(HttpRequest request, string token)
    {
        string? presented = BearerToken(request.Headers.Authorization.FirstOrDefault())
            ?? request.Headers[ApiKeyHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(presented))
        {
            return false;
        }

        byte[] given = Encoding.UTF8.GetBytes(presented);
        byte[] expected = Encoding.UTF8.GetBytes(token);
        // FixedTimeEquals vuole due span della stessa lunghezza: su lunghezze diverse
        // risponde false, che è la risposta giusta.
        return CryptographicOperations.FixedTimeEquals(given, expected);
    }

    /// <summary>Valore dopo lo schema <c>Bearer</c>, oppure <c>null</c> se l'intestazione non lo è.</summary>
    private static string? BearerToken(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)
            || !header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string value = header[BearerPrefix.Length..].Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>Scrive un <see cref="ApiError"/> con il suo stato: una sola forma per ogni rifiuto.</summary>
    private static IResult Error(ApiError error) => Results.Text(
        JsonSerializer.Serialize(error, WebJson), JsonContentType, statusCode: error.Status);

    /// <summary>401 scritto sulla risposta del middleware, dove non c'è un <see cref="IResult"/> da tornare.</summary>
    private static async Task RejectAsync(HttpContext context, ApiError error)
    {
        context.Response.StatusCode = error.Status;
        context.Response.ContentType = JsonContentType;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsync(JsonSerializer.Serialize(error, WebJson)).ConfigureAwait(false);
    }

    /// <summary>
    /// Vero per i modi in cui la lettura di un corpo può fallire per colpa del chiamante:
    /// troppo grande, troppo lungo, malformato o interrotto.
    /// </summary>
    private static bool IsRequestFailure(Exception error) =>
        error is AudioTooLargeException or InvalidDataException or IOException or BadHttpRequestException;

    /// <summary>
    /// Traduce il guasto di lettura in un errore del server. Un corpo oltre il tetto —
    /// nostro (<see cref="AudioTooLargeException"/>) o di Kestrel
    /// (<see cref="BadHttpRequestException"/> con 413) — è 413; un multipart malformato o
    /// oltre il limite del form è 400. Confonderli direbbe al chiamante «ritenta più
    /// piccolo» quando il problema è la forma della richiesta.
    /// </summary>
    private static ApiError RequestFailure(Exception error, long maxBytes) => error switch
    {
        AudioTooLargeException => ApiError.TooLarge(error.Message),
        BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
            ApiError.TooLarge($"Corpo troppo grande: il limite è {maxBytes} byte."),
        _ => ApiError.BadRequest(InvalidRequestCode, "Corpo multipart illeggibile o incompleto."),
    };

    /// <summary>
    /// Indirizzo reale dopo l'avvio. Con <see cref="ApiConfig.Port"/> a zero la porta la
    /// sceglie il sistema: leggerla dalla configurazione darebbe un <see cref="BaseUrl"/>
    /// che punta a una porta chiusa. Un indirizzo jolly non è raggiungibile da un client,
    /// quindi <see cref="BaseUrl"/> riporta il loopback.
    /// </summary>
    private static string BaseUrlFrom(WebApplication application)
    {
        string? address = application.Urls.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new InvalidOperationException(
                "Kestrel non ha riportato alcun indirizzo: il server non è raggiungibile.");
        }

        return address.TrimEnd('/')
            .Replace("://0.0.0.0", "://127.0.0.1", StringComparison.Ordinal)
            .Replace("://[::]", "://[::1]", StringComparison.Ordinal)
            .Replace("://*", "://127.0.0.1", StringComparison.Ordinal);
    }

    /// <summary>
    /// Vero solo per gli indirizzi che restano sulla macchina. Il jolly e un IP qualsiasi non
    /// lo sono; un nome host nemmeno (qui non si risolve niente).
    /// </summary>
    private static bool IsLoopback(string? address) =>
        IPAddress.TryParse(address, out IPAddress? parsed) && IPAddress.IsLoopback(parsed);

    /// <summary>Indirizzo di ascolto di <see cref="ApiConfig.Listen"/> nella forma che vuole Kestrel.</summary>
    private static IPAddress ParseAddress(string address)
    {
        // Il jolly è ammesso dal token ma Kestrel non lo conosce: "*" è IPAddress.Any.
        if (string.Equals(address, "*", StringComparison.Ordinal))
        {
            return IPAddress.Any;
        }

        if (IPAddress.TryParse(address, out IPAddress? parsed))
        {
            return parsed;
        }

        throw new ArgumentException($"api.listen: indirizzo '{address}' non valido.", nameof(address));
    }

    /// <summary>
    /// Indirizzo di chi ha chiamato, per la provenienza della registrazione. Vuoto quando
    /// non esiste (trasporto in memoria): meglio un campo vuoto che un valore inventato.
    /// </summary>
    private static string RemoteAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "";

    /// <summary>Il log è best-effort: un delegate che lancia non deve far fallire una richiesta.</summary>
    private static void WriteLog(Action<string, string> log, string code, string message)
    {
        try
        {
            log(code, message);
        }
        catch
        {
            // Il file di log non è una dipendenza: se non si scrive, la dettatura funziona lo stesso.
        }
    }

    /// <summary>
    /// Porta i log di ASP.NET Core e di Kestrel nel log dell'app a partire da Warning: la
    /// console non esiste (app <c>WinExe</c>) e un guasto del server senza traccia sarebbe
    /// indebuggabile. Sotto Warning è traffico normale e non merita una riga per richiesta.
    /// </summary>
    private sealed class ForwardingLoggerProvider(Action<string, string> log) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ForwardingLogger(categoryName, log);

        public void Dispose()
        {
        }

        private sealed class ForwardingLogger(string category, Action<string, string> log) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (!IsEnabled(logLevel))
                {
                    return;
                }

                string message = formatter(state, exception);
                if (exception is not null)
                {
                    message += $" ({exception.GetType().Name}: {exception.Message})";
                }

                WriteLog(log, "api_server", $"{logLevel} [{category}] {message}");
            }
        }
    }
}
