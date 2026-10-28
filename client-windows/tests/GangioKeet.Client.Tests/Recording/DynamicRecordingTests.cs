using GangioKeet.Client.Configuration;
using GangioKeet.Client.Protocol;
using GangioKeet.Client.Recording;
using GangioKeet.Inference;
using Xunit;

namespace GangioKeet.Client.Tests.Recording;

public sealed class DynamicRecordingTests
{
    private sealed class MockRecordingWriter : IRecordingWriter
    {
        public int RecordedCount { get; private set; }

        public Task<string?> RecordAsync(
            ReadOnlyMemory<byte> pcm,
            PipelineResult result,
            StartMessage start,
            CancellationToken ct)
        {
            RecordedCount++;
            return Task.FromResult<string?>("rec_1");
        }
    }

    [Fact]
    public async Task RecordingQueue_WithDynamicEnabledDelegate_TogglesEnqueueLive()
    {
        var config = new RecordingConfig { Enabled = false, Directory = "test_dir" };
        var store = new RecordingStore(config);
        var mockWriter = new MockRecordingWriter();

        // Queue starts with store.Enabled == false
        var queue = new RecordingQueue(mockWriter, capacity: 4, enabled: () => store.Enabled);

        var engineId = new EngineIdentity("stt", "rev", "sha", "1.0", "vulkan", "fp16", "gpu");
        var dummyResult = new PipelineResult("raw", "final", 100, 200, 300, Array.Empty<string>(), engineId, null);
        var dummyStart = new StartMessage("TestApp", "TestTitle", "default");
        byte[] pcm = new byte[320];

        // Should not enqueue while store.Enabled is false
        bool enqueuedBefore = queue.TryEnqueue(pcm, dummyResult, dummyStart);
        Assert.False(enqueuedBefore);

        // Turn on store.Enabled dynamically
        store.Enabled = true;

        // Should enqueue immediately without recreating queue
        bool enqueuedAfter = queue.TryEnqueue(pcm, dummyResult, dummyStart);
        Assert.True(enqueuedAfter);

        // Turn off store.Enabled dynamically
        store.Enabled = false;

        bool enqueuedTurnedOff = queue.TryEnqueue(pcm, dummyResult, dummyStart);
        Assert.False(enqueuedTurnedOff);

        await queue.DrainAsync(CancellationToken.None);
        Assert.Equal(1, mockWriter.RecordedCount);
    }
}
