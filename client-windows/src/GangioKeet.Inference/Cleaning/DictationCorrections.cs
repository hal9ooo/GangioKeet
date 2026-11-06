using System.Text.RegularExpressions;

namespace GangioKeet.Inference.Cleaning;

/// <summary>
/// Deterministic corrections applied during light cleaning, after the language model.
///
/// Why it exists: the cleaner is a small model and some errors are systematic, so they must not
/// depend on it. Measured on 339 real transcripts, "la gente" was never a reference to people --
/// it was the transcription of "l'agente" every time, including the elided prepositions
/// ("nella gente" -> "nell'agente").
///
/// The rules live in the cleanup policy (<c>elision_corrections</c>), so a new systematic error
/// is a YAML entry, not a code change. Keep-markers are shared across the rules of a policy: when
/// the sentence is about people ("molta gente", "la gente comune") no rule fires at all, which is
/// what keeps genuine references intact.
/// </summary>
public static class DictationCorrections
{
    public static string Apply(string text, CleanupPolicy policy, string? profile = null)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Same rule as the glossary normalizer: never rewrite technical profiles.
        if (profile is "code" or "terminal") return text;

        List<ElisionCorrection> rules = policy.ElisionCorrections
            .Where(rule => rule.From.Trim().Length > 0 && rule.To.Length > 0)
            .OrderByDescending(rule => rule.From.Trim().Length)
            .ToList();
        if (rules.Count == 0) return text;

        string[] keepBefore = Markers(rules.SelectMany(rule => rule.KeepWhenPrecededBy));
        string[] keepAfter = Markers(rules.SelectMany(rule => rule.KeepWhenFollowedBy));

        foreach (ElisionCorrection rule in rules)
        {
            string from = rule.From.Trim();
            string pattern = @"(?<!\w)" + string.Join(@"\s+", from
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Regex.Escape)) + @"(?!\w)";
            text = Regex.Replace(
                text,
                pattern,
                match => KeepsOriginal(text, match, keepBefore, keepAfter)
                    ? match.Value
                    : MatchCase(match.Value, rule.To),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }

        return text;
    }

    private static string[] Markers(IEnumerable<string> markers) => markers
        .Select(marker => marker.Trim().ToLowerInvariant())
        .Where(marker => marker.Length > 0)
        .Distinct()
        .ToArray();

    private static bool KeepsOriginal(string text, Match match, string[] keepBefore, string[] keepAfter)
    {
        if (keepBefore.Length > 0)
        {
            int start = Math.Max(0, match.Index - 24);
            string before = text[start..match.Index].ToLowerInvariant();
            if (keepBefore.Any(marker => before.Contains(marker, StringComparison.Ordinal)))
                return true;
        }

        if (keepAfter.Length > 0)
        {
            int end = Math.Min(text.Length, match.Index + match.Length + 16);
            string after = text[(match.Index + match.Length)..end].TrimStart().ToLowerInvariant();
            if (keepAfter.Any(marker => after.StartsWith(marker, StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    private static string MatchCase(string original, string replacement)
    {
        if (original.Length == 0 || replacement.Length == 0) return replacement;
        return char.IsUpper(original[0])
            ? char.ToUpperInvariant(replacement[0]) + replacement[1..]
            : replacement;
    }
}
