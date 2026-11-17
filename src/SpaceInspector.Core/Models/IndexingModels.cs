namespace SpaceInspector.Core.Models;

public sealed record FileMetadata(string FullPath, string FileName, string Extension,
    string ParentDirectory, string Drive, long FileSize, DateTimeOffset CreationTimeUtc,
    DateTimeOffset ModifiedTimeUtc, int Attributes, bool? IsCloudPlaceholder);

public sealed record ScanIssue(string Path, string Status, string Message);
public sealed record ScanObservation(FileMetadata? File, ScanIssue? Issue);
public sealed record ScanProgress(long Files, long Errors, long Skipped, string CurrentPath);
public sealed record ScanResult(long ScanId, string Status, long Files, long Errors, long Skipped);
public sealed record IndexedFile(long FileId, string FileName, string FullPath, long? FileSize,
    string? ModifiedTimeUtc, string ScanStatus, bool? IsCloudPlaceholder)
{
    public string StatusText => ScanStatus switch
    {
        "Normal" => "정상", "Missing" => "원본 파일 없음", "AccessDenied" => "접근 거부",
        "DeviceUnavailable" => "경로/장치 접근 불가", "NeedsRescan" => "재검사 필요", _ => "확인 불가"
    };
    public string CloudText => IsCloudPlaceholder == true ? "오프라인/회수 속성 있음" : "미검증";
}
public sealed record IndexPage(IReadOnlyList<IndexedFile> Files, long TotalCount, int PageNumber, int PageSize);
