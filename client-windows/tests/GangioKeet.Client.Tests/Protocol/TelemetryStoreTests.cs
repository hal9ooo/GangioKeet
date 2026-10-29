using GangioKeet.Client.Protocol;
using Xunit;

namespace GangioKeet.Client.Tests.Protocol;

public sealed class TelemetryStoreTests
{
    [Fact]
    public void RecordDictation_TracksCountersAndLastValues()
    {
        TelemetryStore store = new();

        store.RecordDictation(true, TimeSpan.FromMilliseconds(820), TimeSpan.FromMilliseconds(35), "Casa", null);
        store.RecordDictation(false, TimeSpan.FromMilliseconds(2000), null, "Mobilita", "connect_failed");
        store.RecordRetry();

        TelemetrySnapshot snapshot = store.Snapshot();

        Assert.Equal(1, snapshot.Successes);
        Assert.Equal(1, snapshot.Failures);
        Assert.Equal(1, snapshot.Retries);
        Assert.Equal(2000d, snapshot.LastDictationMs);
        Assert.Null(snapshot.LastStartMs);
        Assert.Equal("Mobilita", snapshot.LastBackend);
        Assert.Equal("connect_failed", snapshot.LastError);
    }

    [Fact]
    public void RecordTiming_IsKeyedByTurnAndKeepsFailureSeparate()
    {
        TelemetryStore store = new();

        store.RecordTiming(11, 3500, null);
        store.RecordTiming(12, null, "focus_lost");

        Assert.Equal(3500, store.TimingFor(11)!.Value.ReleaseToPasteMs);
        Assert.Null(store.TimingFor(12)!.Value.ReleaseToPasteMs);
        Assert.Equal("focus_lost", store.TimingFor(12)!.Value.FailureReason);
        Assert.Null(store.TimingFor(13));
    }

    [Fact]
    public void Success_ClearsThePreviousError()
    {
        TelemetryStore store = new();
        store.RecordDictation(false, TimeSpan.FromSeconds(3), null, "Casa", "timeout");

        store.RecordDictation(true, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(20), "Casa", null);

        Assert.Null(store.Snapshot().LastError);
        Assert.Equal(1, store.Snapshot().Successes);
        Assert.Equal(1, store.Snapshot().Failures);
    }
}
