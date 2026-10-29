namespace GangioKeet.Client.Protocol;

/// <summary>Stato terminale della sessione dettato, completato una sola volta.</summary>
public enum DictationStatus
{
    Result,
    Error,
    Aborted,
}

/// <summary>
/// Errore terminale della dettatura locale. <see cref="Code"/> è un codice
/// stabile dell'app o del backend locale (es. <c>start_failed</c>,
/// <c>backpressure</c>, <c>aborted</c>).
/// </summary>
public sealed record DictationError(string Code, string Message, bool Retryable, int? HttpStatus = null);

/// <summary>
/// Esito terminale della sessione: <see cref="Result"/> ed <see cref="Error"/> si escludono a
/// vicenda; <see cref="Status"/> è sempre valorizzato. Il terminale si completa una sola volta.
/// </summary>
public sealed record DictationOutcome(DictationStatus Status, ResultMessage? Result, DictationError? Error);
