using System.Globalization;
using GangioKeet.Client.Configuration;

namespace GangioKeet.Client.Ui;

/// <summary>
/// Formattazione e validazione pure della finestra impostazioni: niente WPF,
/// così le regole si testano senza un messaggio UI.
/// </summary>
public static class EndpointFormatPolicy
{
    public static bool IsValidHost(string? host) => EndpointHost.IsValid(host);

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 40)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    public static string Latency(double? milliseconds)
        => milliseconds is null
            ? "—"
            : $"{Math.Round(milliseconds.Value, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)} ms";

    public static string Ready(bool? ready, bool? busy, bool? draining)
    {
        if (ready is null && busy is null && draining is null)
        {
            return "—";
        }

        if (ready == true)
        {
            return "ready";
        }

        if (busy == true)
        {
            return "busy";
        }

        return draining == true ? "draining" : "non pronto";
    }

    public static string Status(bool reachable, bool measured)
        => !measured ? "mai verificato" : reachable ? "online" : "offline";

    public static bool HasDuplicate(IReadOnlyList<EndpointConfig> endpoints)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return endpoints.Any(endpoint => !seen.Add(endpoint.Key));
    }

    /// <summary>
    /// I nomi sono l'etichetta che l'utente vede e che il loader richiede
    /// unici: due righe con lo stesso nome renderebbero il file non caricabile.
    /// </summary>
    public static bool HasDuplicateName(IReadOnlyList<EndpointConfig> endpoints)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return endpoints.Any(endpoint => !seen.Add(endpoint.Name.Trim()));
    }

    public static int? ParsePort(string? text)
        => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            && port is >= 1 and <= 65535
                ? port
                : null;

    /// <summary>
    /// Porta la riga <paramref name="index"/> a <c>primary</c> e retrocede ogni
    /// altra. Restituisce lo stesso elenco, mutato: la finestra lavora su copie.
    /// </summary>
    public static IReadOnlyList<EndpointConfig> SetPrimary(
        IReadOnlyList<EndpointConfig> endpoints,
        int index)
    {
        for (int i = 0; i < endpoints.Count; i++)
        {
            endpoints[i].Role = i == index ? "primary" : "secondary";
        }

        return endpoints;
    }
}
