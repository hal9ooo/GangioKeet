using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace GangioKeet.Client.Audio;

/// <summary>Crea l'adapter WASAPI per un endpoint già enumerato.</summary>
public sealed class WasapiMicrophoneSourceFactory : IMicrophoneSourceFactory
{
    public IMicrophoneSource Create(AudioDeviceInfo device)
    {
        using MMDeviceEnumerator enumerator = new();
        MMDevice endpoint = enumerator.GetDevice(device.Id);
        return new WasapiMicrophoneSource(endpoint);
    }
}

/// <summary>
/// Cattura WASAPI shared nel formato nativo dell'endpoint, conversione continua a PCM16/16 kHz mono
/// (downmix manuale dei canali, poi <see cref="MediaFoundationResampler"/> per la frequenza) e consegna
/// dei byte a un pump dedicato: i callback audio non fanno I/O di rete né UI. Lo stop drena il
/// resampler prima di completare, così nessun byte resta nel buffer.
/// </summary>
public sealed class WasapiMicrophoneSource : IMicrophoneSource
{
    private const int TargetSampleRate = 16000;
    private const int EmitBlockBytes = 3200;
    private const int PumpIdleDelayMs = 20;

    private readonly MMDevice _device;
    private readonly WasapiRecorder _recorder;
    private readonly BufferedWaveProvider _nativeMono;
    private readonly IWaveProvider _output;
    private readonly CancellationTokenSource _pumpCts = new();
    private readonly object _gate = new();

    private Task? _pump;
    private bool _started;
    private bool _disposed;
    private long _outputBudget;

    public WasapiMicrophoneSource(MMDevice device)
    {
        _device = device;
        _recorder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithMmcssThreadPriority("Pro Audio")
            .Build();

        WaveFormat input = _recorder.WaveFormat;
        _nativeMono = new BufferedWaveProvider(
            new WaveFormat(input.SampleRate, 16, 1),
            TimeSpan.FromSeconds(2))
        {
            DiscardOnBufferOverflow = true,
        };

        _output = input.SampleRate == TargetSampleRate
            ? _nativeMono
            : new MediaFoundationResampler(_nativeMono, new WaveFormat(TargetSampleRate, 16, 1));

        _recorder.DataAvailable += OnDataAvailable;
        _recorder.RecordingStopped += OnRecordingStopped;
    }

    public event EventHandler<ReadOnlyMemory<byte>>? AudioArrived;

    public event EventHandler? DeviceLost;

    public Task StartAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_started)
            {
                throw new InvalidOperationException("The microphone source is already running.");
            }

            _started = true;
        }

        Interlocked.Exchange(ref _outputBudget, 0);
        _recorder.StartRecording();
        _pump = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
        }

        try
        {
            _recorder.StopRecording();
        }
        catch (Exception)
        {
        }

        _pumpCts.Cancel();

        if (_pump is { } pump)
        {
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        Drain();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _recorder.DataAvailable -= OnDataAvailable;
        _recorder.RecordingStopped -= OnRecordingStopped;

        if (!ReferenceEquals(_output, _nativeMono))
        {
            (_output as IDisposable)?.Dispose();
        }

        _recorder.Dispose();
        _pumpCts.Dispose();
        _device.Dispose();
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[EmitBlockBytes];
        while (!ct.IsCancellationRequested)
        {
            long budget = Interlocked.Read(ref _outputBudget);
            int maxRead = (int)Math.Min(buffer.Length, budget);
            if (maxRead >= 2)
            {
                int read;
                try
                {
                    read = _output.Read(buffer.AsSpan(0, maxRead));
                }
                catch (Exception)
                {
                    return;
                }

                if (read > 0)
                {
                    Interlocked.Add(ref _outputBudget, -read);
                    AudioArrived?.Invoke(this, buffer.AsMemory(0, read).ToArray());
                    continue;
                }
            }

            try
            {
                await Task.Delay(PumpIdleDelayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Drain()
    {
        byte[] buffer = new byte[EmitBlockBytes];
        for (int guard = 0; guard < 256; guard++)
        {
            long budget = Interlocked.Read(ref _outputBudget);
            int maxRead = (int)Math.Min(buffer.Length, budget);
            if (maxRead < 2)
            {
                return;
            }

            int read;
            try
            {
                read = _output.Read(buffer.AsSpan(0, maxRead));
            }
            catch (Exception)
            {
                return;
            }

            if (read <= 0)
            {
                return;
            }

            Interlocked.Add(ref _outputBudget, -read);
            AudioArrived?.Invoke(this, buffer.AsMemory(0, read).ToArray());
        }
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        byte[] mono;
        try
        {
            mono = Pcm16.FromCaptureBuffer(buffer, _recorder.WaveFormat);
        }
        catch (NotSupportedException)
        {
            return;
        }

        if (mono.Length > 0)
        {
            _nativeMono.AddSamples(mono, 0, mono.Length);

            // Budget di output proporzionale ai campioni reali: il resampler non deve
            // inventare silenzio quando l'input è esaurito. 48k→16k produce 1/3 dei byte.
            int nativeRate = _nativeMono.WaveFormat.SampleRate;
            long produced = (long)mono.Length * TargetSampleRate / nativeRate;
            if (produced > 0)
            {
                Interlocked.Add(ref _outputBudget, produced);
            }
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null)
        {
            return;
        }

        DeviceLost?.Invoke(this, EventArgs.Empty);
    }
}
