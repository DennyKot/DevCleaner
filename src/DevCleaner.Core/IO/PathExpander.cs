namespace DevCleaner.Core.IO;

/// <summary>Expands rule paths: tokens ({home}, {temp}, ...), %ENV% vars, ~ and '*'/'?' wildcards in any segment.</summary>
public static class PathExpander
{
    public static IEnumerable<string> Expand(string pattern)
    {
        var resolved = ResolveTokens(pattern);
        if (resolved is null || !Path.IsPathFullyQualified(resolved))
            return [];

        var full = Path.GetFullPath(resolved);
        if (!HasWildcard(full))
            return FsUtil.Exists(full) ? [full] : [];

        var root = Path.GetPathRoot(full)!;
        var segments = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> current = [root];
        for (var i = 0; i < segments.Length; i++)
        {
            var seg = segments[i];
            var last = i == segments.Length - 1;
            current = current.SelectMany(dir => Step(dir, seg, last)).ToList();
        }

        return current;
    }

    public static string? ResolveTokens(string pattern)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var s = pattern
            .Replace("{home}", home, StringComparison.OrdinalIgnoreCase)
            .Replace("{temp}", Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            .Replace("{appdata}", Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("{localappdata}", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StringComparison.OrdinalIgnoreCase)
            .Replace("{nugetpackages}", Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(home, ".nuget", "packages"), StringComparison.OrdinalIgnoreCase);

        if (s.StartsWith('~'))
            s = home + s[1..];
        if (OperatingSystem.IsWindows())
        {
            s = Environment.ExpandEnvironmentVariables(s);
            if (s.Contains('%'))
                return null;
        }

        s = s.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return s.Contains("..", StringComparison.Ordinal) ? null : s;
    }

    private static IEnumerable<string> Step(string dir, string seg, bool last)
    {
        if (!HasWildcard(seg))
        {
            var p = Path.Combine(dir, seg);
            return Directory.Exists(p) || (last && File.Exists(p)) ? [p] : [];
        }

        try
        {
            var opts = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            return last
                ? Directory.EnumerateFileSystemEntries(dir, seg, opts).ToList()
                : Directory.EnumerateDirectories(dir, seg, opts).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool HasWildcard(string s) => s.Contains('*') || s.Contains('?');
}
