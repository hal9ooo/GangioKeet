using System.Runtime.InteropServices;

namespace GangioKeet.Inference.Native;

internal static class NeMoSpeechApi
{
    private const string Library = "nemo_speech_asr_c";

    [StructLayout(LayoutKind.Sequential)]
    internal struct BackendConfig
    {
        public UIntPtr size;
        public int gpu;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ModelConfig
    {
        public UIntPtr size;
        public IntPtr path;
        public IntPtr name;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SalmConfig
    {
        public UIntPtr size;
        public IntPtr encoderPath;
        public int maxSequences;
        public int mtpDraft;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RecognizerConfig
    {
        public UIntPtr size;
        public IntPtr backend;
        public IntPtr model;
        public IntPtr streaming;
        public IntPtr decoder;
        public IntPtr vad;
        public IntPtr endpointing;
        public IntPtr postproc;
        public IntPtr diar;
        public IntPtr batching;
        public IntPtr salm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RecognitionOptions
    {
        public UIntPtr size;
        public IntPtr requestId;
        public IntPtr languageCode;
        [MarshalAs(UnmanagedType.I1)] public bool interimResults;
        [MarshalAs(UnmanagedType.I1)] public bool enableWordTimeOffsets;
        [MarshalAs(UnmanagedType.I1)] public bool enableAutomaticPunctuation;
        [MarshalAs(UnmanagedType.I1)] public bool verbatimTranscripts;
        [MarshalAs(UnmanagedType.I1)] public bool profanityFilter;
        public int stopHistoryEouMs;
        public IntPtr speechContexts;
        public UIntPtr speechContextCount;
        public int maxAlternatives;
        [MarshalAs(UnmanagedType.I1)] public bool enableSpeakerDiarization;
        public int maxSpeakerCount;
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nemo_speech_asr_salm_config_default")]
    internal static extern SalmConfig SalmConfigDefault();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nemo_speech_asr_recognition_options_default")]
    internal static extern RecognitionOptions RecognitionOptionsDefault();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nemo_speech_asr_create")]
    internal static extern int Create(ref RecognizerConfig config, out IntPtr recognizer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nemo_speech_asr_destroy")]
    internal static extern void Destroy(IntPtr recognizer);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nemo_speech_asr_recognize_f32")]
    internal static extern int RecognizeF32(IntPtr recognizer, ref RecognitionOptions options,
        float[] samples, UIntPtr sampleCount, int sampleRate, out IntPtr result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nemo_speech_asr_result_transcript")]
    internal static extern IntPtr ResultTranscript(IntPtr result, UIntPtr alternative);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nemo_speech_asr_result_destroy")]
    internal static extern void ResultDestroy(IntPtr result);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nemo_speech_asr_last_error")]
    internal static extern IntPtr LastError();

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nemo_speech_asr_version")]
    internal static extern IntPtr Version();

    internal static string LastErrorText() => Marshal.PtrToStringUTF8(LastError()) ?? "unknown native error";

    internal static string VersionText() => Marshal.PtrToStringUTF8(Version()) ?? "unknown";
}
