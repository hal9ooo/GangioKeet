namespace GangioKeet.Client.Api;

using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using GangioKeet.Inference;

/// <summary>
/// Audio già portato al contratto della pipeline, con il profilo di pulizia scelto dal
/// chiamante fra quelli ammessi. È l'unica cosa che il server passa al trascrittore:
/// nessun oggetto che arrivi da HTTP entra nella pipeline così com'è.
/// </summary>
public sealed record DictationInput(ReadOnlyMemory<byte> Pcm16, string Profile);

/// <summary>
/// Corpo di <c>POST /v1/correct</c> già validato: il testo da correggere e il profilo di
/// pulizia fra quelli ammessi. Serve alle app che hanno un proprio motore di dettatura
/// (Hex) e vogliono le stesse correzioni deterministiche senza mandare audio: il testo
/// arriva qui, le regole restano nelle classi di pulizia.
/// </summary>
public sealed record CorrectRequest(string Text, string Profile);

/// <summary>
/// Errore della API nel formato che il server restituisce: un codice stabile per il
/// chiamante (<see cref="Error"/>), un messaggio per la persona che legge il log e lo
/// stato HTTP con cui va servito. I codici sono stringhe fisse, non enum, perché sono
/// parte del contratto pubblico verso i client compatibili OpenAI.
/// </summary>
public sealed record ApiError(string Error, string Message, int Status)
{
    /// <summary>Codice di un parametro che il server non applicherebbe.</summary>
    public const string UnsupportedParameterCode = "unsupported_parameter";

    /// <summary>Codice di un profilo di pulizia che non esiste.</summary>
    public const string UnsupportedProfileCode = "unsupported_profile";

    /// <summary>
    /// Codice del corpo che non si può leggere o che non rispetta il contratto della rotta.
    /// Sta qui e non nel server perché lo usano due punti: i rifiuti scritti sulla porta
    /// (multipart o corpo illeggibile) e quelli decisi dal contratto puro.
    /// </summary>
    public const string InvalidRequestCode = "invalid_request";

    /// <summary>
    /// 400 con il codice indicato: serve ai rifiuti che non hanno una factory propria
    /// (un corpo illeggibile, e ogni caso futuro). Il codice va preso dalle costanti di
    /// questa classe, così due punti del server non inventano due stringhe diverse per
    /// lo stesso errore.
    /// </summary>
    public static ApiError BadRequest(string error, string message) => new(error, message, 400);

    /// <summary>
    /// 400 per un profilo di pulizia che non esiste, invece di un ripiego muto su
    /// <c>default</c>. Codice distinto da <see cref="UnsupportedParameterCode"/>: dire
    /// "parametro non supportato" quando il problema è il valore di <c>profile</c>
    /// manderebbe chi legge fuori strada.
    /// </summary>
    public static ApiError UnsupportedProfile(string message) =>
        new(UnsupportedProfileCode, message, 400);

    /// <summary>
    /// Il chiamante non sceglie i modelli: l'unico ammesso è l'alias configurato.
    /// Il messaggio nomina sia quello richiesto sia quello disponibile, così chi legge
    /// il 400 sa cosa correggere.
    /// </summary>
    public static ApiError ModelNotFound(string requested, string alias) => new("model_not_found",
        $"Il modello '{requested}' non è disponibile: l'unico ammesso è '{alias}'.", 400);

    /// <summary>Contenuto che non si riesce a portare a PCM16 mono 16 kHz (415).</summary>
    public static ApiError UnsupportedFormat(string message) => new("unsupported_audio_format", message, 415);

    /// <summary>Audio o corpo oltre i limiti configurati (413).</summary>
    public static ApiError TooLarge(string message) => new("audio_too_large", message, 413);

    /// <summary>Coda occupata oltre l'attesa concessa (503).</summary>
    public static ApiError Busy(string message) => new("busy", message, 503);

    /// <summary>Modelli ancora in caricamento quando l'attesa è scaduta (503).</summary>
    public static ApiError Loading(string message) => new("model_loading", message, 503);

    /// <summary>
    /// Errore inatteso (500). Il messaggio resta generico: il dettaglio va nel log
    /// dell'app, non nella risposta a chi ha chiamato.
    /// </summary>
    public static ApiError Internal(string message) => new("internal_error", message, 500);

    /// <summary>
    /// Token assente, malformato o sbagliato (401). Nasce nel server HTTP, dove vive il
    /// token: il contratto puro non lo conosce. Il messaggio dice come si manda il token
    /// e non ripete mai quello ricevuto, che nel caso peggiore è il token vero con un
    /// carattere in più.
    /// </summary>
    public static ApiError Unauthorized(string message) => new("unauthorized", message, 401);
}

/// <summary>
/// Il traduttore puro fra HTTP e pipeline: nessun socket, nessuno stato, così ogni
/// regola del contratto si può provare senza aprire una porta.
///
/// Due regole di prodotto vivono qui. Il chiamante non sceglie il modello: l'unico
/// valore accettato per <c>model</c> è l'alias configurato. I parametri che
/// cambierebbero il comportamento (<c>language</c>, <c>prompt</c>, <c>temperature</c>, …)
/// sono <em>rifiutati</em>, mai ignorati: un client che manda <c>language=it</c> e
/// riceve 200 crederebbe di aver controllato qualcosa che il server non applica.
/// </summary>
public static class DictationContract
{
    /// <summary>
    /// Chiavi ammesse da <c>POST /v1/audio/transcriptions</c>. Sono solo queste tre:
    /// le altre sono un rifiuto, non un silenzio. I nomi sono quelli che i client
    /// compatibili OpenAI usano davvero.
    /// </summary>
    private static readonly string[] AcceptedKeys = ["model", "response_format", "file"];

    /// <summary>
    /// Chiavi ammesse da <c>POST /v1/dictation</c>: <c>file</c> quando l'audio arriva
    /// multipart, <c>profile</c> perché è il campo proprio di questa rotta, e
    /// <c>model</c> per lo stesso controllo dell'altra rotta (c'è un solo motore).
    /// <c>response_format</c> non c'è: questa rotta ha una sola forma di risposta.
    /// </summary>
    private static readonly string[] DictationAcceptedKeys = ["model", "file", "profile"];

    /// <summary>
    /// Profili di pulizia ammessi. È lo stesso insieme che <c>CleanerPrompt.Render</c>
    /// valida; lì è un letterale dentro il metodo e non è esposto, quindi qui c'è una
    /// copia. Un test verifica che ogni profilo accettato qui sia accettato anche dal
    /// cleaner, così le due liste non possono divergere in silenzio.
    /// </summary>
    private static readonly string[] Profiles = ["default", "code", "terminal", "chat", "email", "document"];

    /// <summary>Unico valore valido di <c>response_format</c> che chiede il JSON: è il predefinito.</summary>
    public const string JsonResponseFormat = "json";

    /// <summary>Valore di <c>response_format</c> che chiede il solo testo, in <c>text/plain</c>.</summary>
    public const string TextResponseFormat = "text";

    /// <summary>
    /// JSON compatto con il solo testo. <c>no_speech</c> compare soltanto quando è vero:
    /// la forma normale resta <c>{"text":"..."}</c>, quella che i client OpenAI si aspettano.
    /// Vedi <see cref="NewWriter"/> per la scelta dell'encoder.
    /// </summary>
    /// <exception cref="ArgumentNullException">Il testo è <c>null</c>: non è un caso che il server debba indovinare.</exception>
    public static string ToJson(string text, bool noSpeech)
    {
        ArgumentNullException.ThrowIfNull(text);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("text", text);
            if (noSpeech)
            {
                writer.WriteBoolean("no_speech", true);
            }

            writer.WriteEndObject();
        }

        return ToUtf8String(buffer);
    }

    /// <summary>
    /// Risposta di <c>POST /v1/correct</c>: esattamente <c>{"text":"..."}</c>. Non c'è
    /// <c>no_speech</c> perché per un testo non esiste audio da cui dedurre il silenzio, e
    /// niente altro: chi chiama ha già il testo, e la risposta serve a riceverlo corretto.
    /// L'encoder è lo stesso di <see cref="ToJson"/>: stesso escaping, stesso accento leggibile.
    /// </summary>
    /// <exception cref="ArgumentNullException">Il testo è <c>null</c>: non è un caso che il server debba indovinare.</exception>
    public static string ToCorrectJson(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("text", text);
            writer.WriteEndObject();
        }

        return ToUtf8String(buffer);
    }

    /// <summary>
    /// Risposta completa di <c>POST /v1/dictation</c>: testo finale, testo grezzo,
    /// testo del cleaner, avvisi della pipeline e latenze.
    /// <c>cleaner_text</c> è sempre presente e vale <c>null</c> quando nessun cleaner
    /// ha lavorato: omettere il campo farebbe sembrare la risposta incompleta, e una
    /// stringa vuota direbbe una cosa falsa (che il cleaner ha restituito niente).
    /// <c>no_speech</c> segnala un audio senza voce e si legge dal testo grezzo: se
    /// l'ASR non ha sentito nulla il grezzo è vuoto, mentre un finale vuoto con un
    /// grezzo pieno è un'altra cosa (il cleaner ha tolto tutto) e non è silenzio.
    /// </summary>
    public static string ToDictationJson(PipelineResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = NewWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("text", result.FinalText);
            writer.WriteString("raw_text", result.RawText);
            if (result.CleanerText is null)
            {
                writer.WriteNull("cleaner_text");
            }
            else
            {
                writer.WriteString("cleaner_text", result.CleanerText);
            }

            // La verità su "nessuna voce" la dice l'ASR, non il cleaner: se il grezzo è
            // vuoto non c'era voce, e il finale vuoto con un grezzo pieno non deve
            // diventare una scusa per dichiarare silenzio.
            writer.WriteBoolean("no_speech", NoSpeech(result.RawText));

            writer.WriteStartArray("warnings");
            foreach (string warning in result.Warnings)
            {
                writer.WriteStringValue(warning);
            }

            writer.WriteEndArray();

            writer.WriteStartObject("latency");
            writer.WriteNumber("stt_ms", result.SttMs);
            writer.WriteNumber("llm_ms", result.LlmMs);
            writer.WriteNumber("total_ms", result.TotalMs);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return ToUtf8String(buffer);
    }

    /// <summary>
    /// Vero quando la pipeline non ha prodotto testo: è la stessa condizione con cui la
    /// dettatura locale consegna il vuoto dopo <c>SilenceGuard</c>. Un'eco dell'ASR su
    /// audio muto arriva qui già azzerata, quindi il chiamante riceve <c>""</c> e
    /// <c>no_speech: true</c> invece di una parola inventata.
    /// </summary>
    public static bool NoSpeech(string? text) => string.IsNullOrWhiteSpace(text);

    /// <summary>
    /// Profilo di pulizia a partire dal valore ricevuto da HTTP. Assente o vuoto
    /// significa <c>default</c>; un valore non ammesso è un errore del chiamante e
    /// diventa <see cref="ArgumentException"/> (il server lo serve come 400
    /// <see cref="ApiError.UnsupportedProfile"/>), non un ripiego muto su <c>default</c>.
    /// Il confronto è ordinale: gli identificatori sono minuscoli e fissi.
    /// </summary>
    /// <exception cref="ArgumentException">Il profilo non è fra quelli ammessi.</exception>
    public static string ProfileFrom(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return "default";
        }

        foreach (string known in Profiles)
        {
            if (string.Equals(known, profile, StringComparison.Ordinal))
            {
                return known;
            }
        }

        throw new ArgumentException(
            $"Profilo '{profile}' non ammesso. Valori validi: {string.Join(", ", Profiles)}.",
            nameof(profile));
    }

    /// <summary>
    /// Limite del testo di <c>POST /v1/correct</c>, in caratteri. Una dettatura è di
    /// qualche migliaio di caratteri: centomila lascia spazio a un incollaggio lungo senza
    /// permettere a una richiesta di occupare la memoria del server.
    /// </summary>
    public const int MaxCorrectTextChars = 100_000;

    /// <summary>
    /// Tetto in byte del corpo di <c>POST /v1/correct</c>, per il pre-controllo su
    /// <c>Content-Length</c>: sei byte per carattere più l'involucro JSON, che sta in molto
    /// meno di un kilobyte. Sei e non quattro perché il corpo è quello <em>codificato</em>
    /// mentre il limite è in caratteri <em>decodificati</em>: un testo che JSON scappa come
    /// <c>\uXXXX</c> (sei byte per carattere, come fa <c>json.dumps</c> con
    /// <c>ensure_ascii=True</c>, o come richiede una coppia surrogata) costa il doppio di un
    /// carattere UTF-8 da quattro byte. Con il caso peggiore non si rifiuta mai un corpo
    /// ammesso; un corpo più grande non può comunque stare sotto
    /// <see cref="MaxCorrectTextChars"/>, quindi il server lo rifiuta prima di copiarlo,
    /// decodificarlo e analizzarlo, invece di scoprirlo dopo aver speso fino a
    /// <c>MaxRequestBytes</c> di memoria.
    /// </summary>
    public const long MaxCorrectBodyBytes = ((long)MaxCorrectTextChars * 6) + 1024;

    /// <summary>
    /// Legge il corpo di <c>POST /v1/correct</c>: <c>{"text":"...","profile":"default"}</c>.
    /// Restituisce <c>null</c> se la richiesta è ammessa, altrimenti l'errore da servire.
    /// <c>text</c> è obbligatorio (un corpo senza testo non è una richiesta di correzione);
    /// vuoto o di soli spazi è invece ammesso, perché il chiamante può aver già deciso che
    /// non c'era niente da correggere. Il profilo segue le stesse regole di
    /// <see cref="ProfileFrom"/>: assente vuol dire <c>default</c>, sconosciuto è
    /// <see cref="ApiError.UnsupportedProfileCode"/>. Un campo che la rotta non conosce è un
    /// rifiuto e non un silenzio: accettarlo direbbe al chiamante che è stato applicato.
    /// Il limite di <see cref="MaxCorrectTextChars"/> si misura sul testo grezzo, prima del
    /// trim: gli spazi in più sono comunque bytes che il server ha ricevuto.
    /// </summary>
    /// <param name="body">Il corpo JSON grezzo; <c>null</c> o vuoto è un corpo mancante.</param>
    /// <param name="request">La richiesta validata, <c>null</c> quando c'è un errore.</param>
    public static ApiError? CorrectFrom(string? body, out CorrectRequest? request)
    {
        request = null;
        if (string.IsNullOrWhiteSpace(body))
        {
            return ApiError.BadRequest(ApiError.InvalidRequestCode,
                "Manca il corpo JSON con il campo 'text' da correggere.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return ApiError.BadRequest(ApiError.InvalidRequestCode,
                "Il corpo non è JSON valido: atteso {\"text\":\"...\",\"profile\":\"default\"}.");
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ApiError.BadRequest(ApiError.InvalidRequestCode,
                    "Il corpo deve essere un oggetto JSON con il campo 'text'.");
            }

            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name is not ("text" or "profile"))
                {
                    return ApiError.BadRequest(ApiError.InvalidRequestCode,
                        $"Il parametro '{property.Name}' non è supportato: campi ammessi: text, profile.");
                }
            }

            if (!root.TryGetProperty("text", out JsonElement text)
                || text.ValueKind != JsonValueKind.String)
            {
                return ApiError.BadRequest(ApiError.InvalidRequestCode,
                    "Il campo 'text' è obbligatorio e deve essere una stringa.");
            }

            string value = text.GetString()!;
            if (value.Length > MaxCorrectTextChars)
            {
                return ApiError.BadRequest(ApiError.InvalidRequestCode,
                    $"Il testo supera il limite di {MaxCorrectTextChars} caratteri.");
            }

            string? profileValue = null;
            if (root.TryGetProperty("profile", out JsonElement profile)
                && profile.ValueKind != JsonValueKind.Null)
            {
                if (profile.ValueKind != JsonValueKind.String)
                {
                    return ApiError.BadRequest(ApiError.InvalidRequestCode,
                        "Il campo 'profile' deve essere una stringa.");
                }

                profileValue = profile.GetString();
            }

            string resolved;
            try
            {
                resolved = ProfileFrom(profileValue);
            }
            catch (ArgumentException error)
            {
                // Il messaggio nomina i profili ammessi e il valore ricevuto: è la correzione.
                return ApiError.UnsupportedProfile(error.Message);
            }

            request = new CorrectRequest(value, resolved);
            return null;
        }
    }

    /// <summary>
    /// Controlla i campi di <c>POST /v1/audio/transcriptions</c> prima di toccare l'audio.
    /// Restituisce <c>null</c> se la richiesta è ammessa, altrimenti l'errore da servire.
    /// <paramref name="keys"/> sono i nomi presenti nella richiesta e <paramref name="value"/>
    /// ne legge il valore: così il controllo è puro e non conosce né ASP.NET né il multipart.
    /// <c>model</c> deve valere esattamente <paramref name="alias"/>; <c>response_format</c>
    /// deve essere <see cref="JsonResponseFormat"/> o <see cref="TextResponseFormat"/>;
    /// ogni altra chiave è un rifiuto che la nomina. Il primo errore incontrato vince,
    /// nell'ordine in cui le chiavi arrivano.
    /// Un <c>model</c> assente è ammesso: c'è un solo modello, quindi non c'è niente da
    /// scegliere e un client minimale che lo omette non merita un 400.
    /// </summary>
    /// <param name="alias">L'unico modello esposto, quello configurato.</param>
    /// <param name="keys">Nomi dei campi presenti nella richiesta.</param>
    /// <param name="value">Lettore del valore di un campo.</param>
    public static ApiError? ValidateTranscription(string alias, IEnumerable<string> keys,
        Func<string, string?> value) =>
        Validate(alias, keys, value, AcceptedKeys, allowsResponseFormat: true);

    /// <summary>
    /// Controlla i campi di <c>POST /v1/dictation</c> quando l'audio arriva multipart. È la
    /// stessa regola dell'altra rotta, con un insieme di chiavi diverso: lì c'è
    /// <c>response_format</c>, qui c'è <c>profile</c>. Lo stesso <c>model</c> sbagliato è
    /// <see cref="ApiError.ModelNotFound"/> e ogni altro campo è
    /// <see cref="ApiError.UnsupportedParameterCode"/>: un <c>model</c>, <c>language</c>,
    /// <c>prompt</c> o <c>temperature</c> ignorato con un 200 direbbe al chiamante che la
    /// sua scelta è stata applicata.
    /// </summary>
    /// <param name="alias">L'unico modello esposto, quello configurato.</param>
    /// <param name="keys">Nomi dei campi presenti nella richiesta.</param>
    /// <param name="value">Lettore del valore di un campo.</param>
    public static ApiError? ValidateDictation(string alias, IEnumerable<string> keys,
        Func<string, string?> value) =>
        Validate(alias, keys, value, DictationAcceptedKeys, allowsResponseFormat: false);

    private static ApiError? Validate(string alias, IEnumerable<string> keys,
        Func<string, string?> value, string[] accepted, bool allowsResponseFormat)
    {
        ArgumentNullException.ThrowIfNull(alias);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(value);

        foreach (string key in keys)
        {
            if (Array.IndexOf(accepted, key) < 0)
            {
                // Un campo che questa rotta non conosce: accettarlo senza applicarlo
                // direbbe al chiamante che ha controllato qualcosa che il server ignora.
                return ApiError.BadRequest(ApiError.UnsupportedParameterCode,
                    $"Il parametro '{key}' non è supportato: il server non lo applicherebbe. "
                    + $"Campi ammessi: {string.Join(", ", accepted)}.");
            }

            if (string.Equals(key, "model", StringComparison.Ordinal))
            {
                string? requested = value(key);
                if (!string.Equals(requested, alias, StringComparison.Ordinal))
                {
                    return ApiError.ModelNotFound(Describe(requested), alias);
                }
            }
            else if (allowsResponseFormat
                && string.Equals(key, "response_format", StringComparison.Ordinal))
            {
                string? format = value(key);
                if (format is not (JsonResponseFormat or TextResponseFormat))
                {
                    return ApiError.BadRequest(ApiError.UnsupportedParameterCode,
                        $"Il parametro 'response_format' accetta solo "
                        + $"'{JsonResponseFormat}' o '{TextResponseFormat}', non '{Describe(format)}'.");
                }
            }

            // `file` non ha un valore da controllare qui, e `profile` lo valida
            // ProfileFrom: questa funzione decide solo cosa è ammesso.
        }

        return null;
    }

    /// <summary>Valore assente mostrato nei messaggi senza lasciare un buco fra gli apici.</summary>
    private static string Describe(string? value) => value is null ? "(assente)" : value;

    /// <summary>
    /// Vero quando il chiamante ha chiesto <c>response_format=text</c>, cioè il solo testo
    /// in <c>text/plain</c> invece del JSON. La decisione sta qui e non nel server perché
    /// è parte del contratto: due copie della regola divergerebbero al primo ritocco.
    /// Una richiesta senza <c>response_format</c> è JSON, come per l'API OpenAI.
    /// </summary>
    public static bool WantsPlainText(IEnumerable<string> keys, Func<string, string?> value)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(value);

        foreach (string key in keys)
        {
            if (string.Equals(key, "response_format", StringComparison.Ordinal))
            {
                return string.Equals(value(key), TextResponseFormat, StringComparison.Ordinal);
            }
        }

        return false;
    }

    /// <summary>
    /// Writer compatto con <see cref="JavaScriptEncoder"/> limitato ai blocchi Unicode veri:
    /// le lettere accentate restano leggibili (un <c>curl</c> di verifica si deve poter
    /// leggere), mentre <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c> e le virgolette restano
    /// scappati. Il testo arriva da un audio caricato dal chiamante, quindi non deve poter
    /// passare per markup se un client lo inserisce in una pagina.
    /// </summary>
    private static Utf8JsonWriter NewWriter(Stream buffer) => new(buffer,
        new JsonWriterOptions { Encoder = JavaScriptEncoder.Create(UnicodeRanges.All) });

    private static string ToUtf8String(MemoryStream buffer) =>
        Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
}
