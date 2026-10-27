using YamlDotNet.Serialization;

namespace GangioKeet.Client.Configuration;

public sealed class InferenceConfig
{
    [YamlMember(Alias = "mode")]
    public string Mode { get; set; } = "local";
    [YamlMember(Alias = "model_selection_path")]
    public string ModelSelectionPath { get; set; } = "models/selected.json";
    [YamlMember(Alias = "cleanup_policy_path")]
    public string CleanupPolicyPath { get; set; } = "cleanup.yaml";
    [YamlMember(Alias = "backend")]
    public string Backend { get; set; } = "vulkan";
}
