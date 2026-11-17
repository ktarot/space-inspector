using SpaceInspector.Core.Indexing;

namespace SpaceInspector.Core.Models;

public enum TargetMode { Drives, Folders }

public sealed record WorkSpecification
{
    public TargetMode Mode { get; init; }
    public string[] Roots { get; init; } = [];
    public bool Recursive { get; init; } = true;
    public string[] Extensions { get; init; } = [];
    public Dictionary<string, string?> Volumes { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public WorkSpecification Normalize()
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("대상 모드를 선택하세요.");
        var roots = Roots.Select(ScanScope.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (roots.Length == 0 || roots.Length > 100) throw new ArgumentException("대상을 1~100개 선택하세요.");
        if (Mode == TargetMode.Drives && roots.Any(r => !string.Equals(r, Path.GetPathRoot(r), StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("드라이브 모드에서는 드라이브 루트만 선택하세요.");
        var extensions = Extensions.Select(e => "." + e.Trim().TrimStart('.').ToLowerInvariant()).Distinct().ToArray();
        if (extensions.Length == 0 || extensions.Length > 100 || extensions.Any(e => e.Length < 2 || e[1..].Any(c => !char.IsLetterOrDigit(c))))
            throw new ArgumentException("대상 확장자를 1~100개 지정하세요 (예: jpg, png, mp4).");
        return this with { Roots = Recursive ? ScanScope.NormalizeRoots(roots).ToArray() : roots, Extensions = extensions,
            Volumes = new Dictionary<string, string?>(Volumes, StringComparer.OrdinalIgnoreCase) };
    }

    public bool ContainsFile(string path)
    {
        var normalized = ScanScope.Normalize(path);
        return Roots.Any(root => Recursive ? ScanScope.Contains(root, normalized)
            : string.Equals(root, Path.GetDirectoryName(normalized), StringComparison.OrdinalIgnoreCase));
    }
    public bool AllowsFile(string path) => ContainsFile(path)
        && Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
        && !new[] { "pagefile.sys", "swapfile.sys", "hiberfil.sys", "DumpStack.log.tmp" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    public bool AllowsDirectory(string path)
    {
        var normalized = ScanScope.Normalize(path);
        return Roots.Any(root => Recursive ? ScanScope.Contains(root, normalized) : string.Equals(root, normalized, StringComparison.OrdinalIgnoreCase));
    }
}
