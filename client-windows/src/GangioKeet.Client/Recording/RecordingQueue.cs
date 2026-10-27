using System.Threading.Channels;
using GangioKeet.Client.Protocol;
using GangioKeet.Inference;

namespace GangioKeet.Client.Recording;

/// <summary>
/// Bounded, non-blocking recording queue. A full queue rejects the capture
/// explicitly and increments <see cref="Dropped"/>; the dictation result is
/// never delayed by disk I/O.
/// </summary>
public sealed class RecordingQueue : IRecordingSink, IAsyncDisposable
{
    private readonly Channel<Job> _channel;
    private readonly IRecordingWriter _writer;
    private readonly Action<string>? _warning;
    private readonly Task _worker;
    private int _dropped;
    private int _failed;
    private int _disposed;

    public RecordingQueue(IRecordingWriter writer, int capacity = 4, Action<string>? warning = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _writer = writer;
        _warning = warning;
        _channel = Channel.CreateBounded<Job>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
        _worker = Task.Run(ConsumeAsync);
    }

    public int Dropped => Volatile.Read(ref _dropped);
    public int Failed => Volatile.Read(ref _failed);

    public bool TryEnqueue(ReadOnlyMemory<byte> pcm, PipelineResult result, StartMessage start)
    {
        if (!_channel.Writer.TryWrite(new(pcm.ToArray(), result, start)))
        {
            Interlocked.Increment(ref _dropped);
            _warning?.Invoke("recording_queue_full");
            return false;
        }
        return true;
    }

    public async Task DrainAsync(CancellationToken ct)
    {
        _channel.Writer.TryComplete();
        try { await _worker.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task ConsumeAsync()
    {
        await foreach (Job job in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (await _writer.RecordAsync(job.Pcm, job.Result, job.Start, CancellationToken.None)
                    .ConfigureAwait(false) is null)
                {
                    Interlocked.Increment(ref _failed);
                    _warning?.Invoke("recording_write_failed");
                }
            }
            catch (Exception error)
            {
                Interlocked.Increment(ref _failed);
                _warning?.Invoke("recording_write_failed:" + error.GetType().Name);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await DrainAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private sealed record Job(byte[] Pcm, PipelineResult Result, StartMessage Start);
}
