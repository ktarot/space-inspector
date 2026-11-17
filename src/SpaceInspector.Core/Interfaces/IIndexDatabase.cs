using SpaceInspector.Core.Models;

namespace SpaceInspector.Core.Interfaces;

public interface IIndexDatabase
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IndexSummary> GetSummaryAsync(CancellationToken cancellationToken = default);
}
