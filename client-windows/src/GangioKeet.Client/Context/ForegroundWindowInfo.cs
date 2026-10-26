namespace GangioKeet.Client.Context;

public record ForegroundWindowInfo(nint Handle, int ProcessId, string ProcessName, string WindowTitle);
