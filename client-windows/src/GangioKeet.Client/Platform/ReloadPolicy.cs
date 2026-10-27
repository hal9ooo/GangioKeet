using GangioKeet.Client.Configuration;
using GangioKeet.Client.Controller;

namespace GangioKeet.Client.Platform;

/// <summary>Esito di un tentativo di reload della configurazione.</summary>
public enum ReloadOutcome
{
    /// <summary>Config caricata e applicata immediatamente.</summary>
    Applied,

    /// <summary>Config caricata ma applicata solo al ritorno in Idle.</summary>
    Deferred,

    /// <summary>Config non valida: la configurazione precedente resta attiva.</summary>
    Invalid,
}

/// <summary>
/// Politica di reload: carica la nuova configurazione e decide se applicarla subito.
/// Se c'è una dettatura in corso, il reload è differito a quando il controller
/// torna in Idle. Una configurazione invalida non tocca quella funzionante.
/// </summary>
public static class ReloadPolicy
{
    /// <summary>Path/mode changes require a prepared replacement runtime.
    /// Callers must keep the current runtime until replacement load succeeds,
    /// and use Evaluate to defer replacement during an active dictation.</summary>
    public static bool RequiresRuntimeReload(ClientConfig current, ClientConfig replacement) =>
        current.Inference.Mode != replacement.Inference.Mode
        || current.Inference.Backend != replacement.Inference.Backend
        || !string.Equals(current.Inference.ModelSelectionPath, replacement.Inference.ModelSelectionPath, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(current.Inference.CleanupPolicyPath, replacement.Inference.CleanupPolicyPath, StringComparison.OrdinalIgnoreCase);

    public static ReloadOutcome Evaluate(ClientState currentState)
    {
        return currentState == ClientState.Idle
            ? ReloadOutcome.Applied
            : ReloadOutcome.Deferred;
    }

    /// <summary>
    /// Tenta il caricamento: restituisce la nuova config o null se invalida.
    /// </summary>
    public static ClientConfig? TryLoad(string? explicitPath, FileLogger? logger)
    {
        try
        {
            ClientConfig config = ConfigLoader.Load(explicitPath);
            logger?.Write("config_reload", "Configuration reloaded successfully.");
            return config;
        }
        catch (ConfigException ex)
        {
            logger?.Write("config_reload_failed", ex.Message);
            return null;
        }
    }
}
