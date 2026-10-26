using GangioKeet.Client.Context;
using GangioKeet.Client.Controller;
using GangioKeet.Client.Ui;
using Xunit;

namespace GangioKeet.Client.Tests.Ui;

public sealed class PillPolicyTests
{
    [Fact]
    public void Arming_ShowsConnectingMessage()
    {
        FakePillView pill = new();

        pill.Show(PillState.Arming, null, DictationProfile.Default);

        Assert.Equal(PillState.Arming, pill.LastState);
    }

    [Fact]
    public void Recording_ShowsRecordingMessage()
    {
        FakePillView pill = new();

        pill.Show(PillState.Recording, null, DictationProfile.Code);

        Assert.Equal(PillState.Recording, pill.LastState);
        Assert.Equal(DictationProfile.Code, pill.LastProfile);
    }

    [Fact]
    public void Processing_ShowsProcessingMessage()
    {
        FakePillView pill = new();

        pill.Show(PillState.Processing, null, null);

        Assert.Equal(PillState.Processing, pill.LastState);
    }

    [Fact]
    public void Warning_ShowsWarningMessage()
    {
        FakePillView pill = new();

        pill.Show(PillState.Warning, "Testo non rifinito", null);

        Assert.Equal(PillState.Warning, pill.LastState);
        Assert.Equal("Testo non rifinito", pill.LastMessage);
    }

    [Fact]
    public void Error_ShowsErrorMessage()
    {
        FakePillView pill = new();

        pill.Show(PillState.Error, "Errore di rete", null);

        Assert.Equal(PillState.Error, pill.LastState);
        Assert.Equal("Errore di rete", pill.LastMessage);
    }

    [Fact]
    public void EmptyResult_ShowsEmptyMessage()
    {
        FakePillView pill = new();

        pill.Show(PillState.EmptyResult, null, null);

        Assert.Equal(PillState.EmptyResult, pill.LastState);
    }

    [Fact]
    public void Hide_ClearsState()
    {
        FakePillView pill = new();
        pill.Show(PillState.Recording, null, null);

        pill.Hide();

        Assert.Equal(1, pill.HideCalls);
    }

    [Fact]
    public void SetLevel_UpdatesLevel()
    {
        FakePillView pill = new();

        pill.SetLevel(0.5);

        Assert.Equal(1, pill.SetLevelCalls);
        Assert.Equal(0.5, pill.LastLevel);
    }

    private sealed class FakePillView : IPillView
    {
        public PillState? LastState { get; private set; }
        public string? LastMessage { get; private set; }
        public DictationProfile? LastProfile { get; private set; }
        public double LastLevel { get; private set; }
        public int HideCalls { get; private set; }
        public int ShowCalls { get; private set; }
        public int SetLevelCalls { get; private set; }

        public void Show(PillState state, string? message, DictationProfile? profile)
        {
            ShowCalls++;
            LastState = state;
            LastMessage = message;
            LastProfile = profile;
        }

        public void Hide() { HideCalls++; }
        public void SetLevel(double level) { SetLevelCalls++; LastLevel = level; }
    }
}
