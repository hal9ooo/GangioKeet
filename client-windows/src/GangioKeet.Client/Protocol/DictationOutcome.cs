namespace GangioKeet.Client.Protocol;

/// <summary>Stato terminale della sessione dettato, completato una sola volta.</summary>
public enum DictationStatus
{
    Result,
    Error,
    Aborted,
}

/// <summary>
/// Errore terminale. <see cref="Code"/> può essere un codice server (frame error) oppure
/// un codice locale della sessione: <c>timeout</c>, <c>send_failed</c>, <c>protocol</c>,
/// <c>closed</c>, <c>aborted</c>, <c>receive_failed</c>. I vocaboli dei fallimenti di
/// handshake restano in <see cref="ConnectFailure"/> (unauthorized/forbidden/not_found/
/// upgrade_required/unexpected_response/connect_failed).
/// </summary>
public sealed record DictationError(string Code, string Message, bool Retryable, int? HttpStatus = null);

/// <summary>
/// Esito terminale della sessione: <see cref="Result"/> ed <see cref="Error"/> si escludono a
/// vicenda; <see cref="Status"/> è sempre valorizzato. Il terminale si completa una sola volta.
/// </summary>
public sealed record DictationOutcome(DictationStatus Status, ResultMessage? Result, DictationError? Error);

/// <summary>Esito della connessione: se <see cref="Connected"/> è false, <see cref="Failure"/> non è null.</summary>
public sealed record SessionConnectOutcome(bool Connected, ConnectFailure? Failure);
