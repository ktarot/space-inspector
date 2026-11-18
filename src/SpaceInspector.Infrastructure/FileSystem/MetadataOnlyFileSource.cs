using SpaceInspector.Core.Indexing;
using SpaceInspector.Core.Interfaces;
using SpaceInspector.Core.Models;

namespace SpaceInspector.Infrastructure.FileSystem;

public sealed class MetadataOnlyFileSource(IEnumerable<string>? excludedDirectories = null, IVolumeIdentityService? volumes = null,
    WorkSpecification? specification = null, Func<string, string?>? driveRoot = null) : IFileMetadataSource
{
    private readonly string[] _excluded = (excludedDirectories ?? []).Select(ScanScope.Normalize).ToArray();
    // Offline, RecallOnOpen, RecallOnDataAccess. Content is never opened, regardless of these flags.
    public static bool IsCloudOnly(FileAttributes attributes) => ((int)attributes & (0x1000 | 0x40000 | 0x400000)) != 0;

    public IEnumerable<ScanObservation> Enumerate(IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        foreach (var root in roots)
        {
            if (specification is not null && !specification.AllowsDirectory(root)) continue;
            var volume = volumes?.GetIdentity(root);
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScanIssue? issue = null;
                IEnumerator<FileSystemInfo>? entries = null;
                try
                {
                    if (specification is not null && specification.Volumes.TryGetValue(Path.GetPathRoot(root)!, out var expected) && expected != volume)
                        throw new IOException("선택 당시 장치와 다릅니다. 원래 장치를 연결하세요.");
                    if (volume is not null && volumes?.GetIdentity(root) != volume)
                        throw new IOException("검사 중 장치가 변경되거나 연결이 끊겼습니다.");
                    issue = CheckDirectory(directory);
                    if (issue is null)
                        entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", new EnumerationOptions
                        {
                            RecurseSubdirectories = false, IgnoreInaccessible = false,
                            AttributesToSkip = 0, ReturnSpecialDirectories = false
                        }).GetEnumerator();
                }
                catch (Exception ex) when (IsExpected(ex)) { issue = ToIssue(directory, ex); }
                if (issue is not null) { yield return new(null, issue); continue; }
                using (entries!)
                {
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        FileSystemInfo? entry = null;
                        issue = null;
                        try { if (entries!.MoveNext()) entry = entries.Current; }
                        catch (Exception ex) when (IsExpected(ex)) { issue = ToIssue(directory, ex); }
                        if (issue is not null) { yield return new(null, issue); break; }
                        if (entry is null) break;
                        FileMetadata? metadata = null;
                        try
                        {
                            var attributes = entry.Attributes;
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (specification?.Recursive == false) continue;
                                if ((attributes & FileAttributes.ReparsePoint) != 0 || IsCloudOnly(attributes))
                                    issue = new(entry.FullName, "SkippedDirectory", "디렉터리 링크 또는 클라우드 전용 폴더: 자동 접근하지 않음");
                                else pending.Push(entry.FullName);
                            }
                            else
                            {
                                if (specification is not null && !specification.AllowsFile(entry.FullName)) continue;
                                var file = (FileInfo)entry;
                                // Enumeration metadata only: no stream, hash, owner query or shell media handler.
                                metadata = new(file.FullName, file.Name, file.Extension.ToLowerInvariant(),
                                    file.DirectoryName!, Path.GetPathRoot(file.FullName)!, file.Length,
                                    file.CreationTimeUtc, file.LastWriteTimeUtc, (int)attributes, IsCloudOnly(attributes) ? true : null,
                                    volume, volume is null ? null : Path.GetRelativePath(Path.GetPathRoot(file.FullName)!, file.FullName));
                            }
                        }
                        catch (Exception ex) when (IsExpected(ex)) { issue = ToIssue(entry.FullName, ex); }
                        if (metadata is not null || issue is not null) yield return new(metadata, issue);
                    }
                }
            }
        }
    }

    private ScanIssue? CheckDirectory(string path)
    {
        if (_excluded.Any(root => ScanScope.Contains(root, path)))
            return new(path, "SkippedExcluded", "앱 데이터 또는 실행 폴더 제외");
        if (IsProtectedSystemDirectory(path, driveRoot?.Invoke(path) ?? Path.GetPathRoot(path)!))
            return new(path, "SkippedExcluded", "Windows 휴지통 또는 시스템 복원 폴더는 탐색 대상에서 제외");
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            return new(path, "Unavailable", "로컬 고정/이동식 드라이브만 검사할 수 있습니다.");
        // Include ancestors: explicitly selecting a child of a junction must not bypass the rule.
        var ancestors = new Stack<DirectoryInfo>();
        for (var item = new DirectoryInfo(path); item is not null; item = item.Parent) ancestors.Push(item);
        foreach (var item in ancestors)
        {
            var attributes = File.GetAttributes(item.FullName);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || IsCloudOnly(attributes))
                return new(path, "SkippedDirectory", "경로에 링크 또는 클라우드 전용 폴더가 있어 검사하지 않았습니다.");
            if ((attributes & FileAttributes.Directory) == 0) throw new DirectoryNotFoundException("폴더를 찾을 수 없습니다.");
        }
        return null;
    }

    public static bool IsProtectedSystemDirectory(string path)
    {
        var root = Path.GetPathRoot(path);
        return !string.IsNullOrEmpty(root) && IsProtectedSystemDirectory(path, root);
    }

    public static bool IsProtectedSystemDirectory(string path, string driveRoot)
    {
        if (string.IsNullOrWhiteSpace(driveRoot)) return false;
        var root = Path.GetFullPath(driveRoot);
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        if (relative == "." || Path.IsPathRooted(relative) || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return false;
        var first = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.Equals(first, "$RECYCLE.BIN", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, "System Volume Information", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;
    private static ScanIssue ToIssue(string path, Exception ex) => new(path,
        ex is UnauthorizedAccessException or System.Security.SecurityException ? "AccessDenied" : "Unavailable", ex.Message);
}
