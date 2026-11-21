using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SpaceInspector.Core.Indexing;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;
namespace SpaceInspector.Infrastructure.FileSystem;

internal static partial class WindowsFileChange
{
    public static string NormalizeTarget(string path)
    {
        var target = ScanScope.Normalize(path);
        var name = Path.GetFileName(target);
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (name.Length is 0 or > 255 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' ')
            || new[] {"CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"}.Contains(stem)
            || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9'))
            throw new ArgumentException("사용할 수 없는 파일 이름입니다.");
        if (!string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("상대 경로, 상위 경로 표기 또는 끝의 공백은 사용할 수 없습니다.");
        return target;
    }
    public static string Inspect(DuplicateCandidate source, string target, IVolumeIdentityService volumes, string jobDirectory, string? targetVolume = null)
    {
        using var locked = new LockedChange(source, target, volumes, jobDirectory, targetVolume);
        if (targetVolume is not null && targetVolume != source.VolumeIdentifier) CheckCopyable(source.FullPath);
        return locked.PhysicalId;
    }
    public static void Move(DuplicateCandidate source, string target, IVolumeIdentityService volumes, string jobDirectory, Action beforeMove)
    {
        using var locked = new LockedChange(source, target, volumes, jobDirectory);
        beforeMove();
        RenameHandle(locked.Source, target);
        WindowsFileFingerprintService.ValidateHandle(locked.Source, source with { FullPath = target });
    }
    private static void RenameHandle(SafeFileHandle handle, string target)
    {
        var name = Encoding.Unicode.GetBytes(target + "\0");
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + 4;
        var buffer = Marshal.AllocHGlobal(nameOffset + name.Length);
        try
        {
            for (var i = 0; i < nameOffset; i++) Marshal.WriteByte(buffer, i, 0);
            // ReplaceIfExists=false; RootDirectory=NULL; absolute destination path.
            Marshal.WriteInt32(buffer, lengthOffset, name.Length - 2);
            Marshal.Copy(name, 0, IntPtr.Add(buffer, nameOffset), name.Length);
            if (!SetFileInformationByHandle(handle, 3, buffer, (uint)(nameOffset + name.Length)))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private sealed class LockedChange : IDisposable
    {
        private readonly List<SafeFileHandle> _directories = [];
        public SafeFileHandle Source { get; private set; } = null!;
        public string PhysicalId { get; }
        public LockedChange(DuplicateCandidate source, string target, IVolumeIdentityService volumes, string jobDirectory, string? targetVolume = null, bool readSource = false)
        {
            try
            {
                target = NormalizeTarget(target);
                targetVolume ??= source.VolumeIdentifier;
                if (string.Equals(source.FullPath, target, StringComparison.OrdinalIgnoreCase)) throw new IOException("현재 경로와 같습니다. 대소문자만 바꾸는 변경은 지원하지 않습니다.");
                if (source.VolumeIdentifier is null || volumes.GetIdentity(source.FullPath) != source.VolumeIdentifier
                    || volumes.GetIdentity(target) != targetVolume)
                    throw new IOException("원본 또는 대상 장치가 변경되었습니다. 변경을 다시 준비하세요.");
                CheckProtected(source.FullPath, jobDirectory); CheckProtected(target, jobDirectory);
                var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in new[] { source.FullPath, target })
                    for (var dir = Directory.GetParent(path); dir is not null; dir = dir.Parent) directories.Add(dir.FullName);
                foreach (var path in directories.OrderBy(p => p.Length))
                    LockDirectory(path, string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase) ? targetVolume! : source.VolumeIdentifier);
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException("같은 이름의 항목이 이미 있습니다. 덮어쓰지 않았습니다.");
                var attributes = File.GetAttributes(source.FullPath);
                if ((attributes & (FileAttributes.ReadOnly | FileAttributes.System | FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 || MetadataOnlyFileSource.IsCloudOnly(attributes))
                    throw new IOException("읽기 전용·시스템·링크·온라인 전용 파일은 변경하지 않습니다.");
                Source = WindowsFileFingerprintService.OpenFile(source.FullPath, 0x10000 | 0x80 | (readSource ? 0x80000000u : 0u)); // DELETE | FILE_READ_ATTRIBUTES
                PhysicalId = WindowsFileFingerprintService.ValidateHandle(Source, source);
            }
            catch { Dispose(); throw; }
        }
        private void LockDirectory(string path, string volume)
        {
            var handle = CreateFile(@"\\?\" + path, 0x80, 1, IntPtr.Zero, 3, 0x02000000 | 0x00200000 | 0x00100000, IntPtr.Zero);
            if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
            _directories.Add(handle);
            if (!GetFileInformationByHandleEx(handle, 9, out var info, 8)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if ((info.Attributes & 0x400) != 0 || MetadataOnlyFileSource.IsCloudOnly((FileAttributes)info.Attributes))
                throw new IOException("링크 또는 온라인 전용 폴더는 변경 경로로 사용할 수 없습니다.");
            var final = new StringBuilder(32768);
            var length = GetFinalPathNameByHandle(handle, final, final.Capacity, 1);
            var relative = Path.GetRelativePath(Path.GetPathRoot(path)!, path);
            var expected = volume.Split('|')[0] + (relative == "." ? "\\" : "\\" + relative);
            if (length == 0 || length >= final.Capacity || !string.Equals(final.ToString().TrimEnd('\\'), expected.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new IOException("폴더의 실제 경로가 선택한 경로와 다릅니다.");
        }
        public void Dispose() { Source?.Dispose(); foreach (var directory in _directories) directory.Dispose(); }
    }
    private static void CheckProtected(string path, string jobDirectory)
    {
        var forbidden = new[] { Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            jobDirectory, AppContext.BaseDirectory };
        if (forbidden.Where(p => !string.IsNullOrEmpty(p)).Any(root => ScanScope.Contains(root, path))
            || new[] { "pagefile.sys", "swapfile.sys", "hiberfil.sys", "DumpStack.log.tmp" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
            throw new IOException("시스템 또는 앱 데이터 경로는 변경할 수 없습니다.");
        for (var parent = Directory.GetParent(path); parent is not null; parent = parent.Parent)
            if (MetadataOnlyFileSource.IsProtectedSystemDirectory(parent.FullName)) throw new IOException("시스템 보호 폴더는 변경할 수 없습니다.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes, ReparseTag; }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, IntPtr info, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, int capacity, uint flags);
}
