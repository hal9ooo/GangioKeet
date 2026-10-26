using GangioKeet.Client.Configuration;
using GangioKeet.Client.Platform;

namespace GangioKeet.Client.Audio;

/// <summary>
/// Policy di cattura sopra un <see cref="IMicrophoneSource"/> sostituibile: risolve il dispositivo
/// (default a ogni sessione, ID esplicito mai rimpiazzato in silenzio), verifica il permesso privacy,
/// pubblica <see cref="Ready"/> al primo callback reale, misura la sessione con
/// <see cref="AudioSignalTracker"/> e non perde byte allo stop (drain della sorgente e flush finale
/// prima del completamento).
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
    private bool _stopping;
    private bool _deviceLost;
    private bool _closed;

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
            _stopping = false;
            _deviceLost = false;
            _closed = false;
        }

        _chunker.Reset();
        _tracker.Reset();

        if (!_privacy.IsAccessAllowed())
        {
            await FailAndTearDownAsync(new CaptureFailure(
                "microphone_permission_denied",
                $"Microphone access is disabled in Windows settings. Enable it in {MicrophonePrivacy.SettingsUri}."))
                .ConfigureAwait(false);
            return;
        }

        IReadOnlyList<AudioDeviceInfo> devices = await _devices.ListAsync(ct).ConfigureAwait(false);
        AudioDeviceInfo? device = deviceId is null
            ? devices.FirstOrDefault(static candidate => candidate.IsDefault) ?? devices.FirstOrDefault()
            : devices.FirstOrDefault(candidate => string.Equals(candidate.Id, deviceId, StringComparison.Ordinal));

        if (device is null)
        {
            await FailAndTearDownAsync(new CaptureFailure(
                "no_input_device",
                deviceId is null
                    ? "No active microphone was found."
                    : $"The configured microphone '{deviceId}' is not available."))
                .ConfigureAwait(false);
            return;
        }

        IMicrophoneSource source;
        try
        {
            source = _factory.Create(device);
        }
        catch (Exception ex)
        {
            await FailAndTearDownAsync(new CaptureFailure("device_unavailable", ex.Message)).ConfigureAwait(false);
            return;
        }

        source.AudioArrived += OnAudioArrived;
        source.DeviceLost += OnDeviceLost;

        lock (_gate)
        {
            _source = source;
        }

        try
        {
            await source.StartAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailAndTearDownAsync(new CaptureFailure("device_unavailable", ex.Message)).ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            if (!_active || _ready || _stopping)
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
            if (_stopping)
            {
                return _tracker.BuildSummary();
            }

            _stopping = true;
            readyTimer = _readyTimer;
            _readyTimer = null;
            source = _source;
        }

        readyTimer?.Dispose();

        if (source is not null)
        {
            // La sottoscrizione resta attiva durante lo stop: la sorgente drena il resampler e
            // consegna gli ultimi byte reali, che devono entrare nei chunk invece di essere persi.
            try
            {
                await source.StopAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
            }

            source.AudioArrived -= OnAudioArrived;
            source.DeviceLost -= OnDeviceLost;
        }

        byte[] tail;
        lock (_gate)
        {
            _closed = true;
            tail = _chunker.Flush();
        }

        if (tail.Length > 0)
        {
            ChunkReady?.Invoke(this, tail);
        }

        if (source is not null)
        {
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
            _source = null;
            _active = false;
            _ready = false;
            _stopping = false;
            _deviceLost = false;
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
        IReadOnlyList<byte[]> chunks;
        lock (_gate)
        {
            if (!_active || _closed)
            {
                return;
            }

            first = !_ready;
            _ready = true;
            ITimer? timer = _readyTimer;
            _readyTimer = null;
            timer?.Dispose();
            level = _tracker.AppendPcm16(pcm.Span);
            chunks = _chunker.Feed(pcm.Span);
        }

        if (first)
        {
            Ready?.Invoke(this, EventArgs.Empty);
        }

        foreach (byte[] chunk in chunks)
        {
            ChunkReady?.Invoke(this, chunk);
        }

        LevelChanged?.Invoke(this, level);
    }

    private void OnDeviceLost(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (!_active || _stopping || _deviceLost)
            {
                return;
            }

            _deviceLost = true;
        }

        // Il dispositivo ha già smesso di consegnare: l'acquisizione è interrotta e l'audio
        // precedente resta nel chunker/tracker, che lo stop successivo evacua. Il teardown non
        // parte da qui per non fare sync-over-async sul thread del recorder.
        Failed?.Invoke(this, new CaptureFailure(
            "device_lost",
            "The microphone disappeared during recording; the audio already captured was kept."));
    }

    private void OnReadyTimeout()
    {
        _ = FailAndTearDownAsync(
            new CaptureFailure(
                "microphone_not_ready",
                $"No audio arrived from the microphone within {_readyTimeout.TotalSeconds:0.#} s."),
            onlyIfNotReady: true);
    }

    private async Task FailAndTearDownAsync(CaptureFailure failure, bool onlyIfNotReady = false)
    {
        IMicrophoneSource? source;
        ITimer? readyTimer;
        lock (_gate)
        {
            if (_stopping || (onlyIfNotReady && (_ready || _deviceLost)))
            {
                return;
            }

            _stopping = true;
            _closed = true;
            readyTimer = _readyTimer;
            _readyTimer = null;
            source = _source;
            _source = null;
        }

        readyTimer?.Dispose();
        Failed?.Invoke(this, failure);

        if (source is not null)
        {
            source.AudioArrived -= OnAudioArrived;
            source.DeviceLost -= OnDeviceLost;
            try
            {
                await source.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
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
            _stopping = false;
        }
    }
}
