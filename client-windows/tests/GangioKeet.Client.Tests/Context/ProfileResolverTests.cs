using GangioKeet.Client.Configuration;
using GangioKeet.Client.Context;
using Xunit;

namespace GangioKeet.Client.Tests.Context;

public sealed class ProfileResolverTests
{
    [Fact]
    public void ProcessMapping_CaseInsensitive()
    {
        ClientConfig config = new();

        Assert.Equal(DictationProfile.Code, ProfileResolver.Resolve("CODE.EXE", "Senza titolo", config));
        Assert.Equal(DictationProfile.Terminal, ProfileResolver.Resolve("WINDOWSTERMINAL.EXE", "pwsh", config));
        Assert.Equal(DictationProfile.Chat, ProfileResolver.Resolve("Slack.EXE", "#general", config));
        Assert.Equal(DictationProfile.Email, ProfileResolver.Resolve("outlook.exe", "Posta in arrivo", config));
        Assert.Equal(DictationProfile.Document, ProfileResolver.Resolve("WinWord.EXE", "Documento1", config));
        Assert.Equal(DictationProfile.Default, ProfileResolver.Resolve("SomeApp.EXE", "Finestra", config));

        Assert.Equal("code", ProfileResolver.ToWire(DictationProfile.Code));
        Assert.Equal("terminal", ProfileResolver.ToWire(DictationProfile.Terminal));
        Assert.Equal("chat", ProfileResolver.ToWire(DictationProfile.Chat));
        Assert.Equal("email", ProfileResolver.ToWire(DictationProfile.Email));
        Assert.Equal("document", ProfileResolver.ToWire(DictationProfile.Document));
        Assert.Equal("default", ProfileResolver.ToWire(DictationProfile.Default));
    }

    [Fact]
    public void BrowserRules_OrderedFirstMatch()
    {
        ClientConfig config = new();

        Assert.Equal(DictationProfile.Email, ProfileResolver.Resolve("chrome.exe", "Gmail - GitHub", config));
        Assert.Equal(DictationProfile.Email, ProfileResolver.Resolve("msedge.exe", "GitHub - Gmail", config));
        Assert.Equal(DictationProfile.Chat, ProfileResolver.Resolve("firefox.exe", "CANALI - Discord", config));
        Assert.Equal(DictationProfile.Code, ProfileResolver.Resolve("brave.exe", "Stack Overflow - Domanda", config));
        Assert.Equal(DictationProfile.Document, ProfileResolver.Resolve("chrome.exe", "Overleaf - Tesi di laurea", config));
        Assert.Equal(DictationProfile.Default, ProfileResolver.Resolve("chrome.exe", "about:blank", config));
    }

    [Fact]
    public void NonBrowser_IgnoresTitle()
    {
        ClientConfig config = new();

        Assert.Equal(DictationProfile.Default, ProfileResolver.Resolve("explorer.exe", "Gmail - Proton Mail", config));
        Assert.Equal(DictationProfile.Default, ProfileResolver.Resolve("notepad.exe", "GitHub - Blocco note", config));
    }

    [Fact]
    public void UnknownApp_Default()
    {
        ClientConfig config = new();

        Assert.Equal(DictationProfile.Default, ProfileResolver.Resolve("fancyapp.exe", "", config));
        Assert.Equal(DictationProfile.Default, ProfileResolver.Resolve("", "Gmail", config));
    }

    [Fact]
    public void OverrideWins()
    {
        ClientConfig config = new();

        config.ContextProfiles.Code = ["chrome.exe"];
        Assert.Equal(DictationProfile.Code, ProfileResolver.Resolve("Chrome.exe", "Gmail - GitHub", config));

        config.ContextProfiles.Code = ["myeditor.exe"];
        config.ContextProfiles.Chat = [];
        Assert.Equal(DictationProfile.Code, ProfileResolver.Resolve("MyEditor.EXE", "Gmail", config));
        Assert.Equal(DictationProfile.Default, ProfileResolver.Resolve("slack.exe", "Slack", config));
    }
}
