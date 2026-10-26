namespace GangioKeet.Client.Protocol;

/// <summary>
/// Policy pura del fallback REST: senza I/O ritenta nulla e non decide l'esito, dice solo se un
/// fallimento di connessione WebSocket ammette un tentativo REST. Vero unicamente per HTTP
/// 404/405/426 o per un handshake 200 senza upgrade, e solo quando ancora non è stato inviato
/// né lo start né audio. Falso per 401/403, DNS/rifiuto/timeout, busy, payload_too_large e per
/// qualunque fallimento successivo allo start.
/// </summary>
public sealed class RestFallbackPolicy
{
    public bool CanFallback(ConnectFailure failure, bool anyStartOrAudioSent)
    {
        if (anyStartOrAudioSent || failure is null)
        {
            return false;
        }

        return failure.HttpStatus is 404 or 405 or 426
            || (failure.Code == "unexpected_response" && failure.HttpStatus == 200);
    }
}
