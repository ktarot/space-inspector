namespace SpaceInspector.Core.Models;

public sealed record DriveSnapshot(
    string RootPath, string Label, string Kind, long? TotalBytes,
    long? AvailableBytes, bool IsReady, string? Error)
{
    public long? UsedBytes => TotalBytes is { } total && AvailableBytes is { } available
        ? Math.Max(0, total - available) : null;
    public double? UsedPercent => TotalBytes is > 0 && UsedBytes is { } used
        ? used * 100d / TotalBytes.Value : null;
    public string CapacityText => TotalBytes is { } total && AvailableBytes is { } free
        ? $"{free / 1073741824d:N1} GB 여유 / {total / 1073741824d:N1} GB"
        : Error ?? "사용할 수 없음";
}
