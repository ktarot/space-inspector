using SpaceInspector.Core.Models;

namespace SpaceInspector.Core.Interfaces;

public interface IScanRepository
{
    Task<long> BeginScanAsync(IReadOnlyList<string> roots, CancellationToken cancellationToken);
    Task AppendAsync(long scanId, IReadOnlyList<ScanObservation> batch, CancellationToken cancellationToken);
    Task FinishScanAsync(ScanResult result, CancellationToken cancellationToken);
    Task<IndexPage> GetPageAsync(int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ScanIssue>> GetIssuesAsync(long scanId, int limit = 100, CancellationToken cancellationToken = default);
}
