using Microsoft.Win32.SafeHandles;

namespace GangioKeet.Inference.Native;

public sealed record NativeDevice(string Backend, string Description, ulong MemoryTotal, ulong MemoryFree);

public interface ITranscribeApi
{
    string RuntimeVersion { get; }
    void Initialize(string directory);
    SafeTranscribeModel Load(string path, string backend);
    SafeTranscribeSession CreateSession(SafeTranscribeModel model);
    NativeDevice GetDevice(SafeTranscribeModel model);
    int Run(SafeTranscribeSession session, float[] samples, Func<bool> abort);
    nint GetText(SafeTranscribeSession session);
}

public sealed class SafeTranscribeModel : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Action<nint> _release;
    public SafeTranscribeModel(nint value, Action<nint> release) : base(true)
    {
        _release = release;
        SetHandle(value);
    }
    protected override bool ReleaseHandle() { _release(handle); return true; }
}

public sealed class SafeTranscribeSession : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly Action<nint> _release;
    public SafeTranscribeSession(nint value, Action<nint> release) : base(true)
    {
        _release = release;
        SetHandle(value);
    }
    protected override bool ReleaseHandle() { _release(handle); return true; }
}
