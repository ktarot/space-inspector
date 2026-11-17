namespace SpaceInspector.Core.Models;

public enum DuplicateLevel { Size, PartialHash, FullHash }
public sealed record DuplicateOptions(DuplicateLevel Level = DuplicateLevel.FullHash, bool IncludeProtected = false);
public sealed record DuplicateCandidate(long FileId, string FullPath, long Size, string ModifiedUtc,
    string? VolumeIdentifier, string? PhysicalId = null);
public sealed record FileFingerprint(string Status, string? Hash = null, string? PhysicalId = null, string? Error = null);
public sealed record DuplicateProgress(string Stage, long Processed, long Unverified);
public sealed record DuplicateResult(long RunId, string Status, long Processed, long Unverified);
public sealed record DuplicateGroup(string GroupId, string Status, long FileCount, long DataCount, long LogicalDuplicateBytes)
{
    public string StatusText => Status switch { "ConfirmedDuplicate" => "SHA-256 확인 (검사 시점)", "StrongCandidate" => "부분 해시 후보", "Unverified" => "미검증 · 사유 확인", "Pending" => "미처리", _ => "크기 일치 후보" };
    public string SpaceText => Status == "ConfirmedDuplicate" ? $"{LogicalDuplicateBytes:N0} 바이트 (논리적 추정)" : "미검증";
    public string DataCountText => Status == "ConfirmedDuplicate" ? DataCount.ToString("N0") : "미검증";
}
public sealed record DuplicateMember(long FileId, string FullPath, long FileSize, string Status, string? Error, string? VolumeIdentifier = null)
{
    public string FileName => Path.GetFileName(FullPath);
}
