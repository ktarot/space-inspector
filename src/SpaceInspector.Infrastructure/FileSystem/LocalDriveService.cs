using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;

namespace SpaceInspector.Infrastructure.FileSystem;

public sealed class LocalDriveService : IDriveService
{
    public Task<IReadOnlyList<DriveSnapshot>> GetLocalDrivesAsync(CancellationToken cancellationToken = default)
        => Task.Run<IReadOnlyList<DriveSnapshot>>(() =>
        {
            var drives = new List<DriveSnapshot>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Do not query capacity or volume labels on mapped network drives.
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                try
                {
                    drives.Add(drive.IsReady
                        ? new(drive.Name, drive.VolumeLabel, drive.DriveType.ToString(),
                            drive.TotalSize, drive.AvailableFreeSpace, true, null)
                        : new(drive.Name, "", drive.DriveType.ToString(), null, null, false, "장치 연결 확인 필요"));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    drives.Add(new(drive.Name, "", drive.DriveType.ToString(), null, null, false, "드라이브 정보를 읽을 수 없습니다."));
                }
            }
            return drives;
        }, cancellationToken);
}
