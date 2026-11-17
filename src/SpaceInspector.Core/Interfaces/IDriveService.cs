using SpaceInspector.Core.Models;

namespace SpaceInspector.Core.Interfaces;

public interface IDriveService
{
    Task<IReadOnlyList<DriveSnapshot>> GetLocalDrivesAsync(CancellationToken cancellationToken = default);
}
