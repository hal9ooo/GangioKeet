using GangioKeet.Client.Configuration;
using GangioKeet.Client.Context;

namespace GangioKeet.Client.Paste;

/// <summary>
/// Transazione di paste: un solo paste alla volta, controllo target/UIPI prima di scrivere e di
/// nuovo subito prima di SendInput, ripristino della clipboard solo se nessuno ha copiato altro
/// (sequence number) e dopo il delay configurato. Se l'iniezione fallisce il testo resta disponibile
/// per il recupero; <see cref="PasteOutcome.TextLeftInClipboard"/> riporta la verità, senza mai
/// affermare che l'app abbia letto il testo.
/// </summary>
public sealed class PasteService
{
    private readonly IClipboardService _clipboard;
    private readonly IInputSimulator _input;
    private readonly IForegroundWindowService _foreground;
    private readonly IElevationChecker _elevation;
    private readonly IPasteDispatcher _dispatcher;
    private readonly PasteConfig _config;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PasteService(
        IClipboardService clipboard,
        IInputSimulator input,
        IForegroundWindowService foreground,
        IElevationChecker elevation,
        IPasteDispatcher dispatcher,
        PasteConfig config,
        TimeProvider time)
    {
        _clipboard = clipboard;
        _input = input;
        _foreground = foreground;
        _elevation = elevation;
        _dispatcher = dispatcher;
        _config = config;
        _time = time;
    }

    public async Task<PasteOutcome> PasteAsync(string text, ForegroundWindowInfo target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new PasteOutcome(PasteStatus.SkippedEmpty, TextLeftInClipboard: false);
        }

        if (!_foreground.IsAlive(target))
        {
            return new PasteOutcome(PasteStatus.FocusLost, TextLeftInClipboard: false);
        }

        if (_elevation.IsInjectionBlocked(target))
        {
            return new PasteOutcome(PasteStatus.BlockedUipi, TextLeftInClipboard: false);
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await PasteCoreAsync(text, target, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PasteOutcome> PasteCoreAsync(string text, ForegroundWindowInfo target, CancellationToken ct)
    {
        ClipboardSnapshot snapshot;
        uint sequence;
        try
        {
            snapshot = await _dispatcher.InvokeAsync(_clipboard.Backup, ct).ConfigureAwait(false);
            sequence = await _dispatcher.InvokeAsync(() => _clipboard.SetText(text), ct).ConfigureAwait(false);
        }
        catch (ClipboardUnavailableException)
        {
            return new PasteOutcome(PasteStatus.ClipboardUnavailable, TextLeftInClipboard: false);
        }

        if (_config.RefocusBeforePaste)
        {
            bool activated = _foreground.TryActivate(target.Handle);
            if (!activated || !StillOnTarget(target))
            {
                await RestoreAsync(snapshot, sequence).ConfigureAwait(false);
                return new PasteOutcome(PasteStatus.FocusLost, TextLeftInClipboard: false);
            }
        }
        else if (!StillOnTarget(target))
        {
            await RestoreAsync(snapshot, sequence).ConfigureAwait(false);
            return new PasteOutcome(PasteStatus.FocusLost, TextLeftInClipboard: false);
        }

        ForegroundWindowInfo current = _foreground.Read(_foreground.CaptureHandle());
        if (_elevation.IsInjectionBlocked(current))
        {
            return new PasteOutcome(PasteStatus.BlockedUipi, TextLeftInClipboard: true);
        }

        string combo = IsTerminal(target.ProcessName) ? _config.TerminalCombo : _config.DefaultCombo;
        if (!_input.SendCombo(combo))
        {
            return new PasteOutcome(PasteStatus.InjectionFailed, TextLeftInClipboard: true);
        }

        try
        {
            await Task.Delay(
                TimeSpan.FromMilliseconds(_config.ClipboardRestoreDelayMs),
                _time,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        bool restored = await RestoreAsync(snapshot, sequence).ConfigureAwait(false);
        return new PasteOutcome(PasteStatus.Pasted, TextLeftInClipboard: !restored);
    }

    private bool StillOnTarget(ForegroundWindowInfo target)
    {
        if (!_foreground.IsAlive(target))
        {
            return false;
        }

        nint handle = _foreground.CaptureHandle();
        ForegroundWindowInfo current = _foreground.Read(handle);
        return current.ProcessId == target.ProcessId && current.Handle == target.Handle;
    }

    /// <summary>
    /// Copia il testo negli appunti senza incollarlo e senza ripristinare il contenuto
    /// precedente: è un'azione esplicita dell'utente sul testo recuperato.
    /// </summary>
    public async Task<bool> CopyTextAsync(string text, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        try
        {
            await _dispatcher.InvokeAsync(() => _clipboard.SetText(text), ct).ConfigureAwait(false);
            return true;
        }
        catch (ClipboardUnavailableException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<bool> RestoreAsync(ClipboardSnapshot snapshot, uint sequence)
    {
        try
        {
            return await _dispatcher
                .InvokeAsync(() => _clipboard.TryRestore(snapshot, sequence), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ClipboardUnavailableException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private bool IsTerminal(string processName)
    {
        foreach (string terminal in _config.TerminalApps)
        {
            if (string.Equals(terminal, processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
