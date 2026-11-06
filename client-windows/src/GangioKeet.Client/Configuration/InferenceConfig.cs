using YamlDotNet.Serialization;

namespace GangioKeet.Client.Configuration;

public sealed class InferenceConfig
{
    [YamlMember(Alias = "model_selection_path")]
    public string ModelSelectionPath { get; set; } = "models/selected.json";
    [YamlMember(Alias = "cleanup_policy_path")]
    public string CleanupPolicyPath { get; set; } = "cleanup.yaml";
    [YamlMember(Alias = "models_root")]
// Cartella dei pesi. Se impostata si usa questa e non <install>/models: cosi' non serve
// un collegamento (junction) dentro la cartella di installazione.
public string? ModelsRoot { get; set; }
[YamlMember(Alias = "backend")]
    public string Backend { get; set; } = "vulkan";
}
