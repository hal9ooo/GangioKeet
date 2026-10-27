namespace GangioKeet.Client.Paste;

/// <summary>Contenuto della clipboard salvato prima del paste: testo e immagine DIB (screenshot) insieme.</summary>
public sealed record ClipboardSnapshot(string? Text, byte[]? Dib)
{
    public static ClipboardSnapshot Empty { get; } = new(null, null);

    public bool IsEmpty => Text is null && Dib is null;
}

public enum PasteStatus
{
    Pasted,
    SkippedEmpty,
    FocusLost,
    BlockedUipi,
    ClipboardUnavailable,
    InjectionFailed,
    Cancelled,
}

/// <summary>
/// Esito reale del paste. <see cref="TextLeftInClipboard"/> dice se il testo è ancora negli appunti
/// (nessun ripristino avvenuto): non implica che l'app lo abbia letto.
/// </summary>
public sealed record PasteOutcome(PasteStatus Status, bool TextLeftInClipboard);

/// <summary>Servizio clipboard con sequence number: il ripristino avviene solo se nessuno ha copiato altro.</summary>
public interface IClipboardService
{
    ClipboardSnapshot Backup();

    /// <summary>Pubblica il testo e restituisce il sequence number successivo alla pubblicazione.</summary>
    uint SetText(string text);

    /// <summary>Ripristina solo se la clipboard è ancora quella pubblicata.</summary>
    bool TryRestore(ClipboardSnapshot snapshot, uint publishedSequence);
}

/// <summary>Iniezione della combo di paste. False se SendInput inserisce meno eventi del previsto.</summary>
public interface IInputSimulator
{
    bool SendCombo(string combo);
}

/// <summary>
/// Marshaler STA per le API clipboard. In test si usa un dispatcher inline.
/// </summary>
public interface IPasteDispatcher
{
    Task<T> InvokeAsync<T>(Func<T> work, CancellationToken ct);
}
