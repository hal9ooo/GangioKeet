using System.Collections.Generic;
using System.Linq;
using GangioKeet.Client.Audio;

namespace GangioKeet.Client.Ui;

/// <summary>Policy pura del sottomenu microfono: selezione e ordinamento.</summary>
public static class MicrophoneMenuPolicy
{
    public static bool IsDefaultSelected(string? deviceId) => string.IsNullOrEmpty(deviceId);

    public static bool IsDeviceSelected(string? configuredId, string deviceId) =>
        !string.IsNullOrEmpty(configuredId)
        && string.Equals(configuredId, deviceId, StringComparison.Ordinal);

    public static IReadOnlyList<AudioDeviceInfo> Order(IEnumerable<AudioDeviceInfo> devices) =>
        devices
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
}
