namespace GangioKeet.Client.Protocol;

public sealed record DictationMetrics(double SttMs, double LlmMs, double TotalMs);

public sealed record StartMessage(string AppName, string WindowTitle, string Profile)
{
    public string Type => "start";

    public string SelectedText => "";

    public bool BypassLlm => false;
}

public sealed record ResultMessage(
    string RawText,
    string FinalText,
    bool FastPath,
    DictationMetrics Metrics,
    IReadOnlyList<string> Warnings);
