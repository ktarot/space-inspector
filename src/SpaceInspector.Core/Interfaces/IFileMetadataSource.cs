using SpaceInspector.Core.Models;

namespace SpaceInspector.Core.Interfaces;

public interface IFileMetadataSource
{
    IEnumerable<ScanObservation> Enumerate(IReadOnlyList<string> roots, CancellationToken cancellationToken);
}
