namespace GangioKeet.Inference;

public sealed record ModelFile(string Path, string Sha256, string? RemotePath = null);

public sealed record ModelArtifact(string Id, string Family, string SourceCheckpoint,
    string Revision, IReadOnlyList<ModelFile> Files, string Precision, string License,
    string RuntimeId, string? ArtifactRepository = null, string? Status = null,
    string? DisplayName = null);
