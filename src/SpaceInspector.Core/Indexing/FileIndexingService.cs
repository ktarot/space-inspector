using System.Diagnostics;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;

namespace SpaceInspector.Core.Indexing;

public sealed class FileIndexingService(IFileMetadataSource source, IScanRepository repository, bool recursive = true) : IFileIndexingService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<ScanResult> ScanAsync(IEnumerable<string> roots, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = recursive ? ScanScope.NormalizeRoots(roots) : roots.Select(ScanScope.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (!await _gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("이미 검사 중입니다.");
        try
        {
            return await Task.Run(async () =>
            {
                var scanId = await repository.BeginScanAsync(normalized, cancellationToken);
                long files = 0, errors = 0, skipped = 0;
                var batch = new List<ScanObservation>(256);
                var status = "Completed";
                var current = "";
                var clock = Stopwatch.StartNew();
                try
                {
                    foreach (var item in source.Enumerate(normalized, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        batch.Add(item);
                        if (item.File is { } file) { files++; current = file.FullPath; }
                        if (item.Issue is { } issue)
                        {
                            current = issue.Path;
                            if (issue.Status.StartsWith("Skipped", StringComparison.Ordinal)) skipped++;
                            else errors++;
                        }
                        if (batch.Count == 256)
                        {
                            await repository.AppendAsync(scanId, batch, cancellationToken);
                            batch.Clear();
                        }
                        if (clock.ElapsedMilliseconds >= 150)
                        {
                            progress?.Report(new(files, errors, skipped, current));
                            clock.Restart();
                        }
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    status = errors > 0 || skipped > 0 ? "CompletedWithIssues" : "Completed";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { status = "Cancelled"; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors++;
                    status = "Failed";
                    batch.Add(new(null, new(current, "Failed", ex.Message)));
                }
                catch
                {
                    await repository.FinishScanAsync(new(scanId, "Failed", files, errors, skipped), CancellationToken.None);
                    throw;
                }
                // Preserve already observed metadata on cancellation. Never reconcile missing files then.
                try
                {
                    if (batch.Count > 0) await repository.AppendAsync(scanId, batch, CancellationToken.None);
                    var result = new ScanResult(scanId, status, files, errors, skipped);
                    await repository.FinishScanAsync(result, CancellationToken.None);
                    progress?.Report(new(files, errors, skipped, current));
                    return result;
                }
                catch
                {
                    await repository.FinishScanAsync(new(scanId, "Failed", files, errors, skipped), CancellationToken.None);
                    throw;
                }
            }, CancellationToken.None);
        }
        finally { _gate.Release(); }
    }
}
