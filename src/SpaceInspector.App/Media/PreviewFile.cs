using System.IO;
using SpaceInspector.Core.Models;
using SpaceInspector.Infrastructure.FileSystem;
namespace SpaceInspector.App.Media;

public enum PreviewKind { Image, Video, Audio, Unsupported }
public sealed record PreviewFile(string Path, string? Volume)
{
    public PreviewKind Kind => Classify(Path);
    public static PreviewFile? From(object? item) => item switch {
        IndexedFile f => new(f.FullPath, f.VolumeIdentifier),
        DuplicateMember f => new(f.FullPath, f.VolumeIdentifier), _ => null
    };
    public static PreviewKind Classify(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch {
        ".jpg" or ".jpeg" or ".png" or ".bmp" or ".gif" or ".tif" or ".tiff" => PreviewKind.Image,
        ".mp4" or ".m4v" or ".avi" or ".wmv" or ".mpg" or ".mpeg" or ".mov" or ".mkv" or ".webm" or ".flv" => PreviewKind.Video,
        ".wav" or ".mp3" or ".m4a" or ".wma" or ".aac" or ".flac" or ".ogg" or ".opus" => PreviewKind.Audio,
        _ => PreviewKind.Unsupported
    };
    public void Validate()
    {
        if (!System.IO.Path.IsPathFullyQualified(Path) || Path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("로컬 파일만 미리볼 수 있습니다.");
        var root = System.IO.Path.GetPathRoot(Path)!;
        if (new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new IOException("로컬 고정·이동식 드라이브만 미리볼 수 있습니다.");
        for (var dir = new FileInfo(Path).Directory; dir is not null; dir = dir.Parent) Check(File.GetAttributes(dir.FullName));
        Check(File.GetAttributes(Path));
        if (Volume is null || new WindowsVolumeIdentityService().GetIdentity(Path) != Volume)
            throw new IOException("원래 장치를 확인할 수 없습니다.");
        if (!File.Exists(Path)) throw new FileNotFoundException("파일을 찾을 수 없습니다.");
    }
    private static void Check(FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) != 0 || MetadataOnlyFileSource.IsCloudOnly(attributes))
            throw new IOException("링크 또는 온라인 전용 경로는 읽지 않습니다.");
    }
}
