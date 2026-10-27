namespace GangioKeet.Inference.Cleaning;

public sealed record GeneratedText(string Text, bool Truncated);

public interface ITextGenerator : IAsyncDisposable
{
    EngineIdentity Identity { get; }
    Task LoadAsync(CancellationToken ct);
    Task<GeneratedText> GenerateAsync(string system, string prompt, int maxTokens, CancellationToken ct);
}
