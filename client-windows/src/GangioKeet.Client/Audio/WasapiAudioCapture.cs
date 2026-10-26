using GangioKeet.Client.Configuration;
using GangioKeet.Client.Platform;

namespace GangioKeet.Client.Audio;

/// <summary>
/// Policy di cattura sopra un <see cref="IMicrophoneSource"/> sostituibile: risolve il dispositivo
/// (default a ogni sessione, ID esplicito mai rimpiazzato in silenzio), verifica il permesso privacy,
/// pubblica <see cref="Ready"/> al primo callback reale, misura la sessione con
/// <see cref="AudioSignalTracker"/> e non perde byte allo stop (flush finale prima del completamento).
/// </summary>
public sealed class WasapiAudioCapture : IAudioCapture
{
    private readonly IAudioDeviceProvider _devices;
    private readonly IMicrophonePrivacy _privacy;
    private readonly IMicrophoneSourceFactory _factory;
    private readonly TimeProvider _time;
    private readonly TimeSpan _readyTimeout;
    private readonly AudioChunker _chunker;
    private readonly AudioSignalTracker _tracker = new();
    private readonly object _gate = new();

    private IMicrophoneSource? _source;
    private ITimer? _readyTimer;
    private bool _ready;
    private bool _active;
    private bool _tearingDown;

    public WasapiAudioCapture(AudioConfig config)
        : this(
            new AudioDevices(),
            new MicrophonePrivacy(),
            new WasapiMicrophoneSourceFactory(),
            TimeProvider.System,
            TimeSpan.FromSeconds(config.ReadyTimeoutSeconds),
            config.SampleRate * config.Channels * sizeof(short) * config.ChunkDurationMs / 1000)
    {
    }

    public WasapiAudioCapture(
        IAudioDeviceProvider devices,
        IMicrophonePrivacy privacy,
        IMicrophoneSourceFactory factory,
        TimeProvider time,
        TimeSpan readyTimeout,
        int targetChunkBytes)
    {
        _devices = devices;
        _privacy = privacy;
        _factory = factory;
        _time = time;
        _readyTimeout = readyTimeout;
        _chunker = new AudioChunker(targetChunkBytes);
    }

    public event EventHandler? Ready;

    public event EventHandler<ReadOnlyMemory<byte>>? ChunkReady;

    public event EventHandler<double>? LevelChanged;

    public event EventHandler<CaptureFailure>? Failed;

    public async Task StartAsync(string? deviceId, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_active)
            {
                throw new InvalidOperationException("Audio capture is already running.");
            }

            _active = true;
            _ready = false;
            _tearingDown = false;
        }

        _chunker.Reset();
        _tracker.Reset();

        if (!_privacy.IsAccessAllowed())
        {
            FailAndTearDown(new CaptureFailure(
                "microphone_permission_denied",
                $"Microphone access is disabled in Windows settings. Enable it in {MicrophonePrivacy.SettingsUri}."));
            return;
        }

        IReadOnlyList<AudioDeviceInfo> devices = await _devices.ListAsync(ct).ConfigureAwait(false);
        AudioDeviceInfo? device = deviceId is null
            ? devices.FirstOrDefault(static candidate => candidate.IsDefault) ?? devices.FirstOrDefault()
            : devices.FirstOrDefault(candidate => string.Equals(candidate.Id, deviceId, StringComparison.Ordinal));

        if (device is null)
        {
            FailAndTearDown(new CaptureFailure(
                "no_input_device",
                deviceId is null
                    ? "No active microphone was found."
                    : $"The configured microphone '{deviceId}' is not available."));
            return;
        }

        IMicrophoneSource source;
        try
        {
            source = _factory.Create(device);
        }
        catch (Exception ex)
        {
            FailAndTearDown(new CaptureFailure("device_unavailable", ex.Message));
            return;
        }

        lock (_gate)
        {
            if (_tearingDown)
            {
                return;
            }

            _source = source;
        }

        source.AudioArrived += OnAudioArrived;
        source.DeviceLost += OnDeviceLost;

        try
        {
            await source.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailAndTearDown(new CaptureFailure("device_unavailable", ex.Message));
            return;
        }

        lock (_gate)
        {
            if (!_active || _ready || _tearingDown)
            {
                return;
            }

            _readyTimer = _time.CreateTimer(
                static state => ((WasapiAudioCapture)state!).OnReadyTimeout(),
                this,
                _readyTimeout,
                Timeout.InfiniteTimeSpan);
        }
    }

    public async Task<AudioSummary> StopAsync(CancellationToken ct)
    {
        IMicrophoneSource? source;
        ITimer? readyTimer;
        lock (_gate)
        {
            if (_tearingDown)
            {
                return _tracker.BuildSummary();
            }

            _tearingDown = true;
            readyTimer = _readyTimer;
            _readyTimer = null;
            source = _source;
            _source = null;
        }

        readyTimer?.Dispose();

        if (source is not null)
        {
            source.AudioArrived -= OnAudioArrived;
            source.DeviceLost -= OnDeviceLost;
            try
            {
                await source.StopAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }

            byte[] tail = _chunker.Flush();
            if (tail.Length > 0)
            {
                ChunkReady?.Invoke(this, tail);
            }

            try
            {
                await source.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        lock (_gate)
        {
            _active = false;
            _ready = false;
            _tearingDown = false;
        }

        return _tracker.BuildSummary();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private void OnAudioArrived(object? sender, ReadOnlyMemory<byte> pcm)
    {
        bool first;
        double level;
        lock (_gate)
        {
            first = !_ready;
            _ready = true;
            ITimer? timer = _readyTimer;
            _readyTimer = null;
            timer?.Dispose();
            level = _tracker.AppendPcm16(pcm.Span);
        }

        if (first)
        {
            Ready?.Invoke(this, EventArgs.Empty);
        }

        foreach (byte[] chunk in _chunker.Feed(pcm.Span))
        {
            ChunkReady?.Invoke(this, chunk);
        }

        LevelChanged?.Invoke(this, level);
    }

    private void OnDeviceLost(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (_tearingDown)
            {
                return;
            }
        }

        Failed?.Invoke(this, new CaptureFailure(
            "device_lost",
            "The microphone disappeared during recording; the audio already captured was kept."));
    }

    private void OnReadyTimeout()
    {
        lock (_gate)
        {
            if (_ready || !_active || _tearingDown)
            {
                return;
            }
        }

        FailAndTearDown(new CaptureFailure(
            "microphone_not_ready",
            $"No audio arrived from the microphone within {_readyTimeout.TotalSeconds:0.#} s."));
    }

    private void FailAndTearDown(CaptureFailure failure)
    {
        IMicrophoneSource? source;
        ITimer? readyTimer;
        lock (_gate)
        {
            if (_tearingDown)
            {
                return;
            }

            _tearingDown = true;
            readyTimer = _readyTimer;
            _readyTimer = null;
            source = _source;
            _source = null;
            _active = false;
            _ready = false;
        }

        readyTimer?.Dispose();
        Failed?.Invoke(this, failure);

        if (source is not null)
        {
            source.AudioArrived -= OnAudioArrived;
            source.DeviceLost -= OnDeviceLost;
            try
            {
                source.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }

            try
            {
                source.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
            }
        }

        lock (_gate)
        {
            _tearingDown = false;
        }
    }
}
