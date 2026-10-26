namespace GangioKeet.Client.Audio;

/// <summary>Un endpoint di input attivo. <see cref="AudioDeviceInfo.IsDefault"/> è il default di Windows al momento dell'enumerazione.</summary>
public sealed record AudioDeviceInfo(string Id, string Name, bool IsDefault);

/// <summary>
/// Diagnosi di cattura. Codici previsti: <c>microphone_permission_denied</c>,
/// <c>no_input_device</c>, <c>device_unavailable</c>, <c>device_lost</c>, <c>microphone_not_ready</c>.
/// </summary>
public sealed record CaptureFailure(string Code, string Message);

/// <summary>Diagnosi dell'audio realmente consegnato in una sessione di cattura.</summary>
/// <param name="SampleCount">Campioni PCM16 appartenenti alla sessione; 0 = nessun audio ricevuto.</param>
/// <param name="AllZero">True solo con SampleCount &gt; 0 e tutti i campioni a zero (microfono muto ≠ nessun callback).</param>
/// <param name="Peak">Piccovolle assoluto massimo normalizzato [0..1].</param>
public sealed record AudioSummary(long SampleCount, bool AllZero, double Peak);

/// <summary>
/// Adapter della cattura PCM16/16 kHz mono. <see cref="Ready"/> scatta al primo callback audio reale,
/// non al ritorno di <see cref="StartAsync"/>; ogni evento parte dal thread di cattura, mai I/O di rete
/// o UI nel consumer sincrono. <see cref="StopAsync"/> evacua il resto prima di completare (nessun byte
/// perso). I fallimenti arrivano su <see cref="Failed"/> senza silenzi: l'ID esplicito mancante è un errore,
/// nessun cambio microfono implicito.
/// </summary>
public interface IAudioCapture : IAsyncDisposable
{
    event EventHandler? Ready;

    event EventHandler<ReadOnlyMemory<byte>>? ChunkReady;

    event EventHandler<double>? LevelChanged;

    event EventHandler<CaptureFailure>? Failed;

    Task StartAsync(string? deviceId, CancellationToken ct);

    Task<AudioSummary> StopAsync(CancellationToken ct);
}

/// <summary>Enumerazione dei dispositivi di input: sempre su worker, mai dal thread UI.</summary>
public interface IAudioDeviceProvider
{
    Task<IReadOnlyList<AudioDeviceInfo>> ListAsync(CancellationToken ct);
}

/// <summary>
/// Sorgente audio già convertita a PCM16/16 kHz mono (le chiamate audio di NAudio restano dentro l'adapter).
/// <see cref="DeviceLost"/> segnala la scomparsa del dispositivo a cattura in corso.
/// </summary>
public interface IMicrophoneSource : IAsyncDisposable
{
    event EventHandler<ReadOnlyMemory<byte>>? AudioArrived;

    event EventHandler? DeviceLost;

    Task StartAsync(CancellationToken ct);

    Task StopAsync(CancellationToken ct);
}

public interface IMicrophoneSourceFactory
{
    IMicrophoneSource Create(AudioDeviceInfo device);
}
