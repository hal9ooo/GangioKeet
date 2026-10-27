using System.Security.Cryptography;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GangioKeet.Inference.Cleaning;

public sealed class CleanupPolicy
{
    public List<string> CanonicalTerms { get; set; } = [];
    public Dictionary<string, string> PhoneticOverrides { get; set; } = [];
    public double LlmTimeoutSeconds { get; set; } = 5;
    [YamlIgnore] public TimeSpan LlmTimeout => TimeSpan.FromSeconds(LlmTimeoutSeconds);
    [YamlIgnore] public string Sha256 { get; private set; } = "";

    public static CleanupPolicy Load(string path)
    {
        string text = File.ReadAllText(path).Replace("\r\n", "\n");
        CleanupPolicy policy = new DeserializerBuilder().WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build().Deserialize<CleanupPolicy>(text) ?? throw new InvalidDataException("Empty cleanup policy.");
        if (!double.IsFinite(policy.LlmTimeoutSeconds) || policy.LlmTimeoutSeconds <= 0 || policy.LlmTimeoutSeconds > 120)
            throw new InvalidDataException("Cleanup timeout must be between 0 and 120 seconds.");
        if (policy.CanonicalTerms is null || policy.PhoneticOverrides is null)
            throw new InvalidDataException("Cleanup glossary must not be null.");
        policy.Sha256 = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
        return policy;
    }

    public IReadOnlyDictionary<string, string> Glossary => CanonicalTerms.ToDictionary(x => x, x => x)
        .Concat(PhoneticOverrides).GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.Last().Value);
}
