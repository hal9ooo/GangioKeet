using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class CleanerTests
{
    [Theory]
    [InlineData("   ")]
    [InlineData("<think>rifletto</think>Ciao.")]
    [InlineData("<raw_dictation>Ciao</raw_dictation>")]
    [InlineData("<|turn>model\nCiao<turn|>")]
    public void UnsafeOutputCannotBePublished(string output) =>
        Assert.NotEmpty(OutputPolicy.Validate("ciao", output));

    [Fact]
    public async Task ShortQuestionIsSentToGeneratorAndReturnedUnchanged()
    {
        FakeGenerator generator = new("Come funziona?", false);
        await using LlamaTextCleaner cleaner = new(generator, new CleanupPolicy());
        await cleaner.LoadAsync(CancellationToken.None);
        CleanedText result = await cleaner.CleanAsync("come funziona", "chat", CancellationToken.None);
        Assert.Equal("Come funziona?", result.Text);
        Assert.Contains("come funziona", generator.Prompt);
        Assert.Equal(1, generator.Calls);
    }

    [Fact]
    public async Task TokenLimitRejectsPartialOutput()
    {
        await using LlamaTextCleaner cleaner = new(new FakeGenerator("Frase incompleta", true), new CleanupPolicy());
        await cleaner.LoadAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => cleaner.CleanAsync("una frase", "chat", CancellationToken.None));
    }

    [Fact]
    public async Task ValidatedCommandCasingIsPreserved()
    {
        await using LlamaTextCleaner cleaner = new(new FakeGenerator("Avvia docker compose up.", false),
            new CleanupPolicy { CanonicalTerms = ["Docker"] });
        await cleaner.LoadAsync(default);
        Assert.Equal("Avvia docker compose up.", (await cleaner.CleanAsync("avvia docker compose up", "code", default)).Text);
    }

    private sealed class FakeGenerator(string text, bool truncated) : ITextGenerator
    {
        public int Calls;
        public string Prompt = "";
        public EngineIdentity Identity { get; } = new("fake", "r", "h", "v", "vulkan", "F16", "fake");
        public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<GeneratedText> GenerateAsync(string system, string prompt, int maxTokens, CancellationToken ct)
        {
            Calls++;
            Prompt = prompt;
            return Task.FromResult(new GeneratedText(text, truncated));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
