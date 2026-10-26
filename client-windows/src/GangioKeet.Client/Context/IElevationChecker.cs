namespace GangioKeet.Client.Context;

/// <summary>
/// Verifica se UIPI bloccherebbe l'invio di input alla finestra target (guardrail 3.7).
/// Lo stato non verificabile (target o processo corrente non ispezionabili) viene
/// trattato in modo conservativo come blocco attivo: mai un successo inventato.
/// </summary>
public interface IElevationChecker
{
    bool IsInjectionBlocked(ForegroundWindowInfo target);
}
