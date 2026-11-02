using System.Collections.Concurrent;
using System.IO.Enumeration;
using DevCleaner.Core.Abstractions;
using DevCleaner.Core.IO;
using DevCleaner.Core.Rules;

namespace DevCleaner.Core.Providers;

/// <summary>Walks project roots and reports build artefact folders next to marker files (*.csproj, package.json, ...).</summary>
public sealed class ProjectRuleProvider(IEnumerable<CleanupRule> rules) : ICleanupProvider
{
    private static readonly string[] AlwaysSkip = [".git", ".hg", ".svn", "node_modules", "$RECYCLE.BIN", "System Volume Information"];

    private readonly CleanupRule[] _rules = rules.Where(r => r.IsProjectRule && r.AppliesTo(OsInfo.Current)).ToArray();

    public string Id => "projects";
    public string Name => "Project artefacts";
    public IReadOnlyCollection<Category> Categories => _rules.Select(r => r.Category).Distinct().ToArray();

    private sealed record Candidate(string Dir, CleanupRule Rule, string Target, string Path, IReadOnlyList<CleanupRule> Matched);

    public IEnumerable<Finding> Scan(ScanContext context, CancellationToken ct)
    {
        var active = _rules.Where(r => context.Includes(r.Category) && !context.Options.DisabledProviders.Contains(r.Id)).ToArray();
        if (active.Length == 0)
            yield break;

        // The directory walk is cheap; measuring artefact folders dominates, so it runs in parallel while results stream out.
        using var output = new BlockingCollection<Finding>();
        var projectDates = new ConcurrentDictionary<string, Lazy<DateTime?>>(OsInfo.PathComparer);
        var worker = Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(
                    Partitioner.Create(Walk(context, active, ct), EnumerablePartitionerOptions.NoBuffering),
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8), CancellationToken = ct },
                    c =>
                    {
                        var stats = FsUtil.Measure(c.Path, ct);
                        if (stats.Size == 0)
                            return;
                        var modified = projectDates.GetOrAdd(c.Dir, d => new Lazy<DateTime?>(() => ProjectLastModified(d, c.Matched, ct))).Value;
                        output.Add(new Finding
                        {
                            ProviderId = c.Rule.Id,
                            Category = c.Rule.Category,
                            Name = $"{c.Rule.Name}: {c.Target}",
                            Path = c.Path,
                            Kind = FindingKind.Directory,
                            SizeBytes = stats.Size,
                            FileCount = stats.FileCount,
                            LastModifiedUtc = stats.LastModifiedUtc,
                            Risk = c.Rule.Risk,
                            ProjectRoot = c.Dir,
                            ProjectLastModifiedUtc = modified,
                            Note = c.Rule.Note,
                            Processes = c.Rule.Processes,
                        }, ct);
                    });
            }
            finally
            {
                output.CompleteAdding();
            }
        }, ct);

        foreach (var f in output.GetConsumingEnumerable(ct))
            yield return f;
        worker.GetAwaiter().GetResult();
    }

    private static IEnumerable<Candidate> Walk(ScanContext context, CleanupRule[] active, CancellationToken ct)
    {
        var stack = new Stack<(string Dir, int Depth)>();
        foreach (var root in context.Options.ProjectRoots.Where(Directory.Exists))
            stack.Push((Path.GetFullPath(root), 0));

        var visited = new HashSet<string>(OsInfo.PathComparer);
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth) = stack.Pop();
            if (!visited.Add(dir) || context.Exclusions.IsMatch(dir))
                continue;

            var entries = FsUtil.SafeEntries(dir).ToList();
            var fileNames = entries.OfType<FileInfo>().Select(f => f.Name).ToList();
            var matched = active.Where(r => r.Markers.Any(m => MarkerMatches(dir, m, fileNames))).ToList();

            var skip = new HashSet<string>(AlwaysSkip, OsInfo.PathComparer);
            foreach (var rule in matched)
            {
                foreach (var target in rule.Targets)
                {
                    skip.Add(target);
                    var path = Path.Combine(dir, target);
                    if (Directory.Exists(path) && !context.Exclusions.IsMatch(path) && !FsUtil.IsReparsePoint(new DirectoryInfo(path)))
                        yield return new Candidate(dir, rule, target, path, matched);
                }
            }

            if (depth >= context.Options.MaxProjectDepth)
                continue;

            foreach (var sub in entries.OfType<DirectoryInfo>())
            {
                if (!skip.Contains(sub.Name) && !FsUtil.IsReparsePoint(sub))
                    stack.Push((sub.FullName, depth + 1));
            }
        }
    }

    /// <summary>Markers are file-name globs, or a relative file path (e.g. "ProjectSettings/ProjectVersion.txt") without wildcards.</summary>
    internal static bool MarkerMatches(string dir, string marker, List<string> fileNames)
    {
        if (marker.IndexOfAny(['/', '\\']) < 0)
            return fileNames.Any(f => FileSystemName.MatchesSimpleExpression(marker, f));
        if (marker.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(marker))
            return false;
        return File.Exists(Path.Combine(dir, marker.Replace('\\', '/').Replace('/', Path.DirectorySeparatorChar)));
    }

    /// <summary>Newest source file write time, ignoring artefact folders.</summary>
    internal static DateTime? ProjectLastModified(string dir, IEnumerable<CleanupRule> matched, CancellationToken ct)
    {
        var skip = new HashSet<string>(AlwaysSkip.Concat(matched.SelectMany(r => r.Targets)), OsInfo.PathComparer)
        {
            "bin", "obj", ".vs", ".idea",
        };
        var latest = DateTime.MinValue;
        var budget = 50_000;
        var stack = new Stack<(string, int)>();
        stack.Push((dir, 0));
        while (stack.Count > 0 && budget > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (d, depth) = stack.Pop();
            foreach (var e in FsUtil.SafeEntries(d))
            {
                if (--budget <= 0)
                    break;
                if (e is FileInfo f)
                {
                    if (f.LastWriteTimeUtc > latest)
                        latest = f.LastWriteTimeUtc;
                }
                else if (depth < 8 && !skip.Contains(e.Name) && !FsUtil.IsReparsePoint(e))
                {
                    stack.Push((e.FullName, depth + 1));
                }
            }
        }

        return latest == DateTime.MinValue ? null : latest;
    }
}
