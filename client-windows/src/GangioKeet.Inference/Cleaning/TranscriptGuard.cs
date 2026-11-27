using System.Text;

namespace GangioKeet.Inference.Cleaning;

/// <summary>
/// Decide se il testo del cleaner è la stessa frase trascritta, o un'altra cosa.
///
/// Misurato sulle dettature reali: un cleaner generalista può tradurre invece di
/// pulire ("呃，这些湖南。" -> "Questi sono.", senza warning). Il risultato viene
/// incollato senza essere letto, quindi una traduzione silenziosa è una dettatura
/// persa. Lo stesso vale per un output vuoto su una frase vera: è un guasto, non
/// una scelta. Un testo fatto solo di esitazioni può diventare vuoto, perché lì
/// il vuoto è il risultato corretto.
/// </summary>
public static class TranscriptGuard
{
    private static readonly HashSet<string> FillerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "ehm", "uhm", "mmh", "hmm", "mhm"
    };

    /// <summary>
    /// True quando l'output del cleaner si può usare. Quando è false,
    /// <paramref name="warning"/> spiega perché e il chiamante tiene il testo grezzo.
    /// </summary>
    public static bool Evaluate(string raw, string cleaned, out string warning)
    {
        warning = "";
        if (!HasLetters(raw)) return true;

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            if (IsFillerOnly(raw)) return true;
            warning = "cleaner_empty_output";
            return false;
        }

        if (!HasLetters(cleaned))
        {
            warning = "cleaner_language_switch";
            return false;
        }

        Classify(raw, out bool rawLatin, out bool rawOther);
        Classify(cleaned, out bool cleanedLatin, out bool cleanedOther);
        bool rawHasScript = rawLatin || rawOther;
        bool cleanedHasScript = cleanedLatin || cleanedOther;
        bool overlapping = (rawLatin && cleanedLatin) || (rawOther && cleanedOther);
        if (rawHasScript && cleanedHasScript && !overlapping)
        {
            warning = "cleaner_language_switch";
            return false;
        }

        return true;
    }

    private static bool HasLetters(string text) => text.Any(char.IsLetter);

    private static bool IsFillerOnly(string raw)
    {
        bool sawWord = false;
        foreach (string token in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string word = token.Trim(',', '.', ';', ':', '!', '?', '…');
            if (word.Length == 0) continue;
            if (!FillerWords.Contains(word)) return false;
            sawWord = true;
        }
        return sawWord;
    }

    private static void Classify(string text, out bool latin, out bool other)
    {
        latin = other = false;
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            if (IsLatin(rune)) latin = true;
            else other = true;
        }
    }

    private static bool IsLatin(Rune rune)
    {
        int value = rune.Value;
        return value is (>= 0x0041 and <= 0x007A)
            or (>= 0x00C0 and <= 0x024F)
            or (>= 0x1E00 and <= 0x1EFF);
    }
}
