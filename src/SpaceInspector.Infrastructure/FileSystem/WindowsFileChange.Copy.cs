using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;
namespace SpaceInspector.Infrastructure.FileSystem;

internal static partial class WindowsFileChange
{
    // Source and destination directory handles prohibit replacement throughout the operation.
    // Cancellation is accepted until publish; after publish the short commit finishes atomically
    // from the application's point of view. Recovery never deletes a surviving original.
    internal static FileChangePlan CopyAndMove(FileChangePlan plan, IVolumeIdentityService volumes, string jobDirectory,
        Action<FileChangeRecord> journal, IProgress<FileChangeProgress>? progress, CancellationToken token)
    {
        using var locked = new LockedChange(plan.Source, plan.TargetPath, volumes, jobDirectory, plan.TargetVolume, true);
        CheckCopyable(plan.Source.FullPath);
        token.ThrowIfCancellationRequested();
        void Save(string state) => journal(new(plan, state, DateTimeOffset.UtcNow));
        Save("Copying");
        using var target = CreateFile(@"\\?\" + plan.TemporaryPath, 0xC0010180, 1, IntPtr.Zero, 1, 0x00200000 | 0x00100000, IntPtr.Zero);
        if (target.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var published = false;
        try
        {
            var initial = plan.Destination with { FullPath = plan.TemporaryPath, Size = 0,
                ModifiedUtc = File.GetLastWriteTimeUtc(target).ToString("O"), PhysicalId = null };
            plan = plan with { TargetPhysicalId = WindowsFileFingerprintService.ValidateHandle(target, initial) };
            Save("Copying");
            using var sourceStream = new FileStream(new SafeFileHandle(locked.Source.DangerousGetHandle(), false), FileAccess.Read, 1024 * 1024, false);
            using var targetStream = new FileStream(new SafeFileHandle(target.DangerousGetHandle(), false), FileAccess.ReadWrite, 1024 * 1024, false);
            plan = plan with { ContentHash = CopyAndVerify(sourceStream, targetStream, plan.Source.Size,
                (stage, bytes) => progress?.Report(new(plan.Source.FullPath, stage, bytes, plan.Source.Size)), token) };
            if (plan.ExpectedSourceHash is not null && plan.ContentHash != plan.ExpectedSourceHash)
                throw new IOException("이동 이후 내용이 변경되어 되돌리지 않았습니다. 현재 파일을 보존했습니다.");
            WindowsFileFingerprintService.ValidateHandle(locked.Source, plan.Source);
            var basicAttributes = File.GetAttributes(locked.Source) & (FileAttributes.Hidden | FileAttributes.Archive | FileAttributes.NotContentIndexed);
            File.SetAttributes(target, basicAttributes == 0 ? FileAttributes.Normal : basicAttributes);
            File.SetCreationTimeUtc(target, File.GetCreationTimeUtc(locked.Source));
            File.SetLastWriteTimeUtc(target, DateTimeOffset.Parse(plan.Source.ModifiedUtc).UtcDateTime);
            targetStream.Flush(true);
            plan = plan with { TargetModifiedUtc = File.GetLastWriteTimeUtc(target).ToString("O"),
                TargetCreatedUtc = File.GetCreationTimeUtc(target).ToString("O"), TargetAttributes = (int)File.GetAttributes(target) };
            WindowsFileFingerprintService.ValidateHandle(target, plan.Destination with { FullPath = plan.TemporaryPath });
            Save("Verified");
            token.ThrowIfCancellationRequested();
            if (volumes.GetIdentity(plan.Source.FullPath) != plan.Source.VolumeIdentifier || volumes.GetIdentity(plan.TargetPath) != plan.TargetVolume)
                throw new IOException("이동 중 장치가 변경되었습니다. 원본을 보존했습니다.");
            RenameHandle(target, plan.TargetPath); published = true;
            Save("Published"); // Persist verified target identity before removing the original.
            WindowsFileFingerprintService.ValidateHandle(target, plan.Destination);
            WindowsFileFingerprintService.ValidateHandle(locked.Source, plan.Source);
            CheckCopyable(plan.Source.FullPath);
            DeleteHandle(locked.Source);
            sourceStream.Dispose(); locked.Source.Dispose(); // Complete delete disposition before database reconciliation.
            return plan;
        }
        catch
        {
            if (!published && !target.IsClosed)
            {
                try { DeleteHandle(target); target.Dispose(); Save("NotMoved"); }
                catch { /* A surviving partial is kept in the journal for explicit inspection. */ }
            }
            throw;
        }
    }

    internal static string CopyAndVerify(Stream source, Stream target, long size, Action<string, long>? report, CancellationToken token)
    {
        var buffer = new byte[1024 * 1024];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long total = 0; var watch = System.Diagnostics.Stopwatch.StartNew();
        report?.Invoke("복사", 0);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = source.Read(buffer, 0, buffer.Length); if (count == 0) break;
            target.Write(buffer, 0, count); hash.AppendData(buffer, 0, count); total += count;
            if (watch.ElapsedMilliseconds >= 150) { report?.Invoke("복사", total); watch.Restart(); }
        }
        if (total != size) throw new IOException("원본 크기가 변경되었습니다.");
        if (target is FileStream file) file.Flush(true); else target.Flush();
        var expected = hash.GetHashAndReset(); target.Position = 0; total = 0; report?.Invoke("복사본 SHA-256 검증", 0);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = target.Read(buffer, 0, buffer.Length); if (count == 0) break;
            hash.AppendData(buffer, 0, count); total += count;
            if (watch.ElapsedMilliseconds >= 150) { report?.Invoke("복사본 SHA-256 검증", total); watch.Restart(); }
        }
        if (total != size || !CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset()))
            throw new IOException("복사본 검증에 실패했습니다. 원본을 보존했습니다.");
        report?.Invoke("복사본 검증 완료", total);
        return Convert.ToHexString(expected);
    }
    private static void DeleteHandle(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1);
            if (!SetFileInformationByHandle(handle, 4, buffer, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static void CheckCopyable(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.Encrypted) != 0)
            throw new IOException("암호화된 파일의 다른 볼륨 이동은 지원하지 않습니다. 원본을 보존합니다.");
        var search = FindFirstStream(@"\\?\" + path, 0, out var stream, 0);
        if (search == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 38 or 87) return; // No streams / filesystem does not support streams.
            throw new Win32Exception(error);
        }
        try
        {
            do
            {
                if (stream.Name != "::$DATA") throw new IOException("추가 데이터 스트림(다운로드 보안 정보 등)이 있는 파일은 다른 볼륨으로 이동하지 않습니다. 원본을 보존합니다.");
            } while (FindNextStream(search, out stream));
            if (Marshal.GetLastWin32Error() != 38) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { FindClose(search); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StreamData { public long Size; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name; }
    [DllImport("kernel32.dll", EntryPoint = "FindFirstStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstStream(string path, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", EntryPoint = "FindNextStreamW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextStream(IntPtr handle, out StreamData data);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr handle);
}
