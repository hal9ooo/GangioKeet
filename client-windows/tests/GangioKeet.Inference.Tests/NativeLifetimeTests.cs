using GangioKeet.Inference.Stt;
using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class NativeLifetimeTests
{
    [Fact]
    public async Task DisposeWaitsForNativeReturnAndFreesSessionBeforeModel()
    {
        using SttFixture fixture = new();
        fixture.Api.Hold = true;
        TranscribeSttEngine engine = fixture.CreateEngine();
        await engine.LoadAsync(CancellationToken.None);
        Task<SpeechTranscript> running = engine.TranscribeAsync(new byte[4], CancellationToken.None);
        await fixture.Api.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task dispose = engine.DisposeAsync().AsTask();
        Assert.False(dispose.IsCompleted);
        Assert.Empty(fixture.Api.Freed);
        fixture.Api.Release.Set();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        await dispose;
        await engine.DisposeAsync();
        Assert.Equal(new[] { "session", "model" }, fixture.Api.Freed);
    }

    [Fact]
    public async Task RepeatedLoadDoesNotReplaceResidentModel()
    {
        using SttFixture fixture = new();
        TranscribeSttEngine engine = fixture.CreateEngine();
        await engine.LoadAsync(CancellationToken.None);
        await engine.LoadAsync(CancellationToken.None);
        await engine.DisposeAsync();
        Assert.Equal(new[] { "session", "model" }, fixture.Api.Freed);
    }
}
