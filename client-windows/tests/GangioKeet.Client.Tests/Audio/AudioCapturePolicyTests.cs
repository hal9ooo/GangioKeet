using GangioKeet.Client.Audio;
using GangioKeet.Client.Configuration;
using GangioKeet.Client.Platform;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GangioKeet.Client.Tests.Audio;

public sealed class AudioCapturePolicyTests
{
    private static readonly AudioDeviceInfo DeviceA = new("device-a", "Mic A", true);
    private static readonly AudioDeviceInfo DeviceB = new("device-b", "Mic B", true);

    private readonly FakeTimeProvider _time = new();

    private readonly FakeDeviceProvider _devices = new();
    private readonly FakePrivacy _privacy = new();
    private readonly FakeSourceFactory _factory = new();
    private readonly EventRecorder _events = new();

    private WasapiAudioCapture BuildCapture()
    {
        AudioConfig config = new();
        WasapiAudioCapture capture = new(
            _devices,
            _privacy,
            _factory,
            _time,
            TimeSpan.FromSeconds(config.ReadyTimeoutSeconds),
            AudioChunker.TargetChunkBytes);
        capture.Ready += (_, _) => _events.ReadyCount++;
        capture.ChunkReady += (_, chunk) => _events.Chunks.Add(chunk);
        capture.LevelChanged += (_, level) => _events.Levels.Add(level);
        capture.Failed += (_, failure) => _events.Failures.Add(failure);
        return capture;
    }

    private static byte[] AudioBytes(int sampleCount, short peak)
    {
        return Pcm16.FromFloat(Enumerable.Repeat(peak / 32768f, sampleCount).ToArray());
    }

    private static byte[] SilenceBytes(int sampleCount) => new byte[sampleCount * 2];

    [Fact]
    public async Task Ready_FiresOnFirstAudioCallback_NotOnStartAsyncReturn()
    {
        WasapiAudioCapture capture = BuildCapture();
        _devices.SetDevices(DeviceA);
        _factory.Prepare(DeviceA);
        FakeSource source = _factory.SourceOf(DeviceA);

        await capture.StartAsync(DeviceA.Id, CancellationToken.None);

        Assert.Equal(0, _events.ReadyCount);
        Assert.Empty(_events.Chunks);
        Assert.Empty(_events.Failures);

        source.Deliver(SilenceBytes(1600));

        Assert.Equal(1, _events.ReadyCount);
        Assert.Equal([AudioChunker.TargetChunkBytes], _events.Chunks.Select(c => c.Length));

        // Un secondo callback non replica Ready e il timer di readiness è già smontato.
        source.Deliver(SilenceBytes(512));
        _time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(1, _events.ReadyCount);
        Assert.Empty(_events.Failures);

        await capture.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NoReadyWithinMicrophoneTimeout_FailsAndStopsAcquisition()
    {
        WasapiAudioCapture capture = BuildCapture();
        _devices.SetDevices(DeviceA);
        _factory.Prepare(DeviceA);
        FakeSource source = _factory.SourceOf(DeviceA);

        await capture.StartAsync(DeviceA.Id, CancellationToken.None);
        Assert.Empty(_events.Failures);

        _time.Advance(TimeSpan.FromSeconds(3));

        CaptureFailure failure = Assert.Single(_events.Failures);
        Assert.Equal("microphone_not_ready", failure.Code);
        Assert.Equal(0, _events.ReadyCount);
        Assert.Equal(1, source.StopCalls);
        Assert.Equal(1, source.DisposeCalls);

        AudioSummary summary = await capture.StopAsync(CancellationToken.None);
        Assert.Equal(0, summary.SampleCount);
    }

    [Fact]
    public async Task ExplicitMissingDeviceId_FailsWithoutSilentFallback()
    {
        WasapiAudioCapture capture = BuildCapture();
        _devices.SetDevices(DeviceB);

        await capture.StartAsync("device-a", CancellationToken.None);

        CaptureFailure failure = Assert.Single(_events.Failures);
        Assert.Equal("no_input_device", failure.Code);
        Assert.Contains("device-a", failure.Message);
        Assert.Equal(0, _events.ReadyCount);
        Assert.Empty(_events.Chunks);

        // Nessun cambio microfono silenzioso: l'ID preferito non viene sostituito dal default.
        Assert.Empty(_factory.Created);
        Assert.Equal(1, _devices.ListCallCount);

        await capture.StopAsync(CancellationToken.None);

        // Il fallback resta esplicito: la sessione successiva sul default funziona.
        _devices.SetDevices(DeviceA with { IsDefault = false }, DeviceB with { IsDefault = true });
        _factory.Prepare(DeviceB);
        await capture.StartAsync(null, CancellationToken.None);
        FakeSource source = Assert.Single(_factory.CreatedSources);
        source.Deliver(AudioBytes(1600, 4096));
        Assert.Equal(1, _events.ReadyCount);

        await capture.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NoCaptureDevices_FailsNoInputDevice()
    {
        WasapiAudioCapture capture = BuildCapture();

        await capture.StartAsync(null, CancellationToken.None);

        CaptureFailure failure = Assert.Single(_events.Failures);
        Assert.Equal("no_input_device", failure.Code);
        Assert.Empty(_factory.Created);
    }

    [Fact]
    public async Task MicrophonePrivacyDenied_FailsPermissionCodeWithoutOpeningDevices()
    {
        WasapiAudioCapture capture = BuildCapture();
        _devices.SetDevices(DeviceA);
        _privacy.Allowed = false;

        await capture.StartAsync(DeviceA.Id, CancellationToken.None);

        CaptureFailure failure = Assert.Single(_events.Failures);
        Assert.Equal("microphone_permission_denied", failure.Code);
        Assert.Contains(MicrophonePrivacy.SettingsUri, failure.Message);
        Assert.Empty(_factory.Created);
        Assert.Equal(0, _events.ReadyCount);
    }

    [Fact]
    public async Task DeviceLostMidStream_KeepsCapturedAudioAndSignalsIncomplete()
    {
        WasapiAudioCapture capture = BuildCapture();
        _devices.SetDevices(DeviceA);
        _factory.Prepare(DeviceA);
        FakeSource source = _factory.SourceOf(DeviceA);

        await capture.StartAsync(DeviceA.Id, CancellationToken.None);
        byte[] half = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        source.Deliver(half);

        Assert.Equal(1, _events.ReadyCount);
        Assert.Equal([AudioChunker.TargetChunkBytes], _events.Chunks.Select(c => c.Length));

        source.EmitDeviceLost();

        CaptureFailure failure = Assert.Single(_events.Failures);
        Assert.Equal("device_lost", failure.Code);

        // Lo stop continua a evacuare il resto: audio precedente mantenuto (nessuna perdita).
        AudioSummary summary = await capture.StopAsync(CancellationToken.None);

        Assert.Equal([AudioChunker.TargetChunkBytes, 1800], _events.Chunks.Select(c => c.Length));
        Assert.Equal(half, _events.Chunks.SelectMany(c => c.ToArray()));
        Assert.Equal(2500, summary.SampleCount);
        Assert.Equal(1, source.DisposeCalls);
    }

    [Fact]
    public async Task SourceStartThrows_FailsDeviceUnavailableAndDisposesSource()
    {
        WasapiAudioCapture capture = BuildCapture();
        _devices.SetDevices(DeviceA);
        _factory.Prepare(DeviceA, startException: new InvalidOperationException("Exclusive mode in use"));

        await capture.StartAsync(DeviceA.Id, CancellationToken.None);

        CaptureFailure failure = Assert.Single(_events.Failures);
        Assert.Equal("device_unavailable", failure.Code);
        Assert.Contains("Exclusive mode in use", failure.Message);
        Assert.Equal(1, _factory.CreatedSources[0].DisposeCalls);

        // Lo stato di fallimento non si impiglia dopo il tear-down.
        _factory.Prepare(DeviceA);
        await capture.StartAsync(DeviceA.Id, CancellationToken.None);
        FakeSource source = _factory.CreatedSources[^1];
        Assert.Equal(1, source.StartCalls);
        source.Deliver(SilenceBytes(64));
        Assert.Equal(1, _events.ReadyCount);
    }

    [Fact]
    public async Task FactoryCreateThrows_FailsDeviceUnavailable()
    {
        WasapiAudioCapture capture = BuildCapture();
        _devices.SetDevices(DeviceA);
        _factory.CreateException = new InvalidOperationException("no endpoint");

        await capture.StartAsync(DeviceA.Id, CancellationToken.None);

        CaptureFailure failure = Assert.Single(_events.Failures);
        Assert.Equal("device_unavailable", failure.Code);
        Assert.Contains("no endpoint", failure.Message);
        Assert.Empty(_factory.CreatedSources);
    }

    [Fact]
    public async Task SecondStartWhileActive_ThrowsInvalidOperationException()
    {
        WasapiAudioCapture capture = BuildCapture();
        _devices.SetDevices(DeviceA);
        _factory.Prepare(DeviceA);

        await capture.StartAsync(DeviceA.Id, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            capture.StartAsync(DeviceA.Id, CancellationToken.None));
        await capture.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_FlushesFinalPartialChunkBeforeCompleting()
    {
        WasapiAudioCapture capture = BuildCapture();
        _devices.SetDevices(DeviceA);
        _factory.Prepare(DeviceA);
        FakeSource source = _factory.SourceOf(DeviceA);

        await capture.StartAsync(DeviceA.Id, CancellationToken.None);
        source.Deliver(SilenceBytes(500));
        Assert.Empty(_events.Chunks);

        AudioSummary summary = await capture.StopAsync(CancellationToken.None);

        Assert.Single(_events.Chunks);
        Assert.Equal(1000, _events.Chunks[0].Length);
        Assert.Equal(500, summary.SampleCount);
        Assert.True(summary.AllZero);
    }

    [Fact]
    public async Task LevelChanged_ReportsChunkPeakAsFractionOfFullScale()
    {
        WasapiAudioCapture capture = BuildCapture();
        _devices.SetDevices(DeviceA);
        _factory.Prepare(DeviceA);
        FakeSource source = _factory.SourceOf(DeviceA);

        await capture.StartAsync(DeviceA.Id, CancellationToken.None);
        source.Deliver(AudioBytes(1600, 8192));
        source.Deliver(SilenceBytes(1600));

        await capture.StopAsync(CancellationToken.None);

        Assert.Equal([0.25, 0], _events.Levels);
    }

    [Fact]
    public async Task ReopeningOnNextSession_ReenumeratesDevicesAndRebuildsSource()
    {
        WasapiAudioCapture capture = BuildCapture();
        _devices.SetDevices(DeviceA);
        _factory.Prepare(DeviceA);

        await capture.StartAsync(DeviceA.Id, CancellationToken.None);
        _factory.SourceOf(DeviceA).Deliver(AudioBytes(3200, 1024));
        AudioSummary first = await capture.StopAsync(CancellationToken.None);
        Assert.Equal(3200, first.SampleCount);

        // Sessione successiva su default: enumera di nuovo e usa il device default attuale.
        _devices.SetDevices(DeviceA with { IsDefault = false }, DeviceB);
        _factory.Prepare(DeviceB);

        await capture.StartAsync(null, CancellationToken.None);
        Assert.Equal([DeviceA, DeviceB], _factory.Created);
        Assert.Equal(2, _devices.ListCallCount);

        _factory.SourceOf(DeviceB).Deliver(AudioBytes(1600, 512));
        AudioSummary second = await capture.StopAsync(CancellationToken.None);
        Assert.Equal(1600, second.SampleCount);
        Assert.Equal(2, _events.ReadyCount);
    }

    [Fact]
    public async Task BytesEmittedDuringSourceStop_AreChunkedAndCounted()
    {
        WasapiAudioCapture capture = BuildCapture();
        _devices.SetDevices(DeviceA);
        _factory.Prepare(DeviceA);
        FakeSource source = _factory.SourceOf(DeviceA);
        source.EmitOnStop = AudioBytes(700, 1024);

        await capture.StartAsync(DeviceA.Id, CancellationToken.None);
        AudioSummary summary = await capture.StopAsync(CancellationToken.None);

        Assert.Single(_events.Chunks);
        Assert.Equal(1400, _events.Chunks[0].Length);
        Assert.Equal(700, summary.SampleCount);
    }

    [Fact]
    public async Task DisposeAsync_AfterStop_DoesNotDisposeSourceTwice()
    {
        WasapiAudioCapture capture = BuildCapture();
        _devices.SetDevices(DeviceA);
        _factory.Prepare(DeviceA);

        await capture.StartAsync(DeviceA.Id, CancellationToken.None);
        await capture.StopAsync(CancellationToken.None);
        await capture.DisposeAsync();

        Assert.Equal(1, _factory.CreatedSources[0].DisposeCalls);
    }
}

internal sealed class FakeDeviceProvider : IAudioDeviceProvider
{
    private IReadOnlyList<AudioDeviceInfo> _devices = [];

    public int ListCallCount { get; private set; }

    public void SetDevices(params AudioDeviceInfo[] devices) => _devices = devices;

    public Task<IReadOnlyList<AudioDeviceInfo>> ListAsync(CancellationToken ct)
    {
        ListCallCount++;
        return Task.FromResult(_devices);
    }
}

internal sealed class FakePrivacy : IMicrophonePrivacy
{
    public bool Allowed { get; set; } = true;

    public bool IsAccessAllowed() => Allowed;

    public void OpenSettings() { }
}

internal sealed class FakeSource : IMicrophoneSource
{
    public event EventHandler<ReadOnlyMemory<byte>>? AudioArrived;
    public event EventHandler? DeviceLost;

    public int StartCalls { get; private set; }
    public int StopCalls { get; private set; }
    public int DisposeCalls { get; private set; }

    public Exception? StartException { get; set; }

    public byte[]? EmitOnStop { get; set; }

    public void Deliver(ReadOnlyMemory<byte> pcm) => AudioArrived?.Invoke(this, pcm);

    public void EmitDeviceLost() => DeviceLost?.Invoke(this, EventArgs.Empty);

    public Task StartAsync(CancellationToken ct)
    {
        StartCalls++;
        if (StartException != null)
        {
            throw StartException;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        StopCalls++;
        if (EmitOnStop is { Length: > 0 } drained)
        {
            AudioArrived?.Invoke(this, drained);
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        DisposeCalls++;
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeSourceFactory : IMicrophoneSourceFactory
{
    private readonly Dictionary<string, FakeSource> _prepared = new();
    private readonly List<AudioDeviceInfo> _created = [];
    private readonly List<FakeSource> _createdSources = [];

    public Exception? CreateException { get; set; }

    public IReadOnlyList<AudioDeviceInfo> Created => _created;

    public IReadOnlyList<FakeSource> CreatedSources => _createdSources;

    public void Prepare(AudioDeviceInfo device, Exception? startException = null)
    {
        FakeSource source = new() { StartException = startException };
        _prepared[device.Id] = source;
        _createdSources.Add(source);
    }

    public FakeSource SourceOf(AudioDeviceInfo device) => _prepared[device.Id];

    public IMicrophoneSource Create(AudioDeviceInfo device)
    {
        _created.Add(device);
        if (CreateException != null)
        {
            throw CreateException;
        }

        _prepared.TryGetValue(device.Id, out FakeSource? source);
        return source ?? new FakeSource();
    }
}

internal sealed class EventRecorder
{
    public int ReadyCount { get; set; }
    public List<ReadOnlyMemory<byte>> Chunks { get; } = [];
    public List<double> Levels { get; } = [];
    public List<CaptureFailure> Failures { get; } = [];
}
