using System.IO;
using YamlDotNet.Serialization;

namespace GangioKeet.Client.Configuration;

public sealed class RecordingConfig
{
    [YamlMember(Alias = "enabled")]
    public bool Enabled { get; set; }
    [YamlMember(Alias = "directory")]
    public string Directory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GangioKeet", "recordings");
    // Legacy compatibility: accepted but never used. Selected text is never
    // stored, regardless of this value.
    [YamlMember(Alias = "store_context")]
    public bool StoreContext { get; set; }
}
