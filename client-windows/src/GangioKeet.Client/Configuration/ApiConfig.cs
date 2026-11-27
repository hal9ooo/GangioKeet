using System.Net;
using YamlDotNet.Serialization;

namespace GangioKeet.Client.Configuration;

/// <summary>
/// Sezione <c>api</c> della configurazione: il server HTTP compatibile OpenAI che gira
/// dentro l'app di tray. Spento per impostazione predefinita e in ascolto solo su loopback,
/// così un'installazione pulita non apre nessuna porta verso la rete.
/// Il token non si scrive qui: la configurazione contiene solo il <em>nome</em> della
/// variabile d'ambiente che lo custodisce (<see cref="TokenEnv"/>).
/// </summary>
public sealed class ApiConfig
{
    /// <summary>Accende il server HTTP. Spento non viene aperta nessuna porta.</summary>
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; }

    /// <summary>
    /// Indirizzi di ascolto. Il predefinito è il solo loopback; qualunque indirizzo diverso
    /// dal loopback richiede un token (vedi <see cref="ApiTokenPolicy.RequiredFor"/>).
    /// </summary>
    [YamlMember(Alias = "listen")]
    public string[] Listen { get; set; } = ["127.0.0.1"];

    /// <summary>Porta TCP del server. Sopra 1024: sotto servono privilegi di amministratore.</summary>
    [YamlMember(Alias = "port")]
    public int Port { get; set; } = 5099;

    /// <summary>Nome del modello esposto in <c>/v1/models</c> e <c>/v1/audio/transcriptions</c>.</summary>
    [YamlMember(Alias = "model_alias")]
    public string ModelAlias { get; set; } = "gangio-keet";

    /// <summary>
    /// Nome della variabile d'ambiente che contiene il token, mai il token stesso.
    /// Il valore viene letto all'avvio del server e non finisce nei log.
    /// </summary>
    [YamlMember(Alias = "token_env")]
    public string TokenEnv { get; set; } = "GANGIOKEET_API_TOKEN";

    /// <summary>Dimensione massima del corpo HTTP accettato, in byte (25 MB).</summary>
    [YamlMember(Alias = "max_request_bytes")]
    public int MaxRequestBytes { get; set; } = 26_214_400;

    /// <summary>Durata massima dell'audio di una richiesta, in secondi.</summary>
    [YamlMember(Alias = "max_audio_seconds")]
    public double MaxAudioSeconds { get; set; } = 300;

    /// <summary>Quanto il server aspetta una dettatura in coda prima di rispondere con un errore.</summary>
    [YamlMember(Alias = "wait_seconds")]
    public double WaitSeconds { get; set; } = 120;
}

/// <summary>
/// Regola del token: aprire una porta oltre il loopback senza token significherebbe
/// esporre il microfono e il motore ASR a chiunque sia sulla LAN o su Tailscale,
/// quindi la configurazione viene rifiutata invece di partire in silenzio.
/// </summary>
public static class ApiTokenPolicy
{
    /// <summary>Indirizzo che accetta connessioni su qualsiasi interfaccia.</summary>
    private const string AnyAddress = "*";

    /// <summary>
    /// Restituisce il token da usare per <paramref name="listenAddresses"/>, oppure <c>null</c>
    /// se il server ascolta solo sul loopback e quindi non serve.
    /// <paramref name="tokenEnv"/> è il nome della variabile da leggere, passato esplicitamente:
    /// il nome è configurabile (<see cref="ApiConfig.TokenEnv"/>) e la regola non deve indovinarlo.
    /// </summary>
    /// <param name="readEnvironment">Lettore della variabile: i test lo sostituiscono.</param>
    /// <exception cref="InvalidOperationException">
    /// Un indirizzo di ascolto non è un IP valido, oppure la rete è aperta e <paramref name="tokenEnv"/>
    /// è vuota o assente.
    /// </exception>
    public static string? RequiredFor(string[] listenAddresses, string tokenEnv, Func<string, string?> readEnvironment)
    {
        ArgumentNullException.ThrowIfNull(listenAddresses);
        ArgumentNullException.ThrowIfNull(readEnvironment);

        bool opensNetwork = false;
        foreach (var address in listenAddresses)
        {
            if (IsLoopback(address))
            {
                continue;
            }

            if (!IsNetworkAddress(address))
            {
                throw new InvalidOperationException(
                    $"api.listen: indirizzo '{address}' non valido. Usa 127.0.0.1, ::1, "
                    + $"{AnyAddress} o un indirizzo IP.");
            }

            opensNetwork = true;
        }

        if (!opensNetwork)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(tokenEnv))
        {
            throw new InvalidOperationException("api.listen apre la rete ma api.token_env è vuoto.");
        }

        string? token = readEnvironment(tokenEnv);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("api.listen apre la rete: serve il token in " + tokenEnv);
        }

        return token;
    }

    /// <summary>Vero solo per gli indirizzi che restano sulla macchina: <c>127.0.0.1</c> e <c>::1</c>.</summary>
    private static bool IsLoopback(string? address) =>
        IPAddress.TryParse(address, out IPAddress? parsed) && IPAddress.IsLoopback(parsed);

    /// <summary>
    /// Vero per <c>0.0.0.0</c>, per il jolly <c>*</c> e per ogni IP parsabile che non sia loopback.
    /// Un nome host come <c>example.com</c> non è ammesso: la risoluzione DNS cambierebbe
    /// l'indirizzo reale dopo la verifica del token.
    /// </summary>
    private static bool IsNetworkAddress(string? address) =>
        address == AnyAddress || IPAddress.TryParse(address, out _);
}
