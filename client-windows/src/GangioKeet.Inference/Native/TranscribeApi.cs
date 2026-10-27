using System.Runtime.InteropServices;

namespace GangioKeet.Inference.Native;

/// <summary>ABI of transcribe.cpp 0.4.1, commit 4d7c1cce, Windows x64.</summary>
public sealed class TranscribeApi : ITranscribeApi
{
    public string RuntimeVersion { get; private set; } = "not_loaded";

    public void Initialize(string directory)
    {
        NativeLibraryResolver.LoadTranscribe(directory);
        RuntimeVersion = Marshal.PtrToStringUTF8(Methods.Version()) ?? "unknown";
        if (RuntimeVersion != "0.4.1") throw new InvalidDataException("Unexpected native transcribe version.");
        Check(Methods.InitBackends(Path.GetFullPath(directory)));
    }

    public SafeTranscribeModel Load(string path, string backend)
    {
        LoadParams options = default;
        Methods.LoadParamsInit(ref options);
        CheckSize<LoadParams>(options.Size);
        options.Backend = backend switch { "cpu" => 1, "vulkan" => 3, _ => throw new ArgumentException("Backend must be cpu or vulkan.") };
        Check(Methods.ModelLoad(path, ref options, out nint model));
        return new SafeTranscribeModel(model, Methods.ModelFree);
    }

    public SafeTranscribeSession CreateSession(SafeTranscribeModel model)
    {
        Check(Methods.SessionInit(model, 0, out nint session));
        return new SafeTranscribeSession(session, Methods.SessionFree);
    }

    public NativeDevice GetDevice(SafeTranscribeModel model)
    {
        nint device = Methods.ModelDevice(model);
        return ReadDevice(device);
    }

    public IReadOnlyList<NativeDevice> ListDevices()
    {
        List<NativeDevice> devices = [];
        for (int i = 0; i < Methods.DeviceCount(); i++) devices.Add(ReadDevice(Methods.DeviceGet(i)));
        return devices;
    }

    private static NativeDevice ReadDevice(nint device)
    {
        DeviceInfo info = default;
        Methods.DeviceInfoInit(ref info);
        CheckSize<DeviceInfo>(info.Size);
        Check(Methods.DeviceInfoGet(device, ref info));
        return new NativeDevice(Marshal.PtrToStringUTF8(info.Kind) ?? "unknown",
            Marshal.PtrToStringUTF8(info.Description) ?? "unknown", info.MemoryTotal, info.MemoryFree);
    }

    public int Run(SafeTranscribeSession session, float[] samples, Func<bool> abort)
    {
        RunParams options = default;
        Methods.RunParamsInit(ref options);
        CheckSize<RunParams>(options.Size);
        options.Timestamps = 0;
        AbortCallback callback = _ => abort() ? (byte)1 : (byte)0;
        Methods.SetAbort(session, Marshal.GetFunctionPointerForDelegate(callback), 0);
        try { return Methods.Run(session, samples, samples.Length, ref options); }
        finally
        {
            Methods.SetAbort(session, 0, 0);
            GC.KeepAlive(callback);
        }
    }

    public nint GetText(SafeTranscribeSession session) => Methods.FullText(session);

    private static void CheckSize<T>(ulong nativeSize) where T : struct
    {
        if (nativeSize != (ulong)Marshal.SizeOf<T>())
            throw new InvalidDataException($"Native ABI layout mismatch for {typeof(T).Name}.");
    }

    private static void Check(int status)
    {
        if (status != 0)
            throw new InvalidOperationException($"Native transcribe status {status}: {Marshal.PtrToStringUTF8(Methods.Status(status))}");
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte AbortCallback(nint userData);

    [StructLayout(LayoutKind.Sequential)]
    private struct LoadParams { public ulong Size; public int Backend; public nint Device; public byte NoMmap; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo
    {
        public ulong Size;
        public nint Name, Description, Kind, DeviceId;
        public ulong MemoryTotal, MemoryFree;
        public int DeviceType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RunParams
    {
        public ulong Size;
        public int Task, Timestamps, Pnc, Itn, Diarize;
        public nint Language, TargetLanguage;
        public byte KeepSpecialTags;
        public nint Family;
        public int Drafts;
        public nint Vocabulary;
        public int VocabularyCount;
        public nint Prompt, Prefix;
    }

    private static class Methods
    {
        private const string Library = "gk-transcribe";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_version")]
        internal static extern nint Version();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_status_string")]
        internal static extern nint Status(int status);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_init_backends")]
        internal static extern int InitBackends([MarshalAs(UnmanagedType.LPUTF8Str)] string directory);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_model_load_params_init")]
        internal static extern void LoadParamsInit(ref LoadParams options);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_model_load_file")]
        internal static extern int ModelLoad([MarshalAs(UnmanagedType.LPUTF8Str)] string path, ref LoadParams options, out nint model);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_model_free")]
        internal static extern void ModelFree(nint model);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_session_init")]
        internal static extern int SessionInit(SafeTranscribeModel model, nint options, out nint session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_session_free")]
        internal static extern void SessionFree(nint session);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_model_device")]
        internal static extern nint ModelDevice(SafeTranscribeModel model);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_device_count")]
        internal static extern int DeviceCount();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_device_get")]
        internal static extern nint DeviceGet(int index);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_device_info_init")]
        internal static extern void DeviceInfoInit(ref DeviceInfo info);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_device_get_info")]
        internal static extern int DeviceInfoGet(nint device, ref DeviceInfo info);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_run_params_init")]
        internal static extern void RunParamsInit(ref RunParams options);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_set_abort_callback")]
        internal static extern void SetAbort(SafeTranscribeSession session, nint callback, nint userData);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_run")]
        internal static extern int Run(SafeTranscribeSession session, [In] float[] samples, int count, ref RunParams options);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl, EntryPoint = "transcribe_full_text")]
        internal static extern nint FullText(SafeTranscribeSession session);
    }
}
