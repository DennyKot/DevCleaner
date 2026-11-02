using DevCleaner.Core.Abstractions;
using DevCleaner.Core.Git;
using DevCleaner.Core.IO;

namespace DevCleaner.Core.Providers;

/// <summary>
/// Language-agnostic build output: folders inside Git work trees that .gitignore marks as fully ignored.
/// Only whole directories are reported, never individual ignored files (those are often secrets or local config).
/// </summary>
public sealed class GitIgnoredProvider(GitInspector git) : ICleanupProvider
{
    public const string ProviderId = "git-ignored";

    private static readonly string[] WalkSkip = [".git", ".hg", ".svn", "node_modules", "$RECYCLE.BIN", "System Volume Information"];

    /// <summary>Ignored folders that typically hold user state rather than regenerable output.</summary>
    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".vscode", ".idea", ".fleet", ".history", ".ssh", ".aws", ".azure", ".kube", ".secrets", "secrets",
        ".env", "env.local", "data", "db", "database", "backup", "backups", "uploads", "storage", "certs", "keys",
    };

    public string Id => ProviderId;
    public string Name => "Git-ignored build output";
    public IReadOnlyCollection<Category> Categories => [Category.GitIgnored];

    public IEnumerable<Finding> Scan(ScanContext context, CancellationToken ct)
    {
        if (!context.Includes(Category.GitIgnored) || !git.IsAvailable)
            yield break;

        foreach (var repo in FindRepos(context, ct))
        {
            ct.ThrowIfCancellationRequested();
            var ignored = git.GetIgnoredDirectories(repo, ct);
            if (ignored is null)
            {
                context.Errors.Enqueue($"{Name}: git could not read {repo}");
                continue;
            }

            foreach (var dir in ignored)
            {
                if (IsProtected(repo, dir) || context.Exclusions.IsMatch(dir))
                    continue;
                var info = new DirectoryInfo(dir);
                if (!info.Exists || FsUtil.IsReparsePoint(info) || HasReparseAncestor(repo, info))
                    continue;

                var stats = FsUtil.Measure(dir, ct);
                if (stats.Size == 0)
                    continue;

                yield return new Finding
                {
                    ProviderId = Id,
                    Category = Category.GitIgnored,
                    Name = $"Git-ignored: {Path.GetRelativePath(repo, dir).Replace('\\', '/')}",
                    Path = dir,
                    Kind = FindingKind.Directory,
                    SizeBytes = stats.Size,
                    FileCount = stats.FileCount,
                    LastModifiedUtc = stats.LastModifiedUtc,
                    Risk = RiskLevel.Review,
                    ProjectRoot = repo,
                    Note = "Ignored by .gitignore",
                };
            }
        }
    }

    internal static bool IsProtected(string repo, string dir)
    {
        var rel = Path.GetRelativePath(repo, dir);
        if (rel == "." || rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel))
            return true;
        return rel.Split(Path.DirectorySeparatorChar).Any(s => Protected.Contains(s) || s.StartsWith(".env", StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasReparseAncestor(string repo, DirectoryInfo dir)
    {
        for (var d = dir.Parent; d is not null && !string.Equals(d.FullName.TrimEnd(Path.DirectorySeparatorChar), repo.TrimEnd(Path.DirectorySeparatorChar), OsInfo.PathComparison); d = d.Parent)
        {
            if (FsUtil.IsReparsePoint(d))
                return true;
        }

        return false;
    }

    /// <summary>Repos at or below the project roots (never above them). Nested repos are reported separately.</summary>
    private static IEnumerable<string> FindRepos(ScanContext context, CancellationToken ct)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        foreach (var root in context.Options.ProjectRoots.Where(Directory.Exists).Select(Path.GetFullPath))
            stack.Push((root, 0));

        var visited = new HashSet<string>(OsInfo.PathComparer);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            if (!visited.Add(dir) || context.Exclusions.IsMatch(dir))
                continue;
            if (GitInspector.IsRepoRoot(dir))
                yield return dir;
            if (depth >= context.Options.MaxProjectDepth)
                continue;

            foreach (var sub in FsUtil.SafeEntries(dir).OfType<DirectoryInfo>())
            {
                if (!WalkSkip.Contains(sub.Name, OsInfo.PathComparer) && !FsUtil.IsReparsePoint(sub))
                    stack.Push((sub.FullName, depth + 1));
            }
        }
    }
}
