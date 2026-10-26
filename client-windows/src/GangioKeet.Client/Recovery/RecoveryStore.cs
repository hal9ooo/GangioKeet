using GangioKeet.Client.Context;
using GangioKeet.Client.Protocol;

namespace GangioKeet.Client.Recovery;

/// <summary>
/// Una dettatura fallita recuperabile: audio completo oppure risultato già trascritto, più il
/// contesto necessario a ritentare o copiare. Vive solo in RAM e scade.
/// </summary>
public sealed record RecoveryItem(
    Guid Id,
    byte[]? Pcm,
    ResultMessage? Result,
    StartMessage Start,
    ForegroundWindowInfo Target,
    string FailureCode,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Slot unico di recupero: una nuova dettatura fallita sostituisce la precedente (notificando),
/// la scadenza è valutata pigramente sull'orologio iniettato e il completamento di un retry
/// rimuove solo il proprio ID — un errore più recente non viene cancellato da un esito vecchio.
/// Nessun file: solo memoria, trasferita dallo snapshot della dettatura fallita.
/// </summary>
public sealed class RecoveryStore
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _retention;
    private readonly object _gate = new();

    private RecoveryItem? _current;

    public RecoveryStore(TimeProvider time, TimeSpan retention)
    {
        _time = time;
        _retention = retention;
    }

    public event EventHandler? Changed;

    public RecoveryItem? GetCurrent()
    {
        bool expired;
        RecoveryItem? current;
        lock (_gate)
        {
            expired = _current is not null && _current.ExpiresAt <= _time.GetUtcNow();
            if (expired)
            {
                _current = null;
            }

            current = _current;
        }

        if (expired)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return current;
    }

    public void Offer(RecoveryItem item)
    {
        DateTimeOffset expiresAt = _time.GetUtcNow() + _retention;
        lock (_gate)
        {
            _current = item with { ExpiresAt = expiresAt };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Discard()
    {
        bool had;
        lock (_gate)
        {
            had = _current is not null;
            _current = null;
        }

        if (had)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool RemoveIfCurrent(Guid id)
    {
        bool removed;
        lock (_gate)
        {
            removed = _current is not null && _current.Id == id;
            if (removed)
            {
                _current = null;
            }
        }

        if (removed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return removed;
    }

    public void RemoveExpired()
    {
        _ = GetCurrent();
    }
}
