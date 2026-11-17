using System.Diagnostics;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;

namespace SpaceInspector.Core.DuplicateDetection;

public sealed class DuplicateAnalysisService(IDuplicateRepository repository, IFileFingerprintService fingerprints) : IDuplicateAnalysisService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public Task<DuplicateResult> AnalyzeAsync(DuplicateOptions options, IProgress<DuplicateProgress>? progress, CancellationToken cancellationToken)
        => Task.Run(() => AnalyzeCoreAsync(options, progress, cancellationToken), CancellationToken.None);

    private async Task<DuplicateResult> AnalyzeCoreAsync(DuplicateOptions options, IProgress<DuplicateProgress>? progress, CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken)) throw new InvalidOperationException("중복 분석이 이미 진행 중입니다.");
        try
        {
            long id;
            try { id = await repository.BeginDuplicatesAsync(cancellationToken); }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            { throw new OperationCanceledException("중복 분석 준비를 취소했습니다.", ex, cancellationToken); }
            long processed = 0, unverified = 0;
            var watch = Stopwatch.StartNew();
            try
            {
                for (var pass = 0; pass <= (options.Level == DuplicateLevel.FullHash ? 1 : 0); pass++)
                {
                    long after = 0;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var page = await repository.GetDuplicateCandidatesAsync(after, 100, pass == 1, cancellationToken);
                        if (page.Count == 0) break;
                        foreach (var file in page)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (watch.ElapsedMilliseconds >= 150 || processed == 0)
                            {
                                progress?.Report(new((pass == 1 ? "전체 SHA-256" : "크기/부분 해시") + " · " + Path.GetFileName(file.FullPath), processed, unverified));
                                watch.Restart();
                            }
                            var level = pass == 1 ? DuplicateLevel.FullHash : options.Level == DuplicateLevel.Size ? DuplicateLevel.Size : DuplicateLevel.PartialHash;
                            var fingerprint = await fingerprints.ComputeAsync(file, level, options.IncludeProtected, cancellationToken);
                            await repository.StoreFingerprintAsync(id, file, fingerprint, pass == 1, cancellationToken);
                            processed++;
                            if (fingerprint.Status == "Unverified") unverified++;
                            after = file.FileId;
                            if (watch.ElapsedMilliseconds >= 150)
                            {
                                progress?.Report(new(pass == 1 ? "전체 SHA-256" : "크기/부분 해시", processed, unverified));
                                watch.Restart();
                            }
                        }
                    }
                }
                // Revalidate every fully hashed path before publishing confirmed groups.
                long last = 0;
                while (true)
                {
                    var page = await repository.GetHashedFilesAsync(id, last, cancellationToken);
                    if (page.Count == 0) break;
                    foreach (var file in page)
                    {
                        if (!await fingerprints.IsCurrentAsync(file, cancellationToken))
                        {
                            await repository.StoreFingerprintAsync(id, file, new("Unverified", Error: "검사 중 파일 변경 또는 장치 변경 감지"), true, cancellationToken);
                            unverified++;
                        }
                        last = file.FileId;
                    }
                }
                var result = new DuplicateResult(id, "Completed", processed, unverified);
                await repository.FinishDuplicatesAsync(result, options.Level, cancellationToken);
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                var result = new DuplicateResult(id, "Cancelled", processed, unverified);
                await repository.FinishDuplicatesAsync(result, options.Level, CancellationToken.None);
                return result;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                var result = new DuplicateResult(id, "Cancelled", processed, unverified);
                await repository.FinishDuplicatesAsync(result, options.Level, CancellationToken.None);
                return result;
            }
            catch
            {
                await repository.FinishDuplicatesAsync(new(id, "Failed", processed, unverified), options.Level, CancellationToken.None);
                throw;
            }
        }
        finally { _gate.Release(); }
    }
}
