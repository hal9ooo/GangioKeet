using System.Security.Cryptography;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GangioKeet.Inference.Cleaning;

/// <summary>
/// Riscrittura di una forma sentita, con una guardia di contesto. La regola
/// scatta solo quando l'intorno corrisponde a <see cref="Context"/>: è così che
/// "con Cloud" diventa "con Claude" senza toccare "tutto cloud", e "codecs"
/// diventa "Codex" solo in un elenco di agenti.
/// </summary>
public sealed class ContextualCorrection
{
public string From { get; set; } = "";
public string To { get; set; } = "";
/// <summary>Espressione regolare C# applicata all'intorno; vuota = nessuna guardia.</summary>
public string Context { get; set; } = "";
}

public sealed class ElisionCorrection
{
public string From { get; set; } = "";
public string To { get; set; } = "";
public List<string> KeepWhenPrecededBy { get; set; } = [];
public List<string> KeepWhenFollowedBy { get; set; } = [];
}

public sealed class CleanupPolicy
{
    public List<string> CanonicalTerms { get; set; } = [];
    public Dictionary<string, string> PhoneticOverrides { get; set; } = [];
public List<ElisionCorrection> ElisionCorrections { get; set; } = [];
public List<ContextualCorrection> ContextualCorrections { get; set; } = [];
    public double LlmTimeoutSeconds { get; set; } = 180;
    [YamlIgnore] public TimeSpan LlmTimeout => TimeSpan.FromSeconds(LlmTimeoutSeconds);
    [YamlIgnore] public string Sha256 { get; private set; } = "";

    public static CleanupPolicy Load(string path)
    {
        string text = File.ReadAllText(path).Replace("\r\n", "\n");
        CleanupPolicy policy = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build().Deserialize<CleanupPolicy>(text) ?? throw new InvalidDataException("Empty cleanup policy.");
        if (!double.IsFinite(policy.LlmTimeoutSeconds) || policy.LlmTimeoutSeconds <= 0 || policy.LlmTimeoutSeconds > 300)
            throw new InvalidDataException("Cleanup timeout must be between 0 and 300 seconds.");
        if (policy.CanonicalTerms is null || policy.PhoneticOverrides is null || policy.ElisionCorrections is null
            || policy.ContextualCorrections is null)
            throw new InvalidDataException("Cleanup glossary must not be null.");
        policy.Sha256 = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        return policy;
    }

    public IReadOnlyDictionary<string, string> Glossary => CanonicalTerms.ToDictionary(x => x, x => x)
        .Concat(PhoneticOverrides).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Last().Value);
}
