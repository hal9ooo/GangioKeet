namespace GangioKeet.Inference.Cleaning;

/// <summary>
/// Built-in deterministic cleaner. It has no model files and no runtime: the
/// synthetic artifact only lets it participate in the existing cleaner picker
/// and hot-swap path.
/// </summary>
public sealed class LightTextCleaner : ITextCleaner
{
    public const string Id = "light_cleanup";

    public static ModelArtifact Artifact { get; } = new(
        Id, "deterministic", "local", new string('0', 40), [],
        "rules", "MIT", "deterministic", DisplayName: "Pulizia leggera");

    private static readonly EngineIdentity Identity = new(
        Id, Artifact.Revision, "", "rules-v1", "cpu", "rules", "local");

    public Task LoadAsync(CancellationToken ct) => Task.CompletedTask;

    public Task<CleanedText> CleanAsync(string raw, string profile, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new CleanedText(LightCleanup.Apply(raw), Identity));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
