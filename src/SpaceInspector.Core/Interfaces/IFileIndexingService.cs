using SpaceInspector.Core.Models;

namespace SpaceInspector.Core.Interfaces;

public interface IFileIndexingService
{
    Task<ScanResult> ScanAsync(IEnumerable<string> roots, IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
