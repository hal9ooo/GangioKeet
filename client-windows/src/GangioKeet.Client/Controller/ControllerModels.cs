using GangioKeet.Client.Context;

namespace GangioKeet.Client.Controller;

public enum ClientState
{
    Idle,
    Connecting,
    Recording,
    Processing,
    Pasting,
    Error,
}

public enum PillState
{
    Hidden,
    Arming,
    Recording,
    Processing,
    Warning,
    Error,
    EmptyResult,
}

public interface IPillView
{
    void Show(PillState state, string? message, DictationProfile? profile);
    void Hide();
    void SetLevel(double level);
}

public enum SoundKind
{
    Start,
    Stop,
    Error,
}

public interface ISoundPlayer
{
    void Play(SoundKind kind);
}
