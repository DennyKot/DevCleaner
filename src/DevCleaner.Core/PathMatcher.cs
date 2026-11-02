namespace DevCleaner.Core;

/// <summary>Matches paths against exclusion entries: plain paths match themselves and descendants, wildcards use simple globbing.</summary>
public sealed class PathMatcher
{
    private readonly string[] _prefixes;
    private readonly string[] _globs;

    public static PathMatcher Empty { get; } = new([]);

    public PathMatcher(IEnumerable<string> patterns)
    {
        var list = patterns.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        _globs = list.Where(HasWildcard).ToArray();
        _prefixes = list.Where(p => !HasWildcard(p)).Select(Normalize).ToArray();
    }

    public bool IsMatch(string path)
    {
        var full = Normalize(path);
        foreach (var p in _prefixes)
        {
            if (full.Equals(p, OsInfo.PathComparison) ||
                (full.StartsWith(p, OsInfo.PathComparison) && full.Length > p.Length && IsSep(full[p.Length])))
                return true;
        }

        foreach (var g in _globs)
        {
            if (System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(g, full, OsInfo.PathComparison != StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    public static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool HasWildcard(string p) => p.Contains('*') || p.Contains('?');

    private static bool IsSep(char c) => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar;
}
