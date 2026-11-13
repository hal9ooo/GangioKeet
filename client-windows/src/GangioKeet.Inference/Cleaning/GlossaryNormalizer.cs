using System.Text;
using System.Text.RegularExpressions;

namespace GangioKeet.Inference.Cleaning;

/// <summary>
/// Riscrive i termini tecnici dettati. Le voci canoniche normalizzano
/// maiuscole e spaziatura di un termine già scritto bene; gli override
/// fonetici traducono la forma sentita ("ciper inference") nella grafia
/// corretta ("Cheaper Inference").
///
/// Le correzioni contestuali (<c>contextual_corrections</c>) scattano solo se
/// l'intorno corrisponde alla guardia: "codecs" diventa "Codex" in un elenco
/// di agenti, ma "i codecs video" resta com'è. Un falso positivo costa più di
/// una correzione mancata, perché il testo finisce dritto in un prompt.
///
/// Le chiavi più lunghe passano per prime: "ci per inference" non deve essere
/// mangiato da una regola più corta che combacia su una sua sottostringa.
/// </summary>
public static class GlossaryNormalizer
{
    private const int ContextWindow = 48;

    public static string Normalize(string text, CleanupPolicy policy, string? profile = null)
    {
        if (profile is "code" or "terminal") return text;
        foreach ((string from, string to) in policy.Glossary
            .Select(x => (From: x.Key.Trim(), To: x.Value))
            .DistinctBy(x => x.From, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.From.Length > 0 && x.To.Length > 0)
            .OrderByDescending(x => x.From.Length))
        {
            text = Regex.Replace(text, Pattern(from), _ => to, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        foreach (ContextualCorrection rule in policy.ContextualCorrections
            .Where(x => !string.IsNullOrWhiteSpace(x.From) && x.To.Length > 0)
            .OrderByDescending(x => x.From.Trim().Length))
        {
            string from = rule.From.Trim();
            Regex? guard = string.IsNullOrWhiteSpace(rule.Context)
                ? null
                : new Regex(rule.Context, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            text = Regex.Replace(text, Pattern(from), match => guard is null || InContext(text, match, guard)
                ? rule.To
                : match.Value,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }
        return text;
    }

    private static string Pattern(string term) =>
        @"(?<!\w)" + string.Join(@"\s+", term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(Regex.Escape)) + @"(?!\w)";

    private static bool InContext(string text, Match match, Regex guard)
    {
        int start = Math.Max(0, match.Index - ContextWindow);
        int end = Math.Min(text.Length, match.Index + match.Length + ContextWindow);
        StringBuilder window = new(text.Substring(start, match.Index - start));
        // La forma sentita è sostituita da un segnaposto: la guardia può
        // ancorarsi a ciò che la precede e a ciò che la segue.
        window.Append(" word ");
        window.Append(text.AsSpan(match.Index + match.Length, end - match.Index - match.Length));
        return guard.IsMatch(window.ToString());
    }
}
