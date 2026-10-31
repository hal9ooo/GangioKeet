using GangioKeet.Inference;
using GangioKeet.Inference.Cleaning;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class LightTextCleanerTests
{
    [Fact]
    public void ExposesASyntheticArtifactWithNoFiles()
    {
        ModelArtifact artifact = LightTextCleaner.Artifact;
        Assert.Equal("light_cleanup", artifact.Id);
        Assert.Equal("Pulizia leggera", artifact.DisplayName);
        Assert.Equal("deterministic", artifact.RuntimeId);
        Assert.Empty(artifact.Files);
    }

    [Fact]
    public async Task CleansWithTheDeterministicRules()
    {
        await using LightTextCleaner cleaner = new();
        await cleaner.LoadAsync(default);
        CleanedText result = await cleaner.CleanAsync("Mi mi mandi un una lista", "chat", default);
        Assert.Equal("Mi mandi una lista", result.Text);
        Assert.Equal("light_cleanup", result.Engine.ModelId);
        Assert.Equal("rules-v1", result.Engine.RuntimeVersion);
    }

    [Fact]
    public async Task DisposeIsSafe() =>
        await new LightTextCleaner().DisposeAsync();
}
