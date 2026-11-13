using System.Text.RegularExpressions;

namespace GangioKeet.Inference.Cleaning;

/// <summary>
/// Riscrive i termini tecnici dettati. Le voci canoniche normalizzano
/// maiuscole e spaziatura di un termine già scritto bene; gli override
/// fonetici traducono la forma sentita ("ciper inference") nella grafia
/// corretta ("Cheaper Inference").
///
/// Le chiavi più lunghe passano per prime: "ci per inference" non deve essere
/// mangiato da una regola più corta che combacia su una sua sottostringa.
/// </summary>
public static class GlossaryNormalizer
{
    public static string Normalize(string text, CleanupPolicy policy, string? profile = null)
    {
        if (profile is "code" or "terminal") return text;
        foreach ((string from, string to) in policy.Glossary
            .Select(x => (From: x.Key.Trim(), To: x.Value))
            .Where(x => x.From.Length > 0 && x.To.Length > 0)
            .OrderByDescending(x => x.From.Length))
        {
            string pattern = @"(?<!\w)" + string.Join(@"\s+", from.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(Regex.Escape)) + @"(?!\w)";
            text = Regex.Replace(text, pattern, _ => to, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        return text;
    }
}
