using System.Text.RegularExpressions;

namespace GangioKeet.Inference.Cleaning;

/// <summary>
/// Riconosce un'eco dell'ASR su una registrazione senza voce: poche parole,
/// tutte molto comuni, su un audio che non raggiunge mai il livello di voce.
///
/// Misurato su tutte le 1331 dettature reali del manifest: i testi brevi e
/// comuni sono 4, e tutti e 4 stanno sotto il 1% del fondo scala (picco 0, 32,
/// 304, 312 su 32768). Nessun testo vero, nemmeno a volume bassissimo, sta
/// sotto quella soglia: le frasi vere più deboli ("Vuoi un attimo controllare
/// che cosa ha fatto?" a -70 dBFS) hanno picchi molto più alti, quindi la
/// regola non tocca la voce flebile.
///
/// Il caso peggiore era "Yeah." su una registrazione al 75% di zeri: la guarda
/// attuale lo lasciava passare perché controllava solo il testo vuoto.
/// La stessa AI ha prodotto "you" su 0,04 s di silenzio assoluto.
/// </summary>
public static class SilenceGuard
{
    /// <summary>
    /// 1% del fondo scala PCM16, la stessa soglia di voce usata da
    /// <c>AudioSignalTracker.SpeechFloor</c>: sotto, il blocco è rumore.
    /// </summary>
    public const int SpeechFloor = 328;

    private const int MaximumWords = 3;
    private const int MaximumCharacters = 20;

    private static readonly Regex ShortText = new(
        @"^\s*(?:(?:thank\s+you|yeah|so|you|ok|okay|hmm|uh|um|bye|hello|hi|grazie|ciao|sì|si|no|vai|bene)[.,!?…]?\s*)+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True se il testo è l'eco di un'ASR su una registrazione senza voce.
    /// </summary>
    /// <param name="text">Testo grezzo prodotto dall'ASR.</param>
    /// <param name="peak">Picco assoluto dell'audio PCM16, in unità di campione (0..32768).</param>
    public static bool IsHallucination(string text, int peak)
    {
        if (peak >= SpeechFloor) return false;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        if (trimmed.Length > MaximumCharacters) return false;
        string[] words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int significant = words.Count(token => token.Trim('.', ',', '!', '?', '…').Length > 0);
        if (significant == 0 || significant > MaximumWords) return false;
        return ShortText.IsMatch(trimmed);
    }

    /// <summary>
    /// Picco assoluto di un buffer PCM16 little endian, in unità di campione.
    /// </summary>
    public static int PeakOf(ReadOnlySpan<byte> pcm16)
    {
        int peak = 0;
        for (int i = 0; i + 1 < pcm16.Length; i += 2)
        {
            short sample = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            // Math.Abs(short.MinValue) eccede short: si lavora in int.
            int magnitude = sample == short.MinValue ? 32768 : Math.Abs((int)sample);
            if (magnitude > peak) peak = magnitude;
        }
        return peak;
    }
}
