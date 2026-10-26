namespace GangioKeet.Client.Context;

public interface IForegroundWindowService
{
    nint CaptureHandle();

    ForegroundWindowInfo Read(nint handle);

    nint GetForegroundHandle();

    bool TryActivate(nint handle);

    bool IsAlive(ForegroundWindowInfo target);
}
