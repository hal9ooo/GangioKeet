using System.Runtime.InteropServices;
using GangioKeet.Inference.Native;

namespace GangioKeet.Inference.Stt;

public sealed class NeMoSpeechSttEngine : ISttEngine
{
    private readonly ModelArtifact _artifact;
    private readonly string _modelRoot;
    private readonly string _backend;
    private readonly Func<CancellationToken, Task<string>> _runtimeLoader;
    private readonly object _gate = new();
    private nint _recognizer;
    private EngineIdentity? _identity;

    public NeMoSpeechSttEngine(ModelArtifact artifact, string modelRoot, string backend,
        Func<CancellationToken, Task<string>> runtimeLoader)
    {
        _artifact = artifact ?? throw new ArgumentNullException(nameof(artifact));
        _modelRoot = modelRoot ?? throw new ArgumentNullException(nameof(modelRoot));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _runtimeLoader = runtimeLoader ?? throw new ArgumentNullException(nameof(runtimeLoader));
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            if (_recognizer != 0) return;
        }

        if (_artifact.Files.Count < 2)
            throw new InvalidDataException("Nemotron requires a decoder and an encoder GGUF.");
        string decoder = Path.Combine(_modelRoot, _artifact.Files[0].Path);
        string encoder = Path.Combine(_modelRoot, _artifact.Files[1].Path);
        if (!File.Exists(decoder)) throw new FileNotFoundException("Nemotron decoder not found.", decoder);
        if (!File.Exists(encoder)) throw new FileNotFoundException("Nemotron encoder not found.", encoder);

        string runtimeDirectory = await _runtimeLoader(ct).ConfigureAwait(false);
        NativeLibraryResolver.LoadNeMoSpeech(runtimeDirectory);

        NeMoSpeechApi.BackendConfig backend = new()
        {
            size = (UIntPtr)Marshal.SizeOf<NeMoSpeechApi.BackendConfig>(),
            gpu = _backend.Equals("cpu", StringComparison.OrdinalIgnoreCase) ? -1 : 0
        };
        NeMoSpeechApi.SalmConfig salm = NeMoSpeechApi.SalmConfigDefault();
        salm.encoderPath = Marshal.StringToCoTaskMemUTF8(encoder);
        NeMoSpeechApi.ModelConfig model = new()
        {
            size = (UIntPtr)Marshal.SizeOf<NeMoSpeechApi.ModelConfig>(),
            path = Marshal.StringToCoTaskMemUTF8(decoder),
            name = IntPtr.Zero
        };

        nint backendPtr = Alloc(backend);
        nint salmPtr = Alloc(salm);
        nint modelPtr = Alloc(model);
        NeMoSpeechApi.RecognizerConfig config = new()
        {
            size = (UIntPtr)Marshal.SizeOf<NeMoSpeechApi.RecognizerConfig>(),
            backend = backendPtr,
            model = modelPtr,
            salm = salmPtr
        };
        try
        {
            int status = NeMoSpeechApi.Create(ref config, out nint recognizer);
            if (status != 0)
                throw new InvalidOperationException($"NeMo-Speech model load failed: {NeMoSpeechApi.LastErrorText()}");
            lock (_gate) _recognizer = recognizer;
            _identity = new EngineIdentity(_artifact.Id, _artifact.Revision, _artifact.Files[0].Sha256,
                NeMoSpeechApi.VersionText(), _backend, _artifact.Precision,
                Environment.MachineName);
        }
        finally
        {
            Marshal.FreeCoTaskMem(model.path);
            Marshal.FreeCoTaskMem(salm.encoderPath);
            Marshal.FreeHGlobal(backendPtr);
            Marshal.FreeHGlobal(salmPtr);
            Marshal.FreeHGlobal(modelPtr);
        }
    }

    public Task<SpeechTranscript> TranscribeAsync(ReadOnlyMemory<byte> pcm16, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (pcm16.Length % 2 != 0) throw new ArgumentException("PCM16 requires an even byte count.", nameof(pcm16));
        float[] samples = new float[pcm16.Length / 2];
        ReadOnlySpan<byte> bytes = pcm16.Span;
        for (int i = 0; i < samples.Length; i++)
            samples[i] = BitConverter.ToInt16(bytes.Slice(i * 2, 2)) / 32768f;

        NeMoSpeechApi.RecognitionOptions options = NeMoSpeechApi.RecognitionOptionsDefault();
        options.size = (UIntPtr)Marshal.SizeOf<NeMoSpeechApi.RecognitionOptions>();
        options.enableAutomaticPunctuation = true;
        options.languageCode = IntPtr.Zero;

        lock (_gate)
        {
            if (_recognizer == 0) throw new InvalidOperationException("Nemotron is not loaded.");
            int status = NeMoSpeechApi.RecognizeF32(_recognizer, ref options, samples,
                (UIntPtr)samples.Length, 16000, out nint result);
            if (status != 0)
                throw new InvalidOperationException($"Nemotron transcription failed: {NeMoSpeechApi.LastErrorText()}");
            try
            {
                string text = Marshal.PtrToStringUTF8(NeMoSpeechApi.ResultTranscript(result, UIntPtr.Zero)) ?? "";
                return Task.FromResult(new SpeechTranscript(text, Identity));
            }
            finally
            {
                NeMoSpeechApi.ResultDestroy(result);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_recognizer != 0)
            {
                NeMoSpeechApi.Destroy(_recognizer);
                _recognizer = 0;
            }
        }
        return ValueTask.CompletedTask;
    }

    private EngineIdentity Identity => _identity ?? new EngineIdentity(_artifact.Id, _artifact.Revision,
        _artifact.Files[0].Sha256, "unknown", _backend, _artifact.Precision, Environment.MachineName);

    private static nint Alloc<T>(T value) where T : struct
    {
        nint pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value, pointer, fDeleteOld: false);
        return pointer;
    }
}
