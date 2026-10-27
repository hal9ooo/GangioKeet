namespace GangioKeet.Inference.Cleaning;

public static class GemmaTextTemplate
{
    // Exact text-only, single system/user specialization of the canonical
    // Gemma 4 Jinja in pinned GGUF (sha256 c0e1a4e7...1709abb4c).
    // Verified against Jinja2 with enable_thinking=false; no tools/media/history.
    public static string Render(string system, string user) =>
        $"<bos><|turn>system\n{system.Trim()}<turn|>\n<|turn>user\n{user.Trim()}<turn|>\n<|turn>model\n";

    // LLamaContext.Tokenize adds BOS from the model metadata (verified in the
    // native probe); omit the template's literal BOS at this boundary only.
    public static string RenderForAutoBosTokenizer(string system, string user) => Render(system, user)[5..];
}
