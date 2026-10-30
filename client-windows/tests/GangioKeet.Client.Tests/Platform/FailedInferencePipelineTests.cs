using GangioKeet.Client.Configuration;
using GangioKeet.Client.Platform;
using GangioKeet.Inference;
using Xunit;

namespace GangioKeet.Client.Tests.Platform;

public sealed class FailedInferencePipelineTests
{
    [Fact]
    public async Task WarmupExposesTheStartupError()
    {
        InvalidDataException error = new("catalog missing");
        FailedInferencePipeline pipeline = new(error);

        InvalidDataException actual = await Assert.ThrowsAsync<InvalidDataException>(
            () => pipeline.WarmUpAsync(default));
        Assert.Same(error, actual);
    }

    [Fact]
    public void FailedSetupKeepsTheAppInNotReadyState()
    {
        ClientConfig config = new();
        InvalidDataException error = new("catalog missing");
        LocalInferenceSetup setup = LocalInferenceSetup.Failed(config, error);

        Assert.IsType<FailedInferencePipeline>(setup.Pipeline);
        Assert.Equal("non disponibile", setup.SttId);
        Assert.Null(setup.SttArtifact);
        Assert.False(setup.RefinementEnabled);
    }

    [Fact]
    public async Task ProcessingExposesTheStartupError()
    {
        InvalidDataException error = new("catalog missing");
        FailedInferencePipeline pipeline = new(error);

        InvalidDataException actual = await Assert.ThrowsAsync<InvalidDataException>(
            () => pipeline.ProcessAsync(new(new byte[4], "chat"), default));
        Assert.Same(error, actual);
    }
}
