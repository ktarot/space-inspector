namespace SpaceInspector.Core.Indexing;

public static class ScanScope
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = path.Trim();
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            throw new ArgumentException("드라이브 문자로 시작하는 로컬 절대 경로를 선택하세요.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static string Prefix(string path) => Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
    public static bool Contains(string parent, string child) =>
        string.Equals(parent, child, StringComparison.OrdinalIgnoreCase) ||
        child.StartsWith(Prefix(parent), StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> NormalizeRoots(IEnumerable<string> paths)
    {
        var sorted = paths.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length);
        var roots = new List<string>();
        foreach (var path in sorted)
            if (!roots.Any(root => Contains(root, path))) roots.Add(path);
        if (roots.Count == 0) throw new ArgumentException("검사할 드라이브 또는 폴더를 선택하세요.");
        return roots;
    }
}
