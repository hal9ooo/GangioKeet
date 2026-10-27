namespace GangioKeet.Inference.Cleaning;

public static class OutputPolicy
{
    public static IReadOnlyList<string> Validate(string raw, string output)
    {
        List<string> errors = [];
        if (string.IsNullOrWhiteSpace(output)) errors.Add("empty_output");
        if (new[] { "<think", "</think", "<raw_dictation", "</raw_dictation", "<profile", "<glossary",
                "<|im_", "<|channel", "<channel|>", "<|turn>", "<turn|>", "[start_header_id]", "[start_of_turn]" }
            .Any(marker => output.Contains(marker, StringComparison.OrdinalIgnoreCase))) errors.Add("prompt_or_thinking_tags");
        if (output.Contains('\0') || output.Contains('\uFFFD')) errors.Add("invalid_text");
        if (output.Length > Math.Max(500, raw.Length * 4)) errors.Add("expanded_output");
        return errors;
    }
}
