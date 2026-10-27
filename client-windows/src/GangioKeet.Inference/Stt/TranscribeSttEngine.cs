using System.Buffers.Binary;
using System.Runtime.InteropServices;
using GangioKeet.Inference.Native;

namespace GangioKeet.Inference.Stt;

public sealed class TranscribeSttEngine(ModelArtifact artifact, string modelRoot,
    string nativeDirectory, string backend = "vulkan", ITranscribeApi? api = null) : ISttEngine
{
    private readonly ITranscribeApi _api = api ?? new TranscribeApi();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _disposeGate = new();
    private SafeTranscribeModel? _model;
    private SafeTranscribeSession? _session;
    private EngineIdentity? _identity;
    private Task? _disposeTask;
    private int _disposeRequested;

    public async Task LoadAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_model is not null) return;
            await ModelCatalog.VerifyArtifactAsync(artifact, modelRoot, linked.Token).ConfigureAwait(false);
            await Task.Run(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                _api.Initialize(nativeDirectory);
                linked.Token.ThrowIfCancellationRequested();
                SafeTranscribeModel? model = null;
                SafeTranscribeSession? session = null;
                try
                {
                    model = _api.Load(Path.GetFullPath(artifact.Files[0].Path, Path.GetFullPath(modelRoot)), backend);
                    linked.Token.ThrowIfCancellationRequested();
                    NativeDevice device = _api.GetDevice(model);
                    if (!string.Equals(device.Backend, backend, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Requested {backend}, native model selected {device.Backend}.");
                    session = _api.CreateSession(model);
                    linked.Token.ThrowIfCancellationRequested();
                    _identity = new EngineIdentity(artifact.ArtifactRepository ?? artifact.SourceCheckpoint,
                        artifact.Revision, artifact.Files[0].Sha256, _api.RuntimeVersion,
                        device.Backend, artifact.Precision, device.Description);
                    _model = model;
                    _session = session;
                    model = null;
                    session = null;
                }
                finally { session?.Dispose(); model?.Dispose(); }
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<SpeechTranscript> TranscribeAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
    {
        if (pcm16.IsEmpty || pcm16.Length % 2 != 0) throw new ArgumentException("Audio must be nonempty PCM16 mono 16 kHz.", nameof(pcm16));
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (_session is null || _identity is null) throw new InvalidOperationException("Load the model before transcription.");
            float[] samples = new float[pcm16.Length / 2];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm16.Span.Slice(i * 2, 2)) / 32768f;
            return await Task.Run(() =>
            {
                linked.Token.ThrowIfCancellationRequested();
                int status = _api.Run(_session, samples, () => linked.IsCancellationRequested);
                linked.Token.ThrowIfCancellationRequested();
                if (status == 13) throw new OperationCanceledException("Native transcription aborted.", linked.Token);
                if (status != 0) throw new InvalidDataException($"Native transcription failed with status {status}; partial output is not a complete transcript.");
                string text = Marshal.PtrToStringUTF8(_api.GetText(_session))?.Trim() ?? "";
                return new SpeechTranscript(text, _identity);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _session?.Dispose();
            _session = null;
            _model?.Dispose();
            _model = null;
            _lifetime.Dispose();
        }
        finally { _gate.Release(); }
    }
}
