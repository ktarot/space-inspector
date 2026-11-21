namespace SpaceInspector.Core.Models;

public enum FileSort { IndexedOrder, Name, Size, Modified, Created, Path }

public sealed record FileSearch
{
    public string? Name { get; init; }
    public IReadOnlyList<string> DuplicateStatuses { get; init; } = [];
    public string? DuplicateGroupId { get; init; }
    // Null means no path filter; an empty list deliberately matches no files.
    public IReadOnlyList<string>? IncludedPaths { get; init; }

    public IReadOnlyList<string> Extensions { get; init; } = [];
    public IReadOnlyList<string> Roots { get; init; } = [];
    public long? MinimumBytes { get; init; }
    public long? MaximumBytes { get; init; }
    public DateTimeOffset? CreatedFrom { get; init; }
    public DateTimeOffset? CreatedBefore { get; init; }
    public DateTimeOffset? ModifiedFrom { get; init; }
    public DateTimeOffset? ModifiedBefore { get; init; }
    public FileSort Sort { get; init; }
    public bool Descending { get; init; }

    public void Validate()
    {
        if (MinimumBytes < 0 || MaximumBytes < 0 || MinimumBytes > MaximumBytes)
            throw new ArgumentException("크기 범위를 확인하세요.");
        if (CreatedFrom >= CreatedBefore || ModifiedFrom >= ModifiedBefore)
            throw new ArgumentException("날짜 범위를 확인하세요.");
        if (Extensions.Count > 100 || Roots.Count > 100) throw new ArgumentException("확장자와 검색 범위는 각각 100개까지 지정할 수 있습니다.");
    }
}

public sealed record ScanHistory(long ScanId, string Status, string StartedUtc, string? FinishedUtc,
    long Files, long Errors, long Skipped);
