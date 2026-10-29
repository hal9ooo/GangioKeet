using GangioKeet.Client.Configuration;
using Xunit;

namespace GangioKeet.Client.Tests.Configuration;

public sealed class ConfigLoaderTests
{
    private static string WriteTempYaml(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gangiokeet-config-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    private static string FindExampleYaml()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "client_config.example.yaml");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent!;
        }
        throw new InvalidOperationException("client_config.example.yaml not found");
    }

    [Fact]
    public void LoadsExample_ExactDefaults()
    {
        ClientConfig config = ConfigLoader.Load(FindExampleYaml(), Path.GetTempPath());

        Assert.Equal("vulkan", config.Inference.Backend);
        Assert.EndsWith(Path.Combine("models", "selected.json"), config.Inference.ModelSelectionPath);
        Assert.EndsWith("cleanup.yaml", config.Inference.CleanupPolicyPath);

        Assert.Equal("push_to_talk", config.Hotkey.Mode);
        Assert.Equal("capslock", config.Hotkey.Key);
        Assert.Equal("escape", config.Hotkey.AbortKey);
        Assert.Equal(250, config.Hotkey.DoubleTapMaxMs);
        Assert.Equal(350, config.Hotkey.DoubleTapGapMs);

        Assert.Null(config.Audio.InputDevice);
        Assert.Equal(16000, config.Audio.SampleRate);
        Assert.Equal(1, config.Audio.Channels);
        Assert.Equal(100, config.Audio.ChunkDurationMs);
        Assert.Equal(3.0, config.Audio.ReadyTimeoutSeconds);

        Assert.Equal("ctrl+v", config.Paste.DefaultCombo);
        Assert.Equal("ctrl+shift+v", config.Paste.TerminalCombo);
        Assert.Equal(
            new[]
            {
                "wt.exe", "windowsterminal.exe", "powershell.exe",
                "pwsh.exe", "cmd.exe", "conhost.exe", "mintty.exe",
            },
            config.Paste.TerminalApps);
        Assert.Equal(200, config.Paste.ClipboardRestoreDelayMs);
        Assert.True(config.Paste.RefocusBeforePaste);

        Assert.True(config.Ui.ShowFloatingPill);
        Assert.Equal("center_bottom", config.Ui.PillPosition);
        Assert.True(config.Ui.SoundFeedback);

        Assert.Equal(600.0, config.Recovery.RetentionSeconds);

        Assert.Equal(
            new[] { "code.exe", "devenv.exe", "cursor.exe", "pycharm64.exe" },
            config.ContextProfiles.Code);
        Assert.Equal(
            new[] { "windowsterminal.exe", "wt.exe", "cmd.exe", "powershell.exe" },
            config.ContextProfiles.Terminal);
        Assert.Equal(
            new[] { "slack.exe", "teams.exe", "discord.exe", "telegram.exe" },
            config.ContextProfiles.Chat);
        Assert.Equal(
            new[] { "outlook.exe", "thunderbird.exe" },
            config.ContextProfiles.Email);
        Assert.Equal(
            new[] { "winword.exe", "obsidian.exe", "notion.exe" },
            config.ContextProfiles.Document);

        Assert.Equal(
            new[] { "chrome.exe", "msedge.exe", "firefox.exe", "brave.exe" },
            config.BrowserProfiles.Browsers);
        Assert.Equal(new[] { "Gmail", "Outlook", "Proton Mail" }, config.BrowserProfiles.Rules.Email);
        Assert.Equal(
            new[] { "WhatsApp", "Telegram", "Slack", "Discord", "Teams" },
            config.BrowserProfiles.Rules.Chat);
        Assert.Equal(new[] { "GitHub", "GitLab", "Stack Overflow" }, config.BrowserProfiles.Rules.Code);
        Assert.Equal(new[] { "Google Docs", "Notion", "Overleaf" }, config.BrowserProfiles.Rules.Document);
    }

    [Fact]
    public void UnknownKeyRejected()
    {
        string path = WriteTempYaml("unknown_key: true\n");
        try
        {
            var ex = Record.Exception(() => ConfigLoader.Load(path));
            Assert.IsType<ConfigException>(ex);
            Assert.Contains("unknown_key", ex!.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AltGrRejected()
    {
        string path = WriteTempYaml(
            """
            hotkey:
              key: "alt_r"
            """);
        try
        {
            var ex = Record.Exception(() => ConfigLoader.Load(path));
            Assert.IsType<ConfigException>(ex);
            Assert.Contains("alt_r", ex!.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void InvalidWireAudioRejected()
    {
        string path = WriteTempYaml(
            """
            audio:
              sample_rate: 48000
            """);
        try
        {
            var ex = Record.Exception(() => ConfigLoader.Load(path));
            Assert.IsType<ConfigException>(ex);
            Assert.Contains("16000", ex!.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingExplicitPathFails()
    {
        string missing = Path.Combine(Path.GetTempPath(), $"gangiokeet-missing-{Guid.NewGuid():N}.yaml");

        var ex = Record.Exception(() => ConfigLoader.Load(missing));

        Assert.IsType<ConfigException>(ex);
        Assert.Contains(missing, ex!.Message);
    }

    [Fact]
    public void MissingDefaultUsesEmbeddedDefaults()
    {
        string appDataBase = Path.Combine(Path.GetTempPath(), $"gangiokeet-appdata-{Guid.NewGuid():N}");

        ClientConfig config = ConfigLoader.Load(null, appDataBase);

        Assert.Equal("vulkan", config.Inference.Backend);
        Assert.Equal("capslock", config.Hotkey.Key);
        Assert.Equal("escape", config.Hotkey.AbortKey);
        Assert.Equal(250, config.Hotkey.DoubleTapMaxMs);
        Assert.Equal(350, config.Hotkey.DoubleTapGapMs);
        Assert.Equal(16000, config.Audio.SampleRate);
        Assert.Equal(1, config.Audio.Channels);
        Assert.Equal(100, config.Audio.ChunkDurationMs);
        Assert.Equal(200, config.Paste.ClipboardRestoreDelayMs);
        Assert.Equal(600.0, config.Recovery.RetentionSeconds);
    }

    [Fact]
    public void CapsLockHotkey_IsAccepted()
    {
        ConfigLoader.Validate(new ClientConfig
        {
            Hotkey = new HotkeyConfig { Key = "capslock", AbortKey = "escape" },
        });
    }

    [Fact]
    public void CapsLockAsAbortKey_IsRejected()
    {
        Assert.Throws<ConfigException>(() => ConfigLoader.Validate(new ClientConfig
        {
            Hotkey = new HotkeyConfig { Key = "f8", AbortKey = "capslock" },
        }));
    }

    [Fact]
    public void CreateDefaults_IsLocal()
    {
        ClientConfig config = ConfigLoader.CreateDefaults();

        Assert.Equal("vulkan", config.Inference.Backend);
        Assert.EndsWith(Path.Combine("models", "selected.json"), config.Inference.ModelSelectionPath);
    }
}
