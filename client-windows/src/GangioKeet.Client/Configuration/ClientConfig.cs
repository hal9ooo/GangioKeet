using YamlDotNet.Serialization;

namespace GangioKeet.Client.Configuration;

public sealed class ClientConfig
{
    [YamlMember(Alias = "inference")]
    public InferenceConfig Inference { get; set; } = new();

    [YamlMember(Alias = "recording")]
    public RecordingConfig Recording { get; set; } = new();

    [YamlMember(Alias = "server")]
    public ServerConfig Server { get; set; } = new();

    /// <summary>
    /// Endpoint configurati. Dopo <see cref="ConfigLoader.Load"/> è sempre non
    /// nullo. Solo in remoto viene sintetizzato dal vecchio blocco server;
    /// una nuova installazione locale non costruisce endpoint fittizi.
    /// </summary>
    [YamlMember(Alias = "endpoints")]
    public List<EndpointConfig>? Endpoints { get; set; }

    [YamlMember(Alias = "hotkey")]
    public HotkeyConfig Hotkey { get; set; } = new();

    [YamlMember(Alias = "audio")]
    public AudioConfig Audio { get; set; } = new();

    [YamlMember(Alias = "paste")]
    public PasteConfig Paste { get; set; } = new();

    [YamlMember(Alias = "ui")]
    public UiConfig Ui { get; set; } = new();

    [YamlMember(Alias = "context_profiles")]
    public ContextProfilesConfig ContextProfiles { get; set; } = new();

    [YamlMember(Alias = "browser_profiles")]
    public BrowserProfilesConfig BrowserProfiles { get; set; } = new();

    [YamlMember(Alias = "recovery")]
    public RecoveryConfig Recovery { get; set; } = new();
}

public sealed class ServerConfig
{
    [YamlMember(Alias = "host")]
    public string Host { get; set; } = "192.168.1.167";

    [YamlMember(Alias = "scheme")]
    public string Scheme { get; set; } = "http";

    [YamlMember(Alias = "port")]
    public int Port { get; set; } = 8000;

    [YamlMember(Alias = "transport")]
    public string Transport { get; set; } = "websocket";

    [YamlMember(Alias = "connect_timeout_seconds")]
    public double ConnectTimeoutSeconds { get; set; } = 2.0;

    [YamlMember(Alias = "response_timeout_seconds")]
    public double ResponseTimeoutSeconds { get; set; } = 15.0;

    [YamlMember(Alias = "upload_timeout_seconds")]
    public double UploadTimeoutSeconds { get; set; } = 5.0;

    [YamlMember(Alias = "rest_response_timeout_seconds")]
    public double RestResponseTimeoutSeconds { get; set; } = 20.0;

    [YamlMember(Alias = "rest_fallback")]
    public bool RestFallback { get; set; } = true;

    [YamlMember(Alias = "health_check_interval_seconds")]
    public double HealthCheckIntervalSeconds { get; set; } = 30.0;

    [YamlMember(Alias = "health_check_timeout_seconds")]
    public double HealthCheckTimeoutSeconds { get; set; } = 2.0;

    [YamlMember(Alias = "switch_margin_percent")]
    public int SwitchMarginPercent { get; set; } = 25;

    [YamlMember(Alias = "api_key_env")]
    public string? ApiKeyEnv { get; set; } = null;
}

public sealed class HotkeyConfig
{
    [YamlMember(Alias = "mode")]
    public string Mode { get; set; } = "push_to_talk";

    [YamlMember(Alias = "key")]
    public string Key { get; set; } = "capslock";

    [YamlMember(Alias = "abort_key")]
    public string AbortKey { get; set; } = "escape";

    [YamlMember(Alias = "double_tap_max_ms")]
    public int DoubleTapMaxMs { get; set; } = 250;

    [YamlMember(Alias = "double_tap_gap_ms")]
    public int DoubleTapGapMs { get; set; } = 350;
}

public sealed class AudioConfig
{
    [YamlMember(Alias = "input_device")]
    public string? InputDevice { get; set; } = null;

    [YamlMember(Alias = "sample_rate")]
    public int SampleRate { get; set; } = 16000;

    [YamlMember(Alias = "channels")]
    public int Channels { get; set; } = 1;

    [YamlMember(Alias = "chunk_duration_ms")]
    public int ChunkDurationMs { get; set; } = 100;

    [YamlMember(Alias = "ready_timeout_seconds")]
    public double ReadyTimeoutSeconds { get; set; } = 3.0;
}

public sealed class PasteConfig
{
    [YamlMember(Alias = "default_combo")]
    public string DefaultCombo { get; set; } = "ctrl+v";

    [YamlMember(Alias = "terminal_combo")]
    public string TerminalCombo { get; set; } = "ctrl+shift+v";

    [YamlMember(Alias = "terminal_apps")]
    public string[] TerminalApps { get; set; } =
    [
        "wt.exe",
        "windowsterminal.exe",
        "powershell.exe",
        "pwsh.exe",
        "cmd.exe",
        "conhost.exe",
        "mintty.exe",
    ];

    [YamlMember(Alias = "clipboard_restore_delay_ms")]
    public int ClipboardRestoreDelayMs { get; set; } = 200;

    [YamlMember(Alias = "refocus_before_paste")]
    public bool RefocusBeforePaste { get; set; } = true;
}

public sealed class UiConfig
{
    [YamlMember(Alias = "show_floating_pill")]
    public bool ShowFloatingPill { get; set; } = true;

    [YamlMember(Alias = "pill_position")]
    public string PillPosition { get; set; } = "center_bottom";

    [YamlMember(Alias = "sound_feedback")]
    public bool SoundFeedback { get; set; } = true;
}

public sealed class ContextProfilesConfig
{
    [YamlMember(Alias = "code")]
    public string[] Code { get; set; } = ["code.exe", "devenv.exe", "cursor.exe", "pycharm64.exe"];

    [YamlMember(Alias = "terminal")]
    public string[] Terminal { get; set; } = ["windowsterminal.exe", "wt.exe", "cmd.exe", "powershell.exe"];

    [YamlMember(Alias = "chat")]
    public string[] Chat { get; set; } = ["slack.exe", "teams.exe", "discord.exe", "telegram.exe"];

    [YamlMember(Alias = "email")]
    public string[] Email { get; set; } = ["outlook.exe", "thunderbird.exe"];

    [YamlMember(Alias = "document")]
    public string[] Document { get; set; } = ["winword.exe", "obsidian.exe", "notion.exe"];
}

public sealed class BrowserProfilesConfig
{
    [YamlMember(Alias = "browsers")]
    public string[] Browsers { get; set; } = ["chrome.exe", "msedge.exe", "firefox.exe", "brave.exe"];

    [YamlMember(Alias = "rules")]
    public BrowserRulesConfig Rules { get; set; } = new();
}

public sealed class BrowserRulesConfig
{
    [YamlMember(Alias = "email")]
    public string[] Email { get; set; } = ["Gmail", "Outlook", "Proton Mail"];

    [YamlMember(Alias = "chat")]
    public string[] Chat { get; set; } = ["WhatsApp", "Telegram", "Slack", "Discord", "Teams"];

    [YamlMember(Alias = "code")]
    public string[] Code { get; set; } = ["GitHub", "GitLab", "Stack Overflow"];

    [YamlMember(Alias = "document")]
    public string[] Document { get; set; } = ["Google Docs", "Notion", "Overleaf"];
}

public sealed class RecoveryConfig
{
    [YamlMember(Alias = "retention_seconds")]
    public double RetentionSeconds { get; set; } = 600.0;
}
