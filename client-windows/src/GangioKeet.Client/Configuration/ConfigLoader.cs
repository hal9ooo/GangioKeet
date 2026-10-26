using System.IO;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace GangioKeet.Client.Configuration;

public sealed class ConfigException : Exception
{
    public ConfigException(string message)
        : base(message)
    {
    }
}

public static class ConfigLoader
{
    private static readonly IDeserializer YamlDecoder = new DeserializerBuilder().Build();

    public static ClientConfig Load(string? explicitPath = null, string? appDataBase = null)
    {
        string path = explicitPath is not null
            ? Path.GetFullPath(explicitPath)
            : Path.Combine(
                appDataBase ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GangioKeet",
                "client_config.yaml");

        ClientConfig config;
        if (!File.Exists(path))
        {
            if (explicitPath is not null)
            {
                throw new ConfigException($"Configuration file not found: {path}");
            }
            config = new ClientConfig();
        }
        else
        {
            string text = File.ReadAllText(path);
            try
            {
                config = YamlDecoder.Deserialize<ClientConfig>(text) ?? new ClientConfig();
            }
            catch (YamlException e)
            {
                throw new ConfigException($"Invalid configuration file '{path}': {e.Message}");
            }
        }

        Validate(config, path);
        ApiCredential.Resolve(config.Server.ApiKeyEnv, Environment.GetEnvironmentVariable);
        return config;
    }

    public static void Validate(ClientConfig config, string? sourcePath = null)
    {
        ValidateServer(config.Server, sourcePath);
        ValidateHotkey(config.Hotkey, sourcePath);
        ValidateAudio(config.Audio, sourcePath);
        ValidatePaste(config.Paste, sourcePath);
        ValidateUi(config.Ui, sourcePath);
        ValidateRecovery(config.Recovery, sourcePath);
        ValidateContextProfiles(config.ContextProfiles, sourcePath);
        ValidateBrowserProfiles(config.BrowserProfiles, sourcePath);
    }

    private static string Where(string? sourcePath, string detail)
        => sourcePath is null ? detail : $"{detail} (in '{sourcePath}')";

    private static void Fail(string? sourcePath, string detail)
        => throw new ConfigException(Where(sourcePath, detail));

    private static void RequireNonEmptyString(string? value, string field, string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            Fail(sourcePath, $"{field} must be a non-empty string");
        }
    }

    private static void RequirePositive(double value, string field, string? sourcePath)
    {
        if (value <= 0)
        {
            Fail(sourcePath, $"{field} must be positive");
        }
    }

    private static void RequirePositive(int value, string field, string? sourcePath)
    {
        if (value <= 0)
        {
            Fail(sourcePath, $"{field} must be positive");
        }
    }

    private static void ValidateServer(ServerConfig server, string? sourcePath)
    {
        RequireNonEmptyString(server.Host, "server.host", sourcePath);
        if (server.Port is < 1 or > 65535)
        {
            Fail(sourcePath, "server.port must be between 1 and 65535");
        }
        if (server.Transport is not ("websocket" or "rest"))
        {
            Fail(sourcePath, "server.transport must be 'websocket' or 'rest'");
        }
        RequirePositive(server.ConnectTimeoutSeconds, "server.connect_timeout_seconds", sourcePath);
        RequirePositive(server.ResponseTimeoutSeconds, "server.response_timeout_seconds", sourcePath);
        RequirePositive(server.UploadTimeoutSeconds, "server.upload_timeout_seconds", sourcePath);
        RequirePositive(server.RestResponseTimeoutSeconds, "server.rest_response_timeout_seconds", sourcePath);
        RequirePositive(server.HealthCheckIntervalSeconds, "server.health_check_interval_seconds", sourcePath);
        if (server.ApiKeyEnv is not null && string.IsNullOrWhiteSpace(server.ApiKeyEnv))
        {
            Fail(sourcePath, "server.api_key_env must be null or a non-empty environment variable name");
        }
    }

    private static void ValidateHotkey(HotkeyConfig hotkey, string? sourcePath)
    {
        if (hotkey.Mode is not ("push_to_talk" or "toggle"))
        {
            Fail(sourcePath, "hotkey.mode must be 'push_to_talk' or 'toggle'");
        }
        ValidateKeyName(hotkey.Key, allowEscape: false, sourcePath);
        ValidateKeyName(hotkey.AbortKey, allowEscape: true, sourcePath);
        RequirePositive(hotkey.DoubleTapMaxMs, "hotkey.double_tap_max_ms", sourcePath);
        RequirePositive(hotkey.DoubleTapGapMs, "hotkey.double_tap_gap_ms", sourcePath);
    }

    private static void ValidateKeyName(string value, bool allowEscape, string? sourcePath)
    {
        var key = value.Trim().ToLowerInvariant();
        if (key == "alt_r")
        {
            Fail(sourcePath, "hotkey key 'alt_r' is rejected: it conflicts with the AltGr modifier");
        }

        // capslock è ammesso solo come hotkey principale, non come abort_key.
        bool isCapsLock = !allowEscape && key is "capslock" or "caps_lock";
        if (isCapsLock
            || key == "mouse_x1"
            || (allowEscape && key == "escape")
            || (key.StartsWith('f') && int.TryParse(key.AsSpan(1), out var number) && number >= 1 && number <= 24 && key.Length == $"f{number}".Length))
        {
            return;
        }

        Fail(
            sourcePath,
            $"hotkey key '{value}' is not valid: expected f1-f24, capslock or mouse_x1"
            + (allowEscape ? ", or 'escape' for hotkey.abort_key" : ""));
    }

    private static void ValidateAudio(AudioConfig audio, string? sourcePath)
    {
        if (audio.InputDevice is not null && string.IsNullOrWhiteSpace(audio.InputDevice))
        {
            Fail(sourcePath, "audio.input_device must be null or a non-empty device id");
        }
        if (audio.SampleRate != 16000 || audio.Channels != 1 || audio.ChunkDurationMs != 100)
        {
            Fail(
                sourcePath,
                "audio PCM contract violated: audio.sample_rate must be 16000, audio.channels must be 1 "
                + "and audio.chunk_duration_ms must be 100");
        }
        RequirePositive(audio.ReadyTimeoutSeconds, "audio.ready_timeout_seconds", sourcePath);
    }

    private static void ValidatePaste(PasteConfig paste, string? sourcePath)
    {
        RequireNonEmptyString(paste.DefaultCombo, "paste.default_combo", sourcePath);
        RequireNonEmptyString(paste.TerminalCombo, "paste.terminal_combo", sourcePath);
        foreach (var app in paste.TerminalApps)
        {
            RequireNonEmptyString(app, "paste.terminal_apps entries", sourcePath);
        }
        RequirePositive(paste.ClipboardRestoreDelayMs, "paste.clipboard_restore_delay_ms", sourcePath);
    }

    private static void ValidateUi(UiConfig ui, string? sourcePath)
    {
        if (ui.PillPosition is not ("cursor" or "center_bottom"))
        {
            Fail(sourcePath, "ui.pill_position must be 'cursor' or 'center_bottom'");
        }
    }

    private static void ValidateRecovery(RecoveryConfig recovery, string? sourcePath)
    {
        RequirePositive(recovery.RetentionSeconds, "recovery.retention_seconds", sourcePath);
    }

    private static void ValidateContextProfiles(ContextProfilesConfig profiles, string? sourcePath)
    {
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AssertProfile(profiles.Code, "code");
        AssertProfile(profiles.Terminal, "terminal");
        AssertProfile(profiles.Chat, "chat");
        AssertProfile(profiles.Email, "email");
        AssertProfile(profiles.Document, "document");

        void AssertProfile(string[]? entries, string profile)
        {
            if (entries is null)
            {
                Fail(sourcePath, $"context_profiles.{profile} must be a list");
            }
            foreach (var entry in entries!)
            {
                RequireNonEmptyString(entry, $"context_profiles.{profile} entries", sourcePath);
                if (!owners.TryAdd(entry, profile))
                {
                    Fail(
                        sourcePath,
                        $"exe '{entry}' is assigned to more than one context profile "
                        + $"('{owners[entry]}' and '{profile}')");
                }
            }
        }
    }

    private static void ValidateBrowserProfiles(BrowserProfilesConfig browserProfiles, string? sourcePath)
    {
        if (browserProfiles.Browsers is null || browserProfiles.Rules is null)
        {
            Fail(sourcePath, "browser_profiles.browsers and browser_profiles.rules must be set");
        }
        AssertList(browserProfiles.Browsers!, "browser_profiles.browsers", sourcePath);
        var browsers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var browser in browserProfiles.Browsers!)
        {
            if (!browsers.Add(browser))
            {
                Fail(sourcePath, $"browser '{browser}' is listed more than once in browser_profiles.browsers");
            }
        }
        AssertList(browserProfiles.Rules!.Email, "browser_profiles.rules.email", sourcePath);
        AssertList(browserProfiles.Rules!.Chat, "browser_profiles.rules.chat", sourcePath);
        AssertList(browserProfiles.Rules!.Code, "browser_profiles.rules.code", sourcePath);
        AssertList(browserProfiles.Rules!.Document, "browser_profiles.rules.document", sourcePath);
    }

    private static void AssertList(string[]? entries, string field, string? sourcePath)
    {
        if (entries is null)
        {
            Fail(sourcePath, $"{field} must be a list");
        }
        if (entries!.Length == 0)
        {
            Fail(sourcePath, $"{field} must not be empty");
        }
        foreach (var entry in entries!)
        {
            RequireNonEmptyString(entry, $"{field} entries", sourcePath);
        }
    }
}
