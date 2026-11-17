using SpaceInspector.Core.Models;

namespace SpaceInspector.Core.Interfaces;

public interface IFileSearchRepository
{
    Task<IndexPage> SearchAsync(FileSearch search, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
    Task<ScanHistory?> GetLatestScanAsync(CancellationToken cancellationToken = default);
}
