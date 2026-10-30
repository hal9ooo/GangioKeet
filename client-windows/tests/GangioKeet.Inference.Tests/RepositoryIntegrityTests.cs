using Xunit;

namespace GangioKeet.Inference.Tests;

public sealed class RepositoryIntegrityTests
{
    [Fact]
    public async Task CheckedInIntegrityMatchesRepositoryFiles()
    {
        string root = FindRepositoryRoot();
        string client = Path.Combine(root, "client-windows");
        string models = Path.Combine(client, "models");
        IntegrityManifest manifest = IntegrityManifestFile.Load(Path.Combine(models, "integrity.json"));
        await IntegrityManifestFile.VerifyAsync(manifest,
            Path.Combine(models, "candidates.json"),
            Path.Combine(client, "native", "runtime-lock.json"),
            Path.Combine(client, "cleanup.example.yaml"),
            default);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "client-windows", "models", "candidates.json")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found from the test output directory.");
    }
}
