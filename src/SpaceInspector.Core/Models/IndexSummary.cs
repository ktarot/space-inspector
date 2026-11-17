namespace SpaceInspector.Core.Models;

public sealed record IndexSummary(long FileCount, DateTimeOffset? LastScanUtc);
