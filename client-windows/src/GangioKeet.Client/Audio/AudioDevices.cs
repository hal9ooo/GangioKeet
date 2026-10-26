using NAudio.CoreAudioApi;

namespace GangioKeet.Client.Audio;

/// <summary>
/// Enumerazione WASAPI dei dispositivi di input attivi (sempre su worker). Un sistema senza
/// endpoint attivi o con MMDevice non interrogabile restituisce una lista vuota: la policy della
/// cattura la traduce nel fallimento <c>no_input_device</c> invece di nascondere il problema.
/// </summary>
public sealed class AudioDevices : IAudioDeviceProvider
{
    public Task<IReadOnlyList<AudioDeviceInfo>> ListAsync(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            MMDevice? defaultCapture = null;
            try
            {
                using MMDeviceEnumerator enumerator = new();
                try
                {
                    defaultCapture = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                }
                catch (Exception ex) when (ex is NAudio.MmException or ArgumentException)
                {
                    // Nessun default impostato (tipicamente zero dispositivi): nessuno è default.
                    defaultCapture = null;
                }

                MMDeviceCollection endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                List<AudioDeviceInfo> devices = new(endpoints.Count);
                foreach (MMDevice endpoint in endpoints)
                {
                    devices.Add(new AudioDeviceInfo(
                        endpoint.ID,
                        endpoint.FriendlyName,
                        defaultCapture != null && string.Equals(endpoint.ID, defaultCapture.ID, StringComparison.Ordinal)));
                }

                return (IReadOnlyList<AudioDeviceInfo>)devices;
            }
            catch (NAudio.MmException)
            {
                // Errore WASAPI senza endpoint attivi interrogabili: la policy di cattura lo segnala
                // come no_input_device, diagnosi corretta e non un silenzio sul dispositivo mancante.
                return (IReadOnlyList<AudioDeviceInfo>)[];
            }
            finally
            {
                defaultCapture?.Dispose();
            }
        }, ct);
    }
}
