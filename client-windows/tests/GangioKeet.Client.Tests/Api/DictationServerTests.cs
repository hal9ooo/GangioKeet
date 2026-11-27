using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using GangioKeet.Client.Api;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recording;
using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
using NAudio.Wave;
using Xunit;

namespace GangioKeet.Client.Tests.Api;

/// <summary>
/// Il server HTTP vero: socket veri e <see cref="HttpClient"/> vero. Questi test non
/// sostituiscono il contratto puro (già pinnato) né l'executor: verificano le cose che
/// si vedono solo aprendo una porta, cioè il token, la porta scelta dal sistema e la
/// forma delle risposte.
/// </summary>
public sealed class DictationServerTests
{
    private const string Alias = "gangio-keet";
    private const string Token = "test-token";

    /// <summary>Testo finale che la pipeline finta restituisce in ogni turno.</summary>
    private const string FinalText = "Testo finale del turno.";

    /// <summary>Testo grezzo, distinto dal finale: serve a provare che i due campi non si scambiano.</summary>
    private const string RawText = "testo grezzo del turno";

    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

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

    /// <summary>
    /// Configurazione dei test: loopback con porta 0, così il sistema sceglie una porta
    /// libera e due test non si contendono la stessa. Il tetto del corpo è basso per non
    /// rendere lenti i test, ma resta sopra l'audio che mandano.
    /// </summary>
    private static ApiConfig Config() => new()
    {
        Enabled = true,
        Listen = ["127.0.0.1"],
        Port = 0,
        ModelAlias = Alias,
        MaxRequestBytes = 1_048_576,
        MaxAudioSeconds = 30,
        WaitSeconds = 5,
    };

    private static DictationServer StartServer(Func<IInferencePipeline?> pipeline,
        IRecordingSink? recordings = null, CleanupPolicy? policy = null)
        => DictationServer.Start(Config(), pipeline, () => true, () => false, Token, recordings,
            policy ?? Policy(), (_, _) => { });

    /// <summary>
    /// Policy dei test: una regola fonetica e un'elisione, le stesse forme usate dai test
    /// delle classi di pulizia. Servono a provare che la rotta applica tutte e tre le fasi
    /// deterministiche — filler, glossario, correzioni — e non una copia delle regole.
    /// </summary>
    private static CleanupPolicy Policy() => new()
    {
        PhoneticOverrides = new() { ["ciper inference"] = "Cheaper Inference" },
        ElisionCorrections =
        [
            new()
            {
                From = "la gente",
                To = "l'agente",
                // La guardia vera della policy: in una frase che parla di persone la regola
                // non scatta, e la rotta deve rispettarla passando la policy così com'è.
                KeepWhenFollowedBy = ["comune"],
            },
        ],
    };

    /// <summary>Corpo JSON di <c>/v1/correct</c>; il profilo c'è solo se il test lo chiede.</summary>
    private static StringContent CorrectBody(string text, string? profile = null) =>
        new(JsonSerializer.Serialize(profile is null ? (object)new { text } : new { text, profile }),
            Encoding.UTF8, "application/json");

    /// <summary>Testo della risposta di <c>/v1/correct</c>.</summary>
    private static async Task<string> CorrectedTextAsync(HttpResponseMessage response)
    {
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("text").GetString()!;
    }

    /// <summary>
    /// Porta loopback che il sistema ha appena dato per libera. Serve ai test che devono
    /// nominare una porta esplicita (riuso dopo lo stop): con la porta 0 il secondo server
    /// prenderebbe una porta diversa e il test non proverebbe niente.
    /// </summary>
    private static int FreePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// Client già puntato al server, con il token quando il test lo chiede. Il server va
    /// fermato dal chiamante: la porta resta occupata finché non viene chiuso.
    /// </summary>
    private static HttpClient Client(DictationServer server, bool authenticated = true)
    {
        HttpClient client = new() { BaseAddress = new Uri(server.BaseUrl) };
        if (authenticated)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        }

        return client;
    }

    private static MultipartFormDataContent TranscriptionForm(string model, string? language = null)
    {
        MultipartFormDataContent form = new();
        ByteArrayContent file = new(Wav16k(0.25));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "detta.wav");
        form.Add(new StringContent(model), "model");
        if (language is not null)
        {
            form.Add(new StringContent(language), "language");
        }

        return form;
    }

    private static MultipartFormDataContent DictationForm(params (string Name, string Value)[] fields)
    {
        MultipartFormDataContent form = new();
        ByteArrayContent file = new(Wav16k(0.25));
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "detta.wav");
        foreach ((string name, string value) in fields)
        {
            form.Add(new StringContent(value), name);
        }

        return form;
    }

    /// <summary>Un turno su <c>/v1/dictation</c> con corpo binario, di cui serve solo il testo.</summary>
    private static async Task<string> DictationTextAsync(HttpClient client)
    {
        using ByteArrayContent audio = new(Wav16k(0.25));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        HttpResponseMessage response = await client.PostAsync("/v1/dictation", audio);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("text").GetString()!;
    }

    private static async Task<ApiError> ReadErrorAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<ApiError>(await response.Content.ReadAsStringAsync(), WebJson)!;

    [Fact]
    public async Task HealthAnswersWithoutAToken()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server, authenticated: false);

        // /health è l'unica rotta senza token: serve a chi deve sapere se il server c'è
        // prima di avere una chiave, e non deve rivelare niente dell'audio né dei modelli.
        HttpResponseMessage response = await client.GetAsync("/health");
        string body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"ready\":true", body);
        Assert.Contains(Alias, body);
    }

    [Fact]
    public async Task HealthAnswersWithATrailingSlashWithoutAToken()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server, authenticated: false);

        // Un client con la base URL che finisce con "/" chiede "/health/": è la stessa
        // rotta e non deve diventare un 401 solo per la barra finale, altrimenti il probe
        // di salute fallisce proprio nel caso più comune.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/")).StatusCode);

        // La barra in più non apre altre rotte: la regola vale solo per /health.
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/v1/models/")).StatusCode);
    }

    [Fact]
    public void StartRefusesANonLoopbackBindWithoutAToken()
    {
        // ApiTokenPolicy rifiuta già questa configurazione, ma il server non deve fidarsi
        // del suo argomento: un chiamante futuro che saltasse la regola aprirebbe il
        // microfono alla rete. Il rifiuto arriva prima di Kestrel, quindi nessuna porta.
        ApiConfig config = Config();
        config.Listen = ["0.0.0.0"];
        config.Port = FreePort();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = DictationServer.Start(config, () => new FixedPipeline(), () => true, () => false,
                token: null, recordings: null, policy: Policy(), log: (_, _) => { });
        });

        Assert.Contains("token", error.Message);
    }

    [Fact]
    public async Task ModelsListsOnlyTheAlias()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server);

        HttpResponseMessage response = await client.GetAsync("/v1/models");
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        JsonElement data = json.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetArrayLength());
        Assert.Equal(Alias, data[0].GetProperty("id").GetString());
    }

    [Fact]
    public async Task MissingTokenIs401()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server, authenticated: false);

        HttpResponseMessage response = await client.PostAsync("/v1/audio/transcriptions", null);
        ApiError error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", error.Error);
        Assert.Equal(401, error.Status);
    }

    [Fact]
    public async Task WrongTokenIs401()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server, authenticated: false);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sbagliato");

        // Token sbagliato e token assente devono avere la stessa risposta: un 403 diverso
        // direbbe a chi prova che la forma del token era quella giusta.
        HttpResponseMessage response = await client.PostAsync("/v1/audio/transcriptions", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", (await ReadErrorAsync(response)).Error);
    }

    [Fact]
    public async Task TranscriptionRejectsAnUnknownModel()
    {
        FixedPipeline pipeline = new();
        await using DictationServer server = StartServer(() => pipeline);
        using HttpClient client = Client(server);

        using MultipartFormDataContent form = TranscriptionForm("whisper-1");
        HttpResponseMessage response = await client.PostAsync("/v1/audio/transcriptions", form);
        ApiError error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("model_not_found", error.Error);
        // Il rifiuto arriva prima della pipeline: nessun turno è stato eseguito.
        Assert.Equal(0, pipeline.Calls);
    }

    [Fact]
    public async Task TranscriptionRejectsLanguage()
    {
        FixedPipeline pipeline = new();
        await using DictationServer server = StartServer(() => pipeline);
        using HttpClient client = Client(server);

        // Il server non applica la lingua: accettarla e ignorarla farebbe credere al
        // chiamante di aver controllato qualcosa.
        using MultipartFormDataContent form = TranscriptionForm(Alias, language: "it");
        HttpResponseMessage response = await client.PostAsync("/v1/audio/transcriptions", form);
        ApiError error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsupported_parameter", error.Error);
        Assert.Equal(0, pipeline.Calls);
    }

    [Fact]
    public async Task TranscriptionRejectsANonMultipartBodyAsInvalidRequest()
    {
        FixedPipeline pipeline = new();
        await using DictationServer server = StartServer(() => pipeline);
        using HttpClient client = Client(server);

        // Un `--data-binary` copiato dalla rotta gemella: ReadFormAsync lancerebbe
        // InvalidOperationException e il chiamante riceverebbe un 500 dal corpo vuoto.
        // Il 400 deve dirgli che questa rotta vuole il multipart.
        using ByteArrayContent audio = new(Wav16k(0.25));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        HttpResponseMessage response = await client.PostAsync("/v1/audio/transcriptions", audio);
        ApiError error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", error.Error);
        Assert.Contains("multipart/form-data", error.Message);
        Assert.Equal(0, pipeline.Calls);
    }

    [Fact]
    public async Task DictationRejectsAnUnknownModel()
    {
        FixedPipeline pipeline = new();
        await using DictationServer server = StartServer(() => pipeline);
        using HttpClient client = Client(server);

        // La rotta accettava qualunque campo e restituiva 200: il chiamante credeva che
        // il suo modello fosse stato usato. Ora lo stesso rifiuto della trascrizione.
        using MultipartFormDataContent form = DictationForm(("model", "whisper-1"));
        HttpResponseMessage response = await client.PostAsync("/v1/dictation", form);
        ApiError error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("model_not_found", error.Error);
        Assert.Equal(0, pipeline.Calls);
    }

    [Fact]
    public async Task DictationRejectsLanguage()
    {
        FixedPipeline pipeline = new();
        await using DictationServer server = StartServer(() => pipeline);
        using HttpClient client = Client(server);

        // Il server non applica la lingua: un 200 direbbe che l'ha fatto.
        using MultipartFormDataContent form = DictationForm(("language", "it"));
        HttpResponseMessage response = await client.PostAsync("/v1/dictation", form);
        ApiError error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsupported_parameter", error.Error);
        Assert.Contains("language", error.Message);
        Assert.Equal(0, pipeline.Calls);
    }

    [Fact]
    public async Task DictationStillAcceptsTheProfileField()
    {
        // La validazione nuova non deve rifiutare il campo proprio di questa rotta: il
        // profilo arrivava dal multipart anche prima, e deve continuare ad arrivare.
        ProfilePipeline pipeline = new();
        await using DictationServer server = StartServer(() => pipeline);
        using HttpClient client = Client(server);

        using MultipartFormDataContent form = DictationForm(("profile", "code"));
        HttpResponseMessage response = await client.PostAsync("/v1/dictation", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("code", pipeline.LastProfile);
    }

    [Fact]
    public async Task ARequestAfterThePipelineIsSwappedUsesTheNewPipeline()
    {
        // Il caso rotto dal «Riprova download»: il server era partito con la pipeline
        // catturata per valore, e dopo lo scambio restava legato a quella liberata. La
        // seconda richiesta deve arrivare alla pipeline nuova, la prima alla vecchia.
        TextPipeline old = new("prima pipeline");
        TextPipeline newer = new("seconda pipeline");
        IInferencePipeline? current = old;
        await using DictationServer server = StartServer(() => current);
        using HttpClient client = Client(server);

        Assert.Equal("prima pipeline", await DictationTextAsync(client));
        Assert.Equal(1, old.Calls);

        // Lo scambio che fa App dopo aver creato il runtime nuovo: il provider ora legge
        // un'altra pipeline. Nessun riavvio del server: la funzione è letta a ogni turno.
        current = newer;

        Assert.Equal("seconda pipeline", await DictationTextAsync(client));
        Assert.Equal(1, newer.Calls);

        // Il vecchio non è più toccato: nessuna richiesta è tornata a una pipeline liberata.
        Assert.Equal(1, old.Calls);
    }

    [Fact]
    public async Task NoLogEntryEverContainsTheToken()
    {
        // La policy del token vale finché qualcuno la controlla: un log che scrivesse
        // l'intestazione intera (o il token come valore) passerebbe tutti gli altri test.
        List<(string Code, string Message)> entries = [];
        await using DictationServer server = DictationServer.Start(Config(),
            () => new FixedPipeline(), () => true, () => false, Token, null, Policy(),
            (code, message) =>
            {
                lock (entries) entries.Add((code, message));
            });

        // Un rifiuto 401 e un turno riuscito: sono i due percorsi che scrivono nel log.
        using (HttpClient anonymous = Client(server, authenticated: false))
        {
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await anonymous.GetAsync("/v1/models")).StatusCode);
        }

        using (HttpClient client = Client(server))
        {
            using MultipartFormDataContent form = TranscriptionForm(Alias);
            Assert.Equal(HttpStatusCode.OK,
                (await client.PostAsync("/v1/audio/transcriptions", form)).StatusCode);
        }

        List<(string Code, string Message)> snapshot;
        lock (entries) snapshot = [.. entries];
        Assert.NotEmpty(snapshot);
        Assert.All(snapshot, entry =>
        {
            Assert.DoesNotContain(Token, entry.Code);
            Assert.DoesNotContain(Token, entry.Message);
        });
    }

    [Fact]
    public async Task TranscriptionReturnsTextForAValidFile()
    {
        FixedPipeline pipeline = new();
        await using DictationServer server = StartServer(() => pipeline);
        using HttpClient client = Client(server);

        using MultipartFormDataContent form = TranscriptionForm(Alias);
        HttpResponseMessage response = await client.PostAsync("/v1/audio/transcriptions", form);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(FinalText, json.RootElement.GetProperty("text").GetString());
        // La forma normale è quella dei client OpenAI: niente campi in più quando c'è voce.
        Assert.False(json.RootElement.TryGetProperty("no_speech", out _));
        Assert.Equal(1, pipeline.Calls);
    }

    [Fact]
    public async Task DictationReturnsTheFullResult()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server);
        using ByteArrayContent audio = new(Wav16k(0.25));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        HttpRequestMessage request = new(HttpMethod.Post, "/v1/dictation") { Content = audio };
        request.Headers.Add("X-Dictation-Profile", "code");
        HttpResponseMessage response = await client.SendAsync(request);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RawText, json.RootElement.GetProperty("raw_text").GetString());
        // Il testo finale è "text", non "final_text": la forma della risposta è quella
        // documentata dal contratto (e pinnata da DictationContractTests), dove "text" è
        // il campo compatibile OpenAI e "raw_text" il grezzo. "final_text" nel brief è un
        // nome rimasto dal piano, che nella stessa pagina definisce la risposta senza.
        Assert.Equal(FinalText, json.RootElement.GetProperty("text").GetString());
        Assert.True(json.RootElement.GetProperty("latency").GetProperty("total_ms").GetDouble() > 0);
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("warnings").ValueKind);
    }

    [Fact]
    public async Task ApiCallIsRecordedUnderTheApiTransport()
    {
        RecordingSinkSpy spy = new();
        await using DictationServer server = StartServer(() => new FixedPipeline(), spy);
        using HttpClient client = Client(server);
        byte[] wav = Wav16k(0.25);
        // Lunghezza attesa del PCM decodificato di questo stesso WAV. Il server non decodifica
        // più l'audio per registrarlo: riceve il PCM dall'esito dell'executor, quindi questo
        // numero prova che al sink arriva l'audio vero e completo, non un segnaposto.
        int decodedLength = AudioPcmDecoder.ToPcm16Mono16k(new MemoryStream(wav), out _).Length;
        using ByteArrayContent audio = new(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");

        HttpRequestMessage request = new(HttpMethod.Post, "/v1/dictation") { Content = audio };
        request.Headers.Add("X-Dictation-Profile", "code");
        HttpResponseMessage response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        RecordedCall call = Assert.Single(spy.Calls);
        Assert.Equal("api", call.Start.AppName);
        Assert.Equal("code", call.Start.Profile);
        // Il PCM registrato è audio vero, non un segnaposto vuoto: RecordingStore scarta
        // un PCM vuoto, quindi una registrazione "riuscita" con zero byte sarebbe muta.
        Assert.NotEmpty(call.Pcm);
        Assert.Equal(decodedLength, call.Pcm.Length);
    }

    // La rotta testo→testo: Hex detta con il proprio motore e non può mandare audio, ma
    // deve ricevere le stesse correzioni deterministiche della pipeline locale. Questi test
    // pinnano che le regole sono quelle delle classi esistenti e che la rotta non tocca i
    // pesi (nessuna coda di inferenza).

    [Fact]
    public async Task CorrectAppliesTheSameStagesAsTheLocalPipeline()
    {
        FixedPipeline pipeline = new();
        await using DictationServer server = StartServer(() => pipeline);
        using HttpClient client = Client(server);

        using StringContent body = CorrectBody("ehm la gente ha risposto su ciper inference");
        HttpResponseMessage response = await client.PostAsync("/v1/correct", body);
        string text = await CorrectedTextAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Un filler (LightCleanup), un termine del glossario (GlossaryNormalizer) e
        // un'elisione (DictationCorrections): le tre fasi deterministiche della pipeline
        // locale, nell'ordine della pipeline. `la gente` non è nel glossario, quindi
        // `l'agente` può venire solo dalla terza fase: toglierla lascerebbe questo test rosso.
        Assert.DoesNotContain("ehm", text);
        Assert.Contains("Cheaper Inference", text);
        Assert.DoesNotContain("ciper", text);
        Assert.Contains("l'agente", text);
        Assert.DoesNotContain("la gente", text);
        // Nessun turno di trascrizione: la rotta non passa dall'executor.
        Assert.Equal(0, pipeline.Calls);
    }

    [Fact]
    public async Task CorrectLeavesTechnicalProfilesToTheirOwnRules()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server);

        // `code` è il profilo in cui GlossaryNormalizer non tocca nulla: il termine sentito
        // resta com'è. La regola di profilo sta nella classe, non in una copia nella rotta.
        using StringContent body = CorrectBody("ehm ciper inference", "code");
        HttpResponseMessage response = await client.PostAsync("/v1/correct", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Il filler sparisce lo stesso: LightCleanup non dipende dal profilo.
        Assert.Equal("ciper inference", await CorrectedTextAsync(response));
    }

    [Fact]
    public async Task CorrectRejectsAnUnknownProfile()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server);

        using StringContent body = CorrectBody("ciao", "hacker");
        HttpResponseMessage response = await client.PostAsync("/v1/correct", body);
        ApiError error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsupported_profile", error.Error);
        Assert.Equal(400, error.Status);
    }

    [Fact]
    public async Task CorrectRequiresTheToken()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server, authenticated: false);

        // La rotta non è /health: senza token è un 401, come tutte le altre.
        using StringContent body = CorrectBody("ciao");
        HttpResponseMessage response = await client.PostAsync("/v1/correct", body);
        ApiError error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", error.Error);
    }

    [Fact]
    public async Task CorrectRejectsAnEmptyBody()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server);

        // Un oggetto senza `text`: il chiamante non ha mandato niente da correggere, e la
        // rotta non deve indovinare. Deve essere un 400 del contratto, non un 500.
        using StringContent emptyObject = new("{}", Encoding.UTF8, "application/json");
        HttpResponseMessage withoutText = await client.PostAsync("/v1/correct", emptyObject);
        ApiError error = await ReadErrorAsync(withoutText);
        Assert.Equal(HttpStatusCode.BadRequest, withoutText.StatusCode);
        Assert.Equal("invalid_request", error.Error);

        // Il corpo del tutto assente è lo stesso errore del chiamante, con lo stesso codice.
        using StringContent nothing = new("", Encoding.UTF8, "application/json");
        HttpResponseMessage noBody = await client.PostAsync("/v1/correct", nothing);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        Assert.Equal("invalid_request", (await ReadErrorAsync(noBody)).Error);
    }

    [Fact]
    public async Task CorrectRejectsTextAboveTheLimit()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server);

        // Il limite è 100 000 caratteri: uno in più è un errore del chiamante, non un testo
        // troncato consegnato come se fosse corretto.
        using StringContent body = CorrectBody(new string('a', 100_001));
        HttpResponseMessage response = await client.PostAsync("/v1/correct", body);
        ApiError error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", error.Error);
    }

    [Fact]
    public async Task CorrectAcceptsTextEscapedWithUnicodeSequences()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server);

        // 99 000 caratteri scritti come \u0061 costano sei byte l'uno nel corpo ma uno solo
        // da decodificati: è quello che manda json.dumps con ensure_ascii=True (o una coppia
        // surrogata). Il pre-controllo misura i byte, quindi deve usare il caso peggiore
        // escapato e non i quattro byte di un carattere UTF-8, altrimenti rifiuterebbe un
        // testo ammesso con un 400 che il contratto non ha mai chiesto.
        string escaped = string.Concat(Enumerable.Repeat("""\u0061""", 99_000));
        using StringContent body = new($$"""{"text":"{{escaped}}"}""", Encoding.UTF8,
            "application/json");
        HttpResponseMessage response = await client.PostAsync("/v1/correct", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(99_000, (await CorrectedTextAsync(response)).Length);
    }

    [Fact]
    public async Task CorrectRejectsAnOversizedBodyBeforeReadingIt()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server);

        // 700 000 caratteri ASCII stanno sotto il tetto di Kestrel ma superano il caso
        // peggiore di JSON escapato di MaxCorrectTextChars: il Content-Length lo dice prima
        // di leggere il corpo, e il messaggio nomina i byte (il limite in caratteri direbbe
        // altro).
        using StringContent body = CorrectBody(new string('a', 700_000));
        HttpResponseMessage response = await client.PostAsync("/v1/correct", body);
        ApiError error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", error.Error);
        Assert.Contains("byte", error.Message);
    }

    [Fact]
    public async Task CorrectAcceptsABodyWithAByteOrderMark()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server);

        // Un client che scrive UTF-8 con BOM manda EF BB BF prima di "{": il JSON è valido e
        // il BOM non deve arrivare al parser come primo carattere, altrimenti un corpo
        // corretto diventa «JSON non valido».
        byte[] json = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("""{"text":"Ciao"}""")];
        using ByteArrayContent body = new(json);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        HttpResponseMessage response = await client.PostAsync("/v1/correct", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Ciao", await CorrectedTextAsync(response));
    }

    [Fact]
    public async Task CorrectRejectsABodyThatIsNotUtf8()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server);

        // 0xFF non è un byte UTF-8 valido: una decodifica tollerante lo trasformerebbe in
        // U+FFFD e risponderebbe 200 con un testo che il chiamante non ha mai scritto.
        byte[] json = [.. Encoding.ASCII.GetBytes("""{"text":"c"""), 0xFF,
            .. Encoding.ASCII.GetBytes("""ao"}""")];
        using ByteArrayContent body = new(json);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        HttpResponseMessage response = await client.PostAsync("/v1/correct", body);
        ApiError error = await ReadErrorAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", error.Error);
    }

    [Fact]
    public async Task CorrectReturnsJsonWithOnlyTheText()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server);

        using StringContent body = CorrectBody("Ciao, come va?");
        HttpResponseMessage response = await client.PostAsync("/v1/correct", body);
        string json = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // La forma è esattamente quella documentata: solo `text`, senza campi dell'audio.
        Assert.Equal("{\"text\":\"Ciao, come va?\"}", json);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonProperty property = Assert.Single(document.RootElement.EnumerateObject());
        Assert.Equal("text", property.Name);
        Assert.Contains("application/json", response.Content.Headers.ContentType!.MediaType!);
    }

    // I due test che seguono non sono nel brief: pinnano le due promesse che il brief fa a
    // parole e che nessun altro test coprirebbe — la rotta non tocca i pesi (risponde anche
    // senza modelli caricati) e un testo vuoto dopo il trim non è un errore.

    [Fact]
    public async Task CorrectWorksWithoutAModel()
    {
        // Nessun modello caricato: /v1/dictation sarebbe un 503. Una correzione non tocca i
        // pesi né la coda di inferenza, quindi risponde lo stesso.
        await using DictationServer server = StartServer(() => null);
        using HttpClient client = Client(server);

        using StringContent body = CorrectBody("ehm ci vediamo dopo");
        HttpResponseMessage response = await client.PostAsync("/v1/correct", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ci vediamo dopo", await CorrectedTextAsync(response));
    }

    [Fact]
    public async Task CorrectReturnsEmptyTextWhenThereIsNothingToCorrect()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server);

        // Il chiamante ha già deciso che non c'era nulla da correggere: un testo di soli
        // spazi non è un errore, e la risposta è la forma normale con il vuoto.
        using StringContent body = CorrectBody("   \t ");
        HttpResponseMessage response = await client.PostAsync("/v1/correct", body);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"text\":\"\"}", await response.Content.ReadAsStringAsync());
    }

    // I due test che seguono non sono elencati nel brief: pinnano due comportamenti che il
    // brief però impone a parole (il token è accettato anche come X-Api-Key, e lo stop deve
    // poter essere chiamato due volte), e senza un test resterebbero solo promesse.

    [Fact]
    public async Task ApiKeyHeaderIsAccepted()
    {
        await using DictationServer server = StartServer(() => new FixedPipeline());
        using HttpClient client = Client(server, authenticated: false);
        client.DefaultRequestHeaders.Add("X-Api-Key", Token);

        HttpResponseMessage response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task StopCanRunTwice()
    {
        // La via di emergenza di App.OnExit può passare dopo lo spegnimento normale:
        // un secondo stop non deve lanciare. Il finally copre anche un'asserzione rossa.
        DictationServer server = StartServer(() => new FixedPipeline());
        try
        {
            using HttpClient client = Client(server);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

            await server.StopAsync();
            await server.StopAsync();
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ThePortIsFreeAgainAfterStopAndCanBeReused()
    {
        // È la sequenza del cambio di coppia di modelli: il server si ferma, si fermano i
        // modelli, nasce il runtime nuovo e il server riparte sulla stessa porta esplicita.
        // Con la porta 0 il secondo server prenderebbe una porta diversa e il test non
        // direbbe niente sul rilascio del socket: la porta è la stessa per entrambi.
        int port = FreePort();
        ApiConfig first = Config();
        first.Port = port;
        DictationServer original = DictationServer.Start(first, () => new FixedPipeline(), () => true,
            () => false, Token, null, Policy(), (_, _) => { });
        try
        {
            using HttpClient client = Client(original);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
            Assert.Equal(port, new Uri(original.BaseUrl).Port);
            await original.StopAsync();
        }
        finally
        {
            await original.DisposeAsync();
        }

        ApiConfig restartedConfig = Config();
        restartedConfig.Port = port;
        await using DictationServer restarted = DictationServer.Start(restartedConfig,
            () => new FixedPipeline(), () => true, () => false, Token, null, Policy(), (_, _) => { });
        using HttpClient restartedClient = Client(restarted);

        Assert.Equal(port, new Uri(restarted.BaseUrl).Port);
        Assert.Equal(HttpStatusCode.OK, (await restartedClient.GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task OversizeMultipartUploadIs413AudioTooLarge()
    {
        // Un corpo sopra il tetto deve essere 413 audio_too_large, non il 400 del multipart.
        // Il limite del form sta apposta poco sopra quello di Kestrel: con i due valori
        // uguali l'esito dipenderebbe da quale contatore arriva prima, cioè sarebbe una gara.
        FixedPipeline pipeline = new();
        ApiConfig config = Config();
        config.MaxRequestBytes = 65_536;
        await using DictationServer server = DictationServer.Start(config, () => pipeline, () => true,
            () => false, Token, null, Policy(), (_, _) => { });
        using HttpClient client = Client(server);

        using MultipartFormDataContent form = new();
        ByteArrayContent file = new(new byte[config.MaxRequestBytes * 2]);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "grande.wav");
        form.Add(new StringContent(Alias), "model");

        HttpResponseMessage response = await client.PostAsync("/v1/audio/transcriptions", form);
        ApiError error = await ReadErrorAsync(response);

        Assert.Equal((HttpStatusCode)413, response.StatusCode);
        Assert.Equal("audio_too_large", error.Error);
        Assert.Equal(413, error.Status);
        Assert.Equal(0, pipeline.Calls);
    }

    /// <summary>Pipeline fissa: restituisce sempre lo stesso risultato e conta le chiamate.</summary>
    private sealed class FixedPipeline : IInferencePipeline
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new PipelineResult(RawText, FinalText, 12, 3, 15,
                ["avviso di prova"], new EngineIdentity("stt", "rev", "sha", "runtime", "cpu", "q8", "cpu"),
                Cleaner: null, CleanerText: "testo pulito"));
        }

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// Pipeline che restituisce un testo scelto e conta le chiamate: serve a distinguere
    /// due pipeline nello stesso server (il primo turno e quello dopo lo scambio).
    /// </summary>
    private sealed class TextPipeline(string text) : IInferencePipeline
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new PipelineResult(text, text, 10, 0, 10, [],
                new EngineIdentity("stt", "rev", "sha", "runtime", "cpu", "q8", "cpu"),
                Cleaner: null));
        }

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Pipeline che registra il profilo ricevuto: prova che il multipart arriva intatto.</summary>
    private sealed class ProfilePipeline : IInferencePipeline
    {
        private string? _profile;

        public string? LastProfile => Volatile.Read(ref _profile);

        public Task<PipelineResult> ProcessAsync(PipelineRequest request, CancellationToken ct)
        {
            Volatile.Write(ref _profile, request.Profile);
            return Task.FromResult(new PipelineResult(RawText, FinalText, 10, 0, 10, [],
                new EngineIdentity("stt", "rev", "sha", "runtime", "cpu", "q8", "cpu"),
                Cleaner: null));
        }

        public Task WarmUpAsync(CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Registra le chiamate al sink: prova che un turno HTTP diventa una registrazione.</summary>
    private sealed class RecordingSinkSpy : IRecordingSink
    {
        private readonly object _gate = new();
        private readonly List<RecordedCall> _calls = [];

        public IReadOnlyList<RecordedCall> Calls
        {
            get { lock (_gate) return _calls.ToArray(); }
        }

        public bool TryEnqueue(ReadOnlyMemory<byte> pcm, PipelineResult result, StartMessage start)
        {
            lock (_gate) _calls.Add(new RecordedCall(pcm.ToArray(), result, start));
            return true;
        }
    }

    private sealed record RecordedCall(byte[] Pcm, PipelineResult Result, StartMessage Start);
}
