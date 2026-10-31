using System.Text.RegularExpressions;

namespace GangioKeet.Inference.Cleaning;

/// <summary>
/// Deterministic, conservative cleanup for dictation. It removes only explicit
/// filled pauses and immediate repetitions; it never rewrites content, terms,
/// numbers or commands.
/// </summary>
public static class LightCleanup
{
    private const string Word = @"[\p{L}\p{N}_']+";
    private const string ShortWord = @"[\p{L}\p{N}_']{2,4}";
    private static readonly Regex Filler = new(
        $@"(?<![\p{{L}}\p{{N}}_'])(?:ehm|uhm|mmh|hmm|mhm)(?![\p{{L}}\p{{N}}_'])[ \t]*,?[ \t]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CommaRepeat = new(
        $@"(?<![\p{{L}}\p{{N}}_'])({Word})[ \t]*,[ \t]*\1(?![\p{{L}}\p{{N}}_'])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ShortRepeat = new(
        $@"(?<![\p{{L}}\p{{N}}_'])({ShortWord})[ \t]+\1(?![\p{{L}}\p{{N}}_'])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex WordPair = new(
        $@"(?<![\p{{L}}\p{{N}}_'])({Word})[ \t]+({Word})(?![\p{{L}}\p{{N}}_'])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> ProtectedReduplications = new(StringComparer.Ordinal)
    {
        "piano piano", "poco poco", "molto molto", "così così", "bene bene",
        "eccetera eccetera", "ciao ciao"
    };

    private static readonly HashSet<string> Articles = new(StringComparer.Ordinal)
    {
        "un", "una", "uno", "il", "lo", "la", "i", "gli", "le"
    };

    private static readonly HashSet<string> ArticulatedPrepositions = new(StringComparer.Ordinal)
    {
        "al", "allo", "alla", "ai", "agli", "alle",
        "dal", "dallo", "dalla", "dai", "dagli", "dalle",
        "nel", "nello", "nella", "nei", "negli", "nelle",
        "sul", "sullo", "sulla", "sui", "sugli", "sulle",
        "del", "dello", "della", "dei", "degli", "delle"
    };

    public static string Apply(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Length == 0) return raw;

        string text = Filler.Replace(raw, "");
        text = CollapseRepeated(text, CommaRepeat);
        text = CollapseRepeated(text, ShortRepeat);
        text = WordPair.Replace(text, KeepLastArticleOrPreposition);
        text = Regex.Replace(text, @"[ \t]{2,}", " ", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"[ \t]+([,.;:!?])", "$1", RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @",[ \t]*,", ",", RegexOptions.CultureInvariant);
        return text.Trim();
    }

    private static string CollapseRepeated(string text, Regex pattern)
    {
        string current = text;
        for (int i = 0; i < 10; i++)
        {
            string next = pattern.Replace(current, match =>
            {
                string value = match.Groups[1].Value;
                if (value.Length < 2 || value.All(char.IsDigit)) return match.Value;
                string pair = NormalizePair(match.Value);
                return ProtectedReduplications.Contains(pair) ? match.Value : value;
            });
            if (string.Equals(next, current, StringComparison.Ordinal)) return current;
            current = next;
        }
        return current;
    }

    private static string KeepLastArticleOrPreposition(Match match)
    {
        string first = match.Groups[1].Value;
        string second = match.Groups[2].Value;
        string firstLower = first.ToLowerInvariant();
        string secondLower = second.ToLowerInvariant();
        bool sameCategory = (Articles.Contains(firstLower) && Articles.Contains(secondLower))
            || (ArticulatedPrepositions.Contains(firstLower)
                && ArticulatedPrepositions.Contains(secondLower));
        if (!sameCategory) return match.Value;
        if (first.Length > 0 && char.IsUpper(first[0]) && second.Length > 0 && char.IsLower(second[0]))
            second = char.ToUpperInvariant(second[0]) + second[1..];
        return second;
    }

    private static string NormalizePair(string value) =>
        Regex.Replace(value.Replace(",", "", StringComparison.Ordinal), @"[ \t]+", " ",
            RegexOptions.CultureInvariant).Trim().ToLowerInvariant();
}
