namespace GangioKeet.Client.Context;

using GangioKeet.Client.Configuration;

public static class ProfileResolver
{
    public static DictationProfile Resolve(string processName, string windowTitle, ClientConfig cfg)
    {
        if (MatchesExe(processName, cfg.ContextProfiles.Code)) return DictationProfile.Code;
        if (MatchesExe(processName, cfg.ContextProfiles.Terminal)) return DictationProfile.Terminal;
        if (MatchesExe(processName, cfg.ContextProfiles.Chat)) return DictationProfile.Chat;
        if (MatchesExe(processName, cfg.ContextProfiles.Email)) return DictationProfile.Email;
        if (MatchesExe(processName, cfg.ContextProfiles.Document)) return DictationProfile.Document;

        if (MatchesExe(processName, cfg.BrowserProfiles.Browsers))
        {
            return ResolveBrowser(windowTitle, cfg.BrowserProfiles.Rules);
        }

        return DictationProfile.Default;
    }

    public static string ToWire(DictationProfile profile)
    {
        return profile switch
        {
            DictationProfile.Code => "code",
            DictationProfile.Terminal => "terminal",
            DictationProfile.Chat => "chat",
            DictationProfile.Email => "email",
            DictationProfile.Document => "document",
            DictationProfile.Default => "default",
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown profile"),
        };
    }

    private static DictationProfile ResolveBrowser(string windowTitle, BrowserRulesConfig rules)
    {
        if (ContainsKeyword(windowTitle, rules.Email)) return DictationProfile.Email;
        if (ContainsKeyword(windowTitle, rules.Chat)) return DictationProfile.Chat;
        if (ContainsKeyword(windowTitle, rules.Code)) return DictationProfile.Code;
        if (ContainsKeyword(windowTitle, rules.Document)) return DictationProfile.Document;
        return DictationProfile.Default;
    }

    private static bool ContainsKeyword(string windowTitle, string[] keywords)
    {
        foreach (string keyword in keywords)
        {
            if (windowTitle.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesExe(string processName, string[] entries)
    {
        if (string.IsNullOrWhiteSpace(processName) || entries.Length == 0)
        {
            return false;
        }

        string name = NormalizeExeName(processName);
        foreach (string entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (string.Equals(name, NormalizeExeName(entry), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeExeName(string value)
    {
        string name = value.Trim();
        const string suffix = ".exe";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^suffix.Length];
        }

        return name;
    }
}
