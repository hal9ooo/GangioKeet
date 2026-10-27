namespace GangioKeet.Inference.Cleaning;

public sealed class LlamaTextCleaner(ITextGenerator generator, CleanupPolicy policy) : ITextCleaner
{
    public Task LoadAsync(CancellationToken ct) => generator.LoadAsync(ct);

    public async Task<CleanedText> CleanAsync(string raw, string profile, CancellationToken ct)
    {
        string prompt = CleanerPrompt.Render(raw, profile, policy.Glossary);
        ct.ThrowIfCancellationRequested();
        int maxTokens = Math.Clamp(raw.Length * 2 + 64, 128, 4096);
        GeneratedText generated = await generator.GenerateAsync(CleanerPrompt.System, prompt, maxTokens, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<string> warnings = OutputPolicy.Validate(raw, generated.Text);
        if (generated.Truncated || warnings.Count > 0)
            throw new InvalidDataException("Cleaner output rejected: " + (generated.Truncated ? "token_limit" : string.Join(",", warnings)));
        return new CleanedText(GlossaryNormalizer.Normalize(generated.Text.Trim(), policy), generator.Identity);
    }

    public ValueTask DisposeAsync() => generator.DisposeAsync();
}
