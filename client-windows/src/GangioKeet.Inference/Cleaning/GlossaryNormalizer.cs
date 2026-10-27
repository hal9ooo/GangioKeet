using System.Text.RegularExpressions;

namespace GangioKeet.Inference.Cleaning;

public static class GlossaryNormalizer
{
    public static string Normalize(string text, CleanupPolicy policy, string? profile = null)
    {
        if (profile is "code" or "terminal") return text;
        foreach (string term in policy.CanonicalTerms.Select(x => x.Trim()).Where(x => x.Length > 0)
                     .Distinct().OrderByDescending(x => x.Length))
        {
            string pattern = @"(?<!\w)" + string.Join(@"\s+", term.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(Regex.Escape)) + @"(?!\w)";
            text = Regex.Replace(text, pattern, _ => term, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }
        return text;
    }
}
