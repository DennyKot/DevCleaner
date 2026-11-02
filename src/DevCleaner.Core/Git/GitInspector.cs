using System.Collections.Concurrent;

namespace DevCleaner.Core.Git;

public sealed record GitRepoStatus(string Root, int UncommittedChanges)
{
    public bool IsDirty => UncommittedChanges > 0;
}

/// <summary>Thin, cached wrapper over the git CLI. Every method degrades to "unknown" when git is unavailable.</summary>
public sealed class GitInspector(string gitExecutable = "git")
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, string?> _rootOf = new(OsInfo.PathComparer);
    private readonly ConcurrentDictionary<string, Lazy<GitRepoStatus?>> _status = new(OsInfo.PathComparer);
    private readonly Lazy<bool> _available = new(() => ProcessRunner.Run(gitExecutable, ["--version"], TimeSpan.FromSeconds(10)) is { ExitCode: 0 });

    public bool IsAvailable => _available.Value;

    /// <summary>A directory is a work-tree root if it has a ".git" folder or a ".git" file (worktrees, submodules).</summary>
    public static bool IsRepoRoot(string dir) =>
        Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"));

    /// <summary>Nearest enclosing work-tree root, found by walking up parent folders.</summary>
    public string? FindRepoRoot(string path)
    {
        var dir = Path.GetFullPath(path);
        var visited = new List<string>();
        string? root = null;
        while (dir is not null)
        {
            if (_rootOf.TryGetValue(dir, out var cached))
            {
                root = cached;
                break;
            }

            visited.Add(dir);
            if (IsRepoRoot(dir))
            {
                root = dir;
                break;
            }

            dir = Path.GetDirectoryName(dir);
        }

        foreach (var v in visited)
            _rootOf[v] = root;
        return root;
    }

    /// <returns>null when git is missing or the folder is not a readable repository.</returns>
    public GitRepoStatus? GetStatus(string repoRoot) =>
        _status.GetOrAdd(Path.GetFullPath(repoRoot), r => new Lazy<GitRepoStatus?>(() => LoadStatus(r))).Value;

    /// <summary>
    /// Folders that an ignore pattern matches directly and that hold no tracked files, as absolute paths.
    /// git collapses untracked folders whose content is all ignored (e.g. "lib/" holding only "lib/_build/"),
    /// so collapsed entries are re-checked with check-ignore and expanded into their sub-folders when not matched.
    /// </summary>
    public IReadOnlyList<string>? GetIgnoredDirectories(string repoRoot, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return null;
        var r = Git(repoRoot, "ls-files", "--others", "--ignored", "--exclude-standard", "--directory", "--no-empty-directory", "-z");
        if (r is null)
            return null;

        var result = new List<string>();
        var pending = ParseIgnoredDirectories(repoRoot, r).ToList();
        for (var depth = 0; pending.Count > 0 && depth < 8; depth++)
        {
            ct.ThrowIfCancellationRequested();
            var rel = pending.ToDictionary(p => Path.GetRelativePath(repoRoot, p).Replace('\\', '/') + "/", p => p);
            var matched = CheckIgnore(repoRoot, rel.Keys);
            if (matched is null)
                return null;

            var next = new List<string>();
            foreach (var (key, path) in rel)
            {
                if (matched.Contains(key))
                {
                    result.Add(path);
                    continue;
                }

                foreach (var sub in IO.FsUtil.SafeEntries(path).OfType<DirectoryInfo>())
                {
                    if (!IO.FsUtil.IsReparsePoint(sub))
                        next.Add(sub.FullName);
                }
            }

            pending = next;
        }

        return result;
    }

    private HashSet<string>? CheckIgnore(string repoRoot, IEnumerable<string> relDirs)
    {
        var input = string.Concat(relDirs.Select(d => d + "\0"));
        // Exit code 1 means "nothing ignored", which is a valid answer.
        var r = ProcessRunner.Run(gitExecutable, [.. Prefix(repoRoot), "check-ignore", "--stdin", "-z"], Timeout, input, Utf8);
        if (r is null || r.ExitCode > 1)
            return null;
        return r.StdOut.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
    }

    internal static IReadOnlyList<string> ParseIgnoredDirectories(string repoRoot, string output) =>
        output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(e => e.EndsWith('/'))
            .Select(e => e.TrimEnd('/'))
            .Where(e => e.Length > 0 && !e.Split('/').Contains(".."))
            .Select(e => Path.GetFullPath(Path.Combine(repoRoot, e.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

    internal static int CountStatusEntries(string porcelainZ)
    {
        // Renames/copies ("R"/"C") are followed by an extra NUL-terminated source path.
        var parts = porcelainZ.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        var count = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            count++;
            if (parts[i].Length >= 2 && (parts[i][0] is 'R' or 'C' || parts[i][1] is 'R' or 'C'))
                i++;
        }

        return count;
    }

    private GitRepoStatus? LoadStatus(string repoRoot)
    {
        if (!IsAvailable)
            return null;
        var r = Git(repoRoot, "status", "--porcelain=v1", "-z", "--untracked-files=normal", "--ignore-submodules=dirty");
        return r is null ? null : new GitRepoStatus(repoRoot, CountStatusEntries(r));
    }

    private static readonly System.Text.Encoding Utf8 = new System.Text.UTF8Encoding(false);

    // safe.directory=* avoids "dubious ownership" failures on repos owned by another account; we only read.
    private static string[] Prefix(string repoRoot) => ["-c", "core.quotepath=off", "-c", "safe.directory=*", "-C", repoRoot];

    private string? Git(string repoRoot, params string[] args)
    {
        var r = ProcessRunner.Run(gitExecutable, [.. Prefix(repoRoot), .. args], Timeout, encoding: Utf8);
        return r is { ExitCode: 0 } ? r.StdOut : null;
    }
}
