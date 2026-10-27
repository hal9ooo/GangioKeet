using System.Net;
using System.Net.Sockets;

namespace GangioKeet.Client.Configuration;

/// <summary>
/// Validazione condivisa del campo <c>host</c> di un endpoint: accetta IPv4,
/// IPv6 e nomi host, rifiuta schema, porta o caratteri non ammessi. Il valore
/// usato negli URI mette tra parentesi quadre gli IPv6.
/// </summary>
public static class EndpointHost
{
    public static bool IsValid(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253)
        {
            return false;
        }

        if (IPAddress.TryParse(host, out IPAddress? address) && address is not null)
        {
            return address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6;
        }

        foreach (string label in host.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }

            foreach (char c in label)
            {
                if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static string ForUri(string host)
    {
        return IPAddress.TryParse(host, out IPAddress? address)
            && address.AddressFamily == AddressFamily.InterNetworkV6
            && !host.StartsWith('[')
                ? $"[{host}]"
                : host;
    }
}
