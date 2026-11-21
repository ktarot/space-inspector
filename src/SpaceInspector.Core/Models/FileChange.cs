namespace SpaceInspector.Core.Models;

public sealed record FileChangePlan(Guid Id, DuplicateCandidate Source, string TargetPath, bool RemainsInScope, string? ResultFile, bool WasKept)
{
    public Guid? UndoOf { get; init; }
    public string? ExpectedSourceHash { get; init; }
    public string? TargetVolumeIdentifier { get; init; }
    public string? TargetPhysicalId { get; init; }
    public string? TargetModifiedUtc { get; init; }
    public string? TargetCreatedUtc { get; init; }
    public int? TargetAttributes { get; init; }
    public string? ContentHash { get; init; }
    public string TargetVolume => TargetVolumeIdentifier ?? Source.VolumeIdentifier!;
    public bool IsCrossVolume => TargetVolume != Source.VolumeIdentifier;
    public DuplicateCandidate Destination => Source with { FullPath = TargetPath, VolumeIdentifier = TargetVolume,
        PhysicalId = IsCrossVolume ? TargetPhysicalId : Source.PhysicalId, ModifiedUtc = TargetModifiedUtc ?? Source.ModifiedUtc };
    public string TemporaryPath => Path.Combine(Path.GetDirectoryName(TargetPath)!, ".space-inspector-" + Id.ToString("N") + ".partial");
}
public sealed record FileChangeRecord(FileChangePlan Plan, string State, DateTimeOffset UpdatedUtc);
public sealed record FileChangeRequest(IndexedFile File, string TargetPath);
public sealed record FileChangeOutcome(FileChangePlan Plan, string State, string? Error = null);
public sealed record FileChangeProgress(string Path, string Stage, long Bytes, long TotalBytes);
