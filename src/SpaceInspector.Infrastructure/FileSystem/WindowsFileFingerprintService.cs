using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SpaceInspector.Core.Indexing;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;

namespace SpaceInspector.Infrastructure.FileSystem;

public sealed class WindowsFileFingerprintService(IVolumeIdentityService volumes) : IFileFingerprintService
{
    private const uint Flags = 0x00200000 | 0x00100000 | 0x08000000; // OPEN_REPARSE_POINT, OPEN_NO_RECALL, SEQUENTIAL_SCAN
    public async Task<FileFingerprint> ComputeAsync(DuplicateCandidate file, DuplicateLevel level, bool includeProtected, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckPath(file, includeProtected);
            if (level == DuplicateLevel.Size) return new("SuspectedDuplicate");
            using var handle = OpenFile(file.FullPath, 0x80000000);
            var before = ValidateHandle(handle, file);
            using var stream = new FileStream(handle, FileAccess.Read, 65536, false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            async Task Read(long length)
            {
                while (length > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(length, buffer.Length)), cancellationToken);
                    if (read == 0) throw new IOException("검사 중 파일 크기가 변경되었습니다.");
                    hash.AppendData(buffer, 0, read); length -= read;
                }
            }
            if (level == DuplicateLevel.FullHash) await Read(file.Size);
            else
            {
                await Read(Math.Min(file.Size, buffer.Length));
                if (file.Size > buffer.Length) { stream.Position = Math.Max(buffer.Length, file.Size - buffer.Length); await Read(file.Size - stream.Position); }
            }
            var after = ValidateHandle(handle, file);
            if (after != before || volumes.GetIdentity(file.FullPath) != file.VolumeIdentifier)
                throw new IOException("검사 중 파일 또는 장치가 변경되었습니다.");
            return new("Verified", Convert.ToHexString(hash.GetHashAndReset()), after);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException)
        {
            return new("Unverified", Error: ex.Message);
        }
    }

    public Task<bool> IsCurrentAsync(DuplicateCandidate file, CancellationToken cancellationToken) => Task.Run(() =>
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckPath(file, true);
            using var handle = OpenFile(file.FullPath, 0);
            return ValidateHandle(handle, file) == file.PhysicalId;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException) { return false; }
    }, cancellationToken);

    internal static bool ProtectedPath(string path)
    {
        var folders = new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData,
            Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData };
        return folders.Select(Environment.GetFolderPath).Where(p => !string.IsNullOrEmpty(p)).Any(p => ScanScope.Contains(p, path));
    }

    private void CheckPath(DuplicateCandidate file, bool includeProtected)
    {
        if (!OperatingSystem.IsWindows()) throw new IOException("Windows 파일 시스템 검증이 필요합니다.");
        ScanScope.Normalize(file.FullPath);
        if (file.VolumeIdentifier is null || volumes.GetIdentity(file.FullPath) != file.VolumeIdentifier)
            throw new IOException("장치 식별이 없거나 현재 장치와 다릅니다. 해당 범위를 재검사하세요.");
        var ancestors = new Stack<string>();
        for (var parent = Directory.GetParent(file.FullPath); parent is not null; parent = parent.Parent) ancestors.Push(parent.FullName);
        foreach (var path in ancestors)
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || MetadataOnlyFileSource.IsCloudOnly(attributes))
                throw new IOException("링크 또는 클라우드 폴더의 콘텐츠는 열지 않습니다.");
        }
        var attrs = File.GetAttributes(file.FullPath);
        if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0 || MetadataOnlyFileSource.IsCloudOnly(attrs))
            throw new IOException("링크 또는 온라인 전용/회수 속성 파일은 미검증으로 유지합니다.");
        if (!includeProtected && ((attrs & FileAttributes.System) != 0 || ProtectedPath(file.FullPath)))
            throw new IOException("시스템·프로그램 보호 범위는 기본적으로 제외합니다.");
    }

    private static SafeFileHandle OpenFile(string path, uint access)
    {
        var full = Path.GetFullPath(path);
        var handle = CreateFile(@"\\?\" + full, access, 1, IntPtr.Zero, 3, Flags, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }

    private static string ValidateHandle(SafeFileHandle handle, DuplicateCandidate file)
    {
        if (!GetFileInformationByHandle(handle, out var info) || !GetFileInformationByHandleEx(handle, 18, out var id, 24))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var volumeParts = file.VolumeIdentifier?.Split('|');
        if (volumeParts is not { Length: 3 } || !uint.TryParse(volumeParts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var serial)
            || serial != info.VolumeSerial) throw new IOException("열린 파일의 볼륨 식별자가 다릅니다.");
        var finalPath = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, finalPath, finalPath.Capacity, 1); // VOLUME_NAME_GUID
        var expectedPath = volumeParts[0] + "\\" + Path.GetRelativePath(Path.GetPathRoot(file.FullPath)!, file.FullPath);
        if (length == 0 || length >= finalPath.Capacity || !string.Equals(finalPath.ToString(), expectedPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("열린 파일의 최종 경로가 인덱스와 다릅니다.");
        if ((info.Attributes & (0x400 | 0x10 | 0x1000 | 0x40000 | 0x400000)) != 0) throw new IOException("링크/클라우드 파일은 검사하지 않습니다.");
        var size = ((long)info.SizeHigh << 32) | info.SizeLow;
        var modified = DateTimeOffset.FromFileTime(((long)info.WriteHigh << 32) | info.WriteLow);
        if (!DateTimeOffset.TryParse(file.ModifiedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expected)
            || modified != expected || size != file.Size) throw new IOException("인덱스 이후 파일이 변경되었습니다. 재검사하세요.");
        var physical = $"{file.VolumeIdentifier}|{id.Low:X16}{id.High:X16}";
        if (file.PhysicalId is not null && file.PhysicalId != physical) throw new IOException("파일 식별자가 변경되었습니다.");
        return physical;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
            VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileIdInfo { public ulong Volume, Low, High; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out FileIdInfo info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int length, uint flags);
}
