using DevCleaner.Core.Abstractions;
using DevCleaner.Core.IO;
using DevCleaner.Core.Rules;

namespace DevCleaner.Core.Providers;

/// <summary>Scans fixed locations (temp, caches, browsers, IDEs) defined by path rules.</summary>
public sealed class PathRuleProvider(IEnumerable<CleanupRule> rules) : ICleanupProvider
{
    private readonly CleanupRule[] _rules = rules.Where(r => !r.IsProjectRule && r.AppliesTo(OsInfo.Current)).ToArray();

    public string Id => "paths";
    public string Name => "Known locations";
    public IReadOnlyCollection<Category> Categories => _rules.Select(r => r.Category).Distinct().ToArray();

    public IEnumerable<Finding> Scan(ScanContext context, CancellationToken ct)
    {
        var seen = new HashSet<string>(OsInfo.PathComparer);
        foreach (var rule in _rules)
        {
            if (!context.Includes(rule.Category) || context.Options.DisabledProviders.Contains(rule.Id))
                continue;

            foreach (var pattern in rule.Paths)
            {
                foreach (var path in PathExpander.Expand(pattern))
                {
                    ct.ThrowIfCancellationRequested();
                    if (!seen.Add(path) || context.Exclusions.IsMatch(path))
                        continue;

                    var stats = FsUtil.Measure(path, ct);
                    if (stats.Size == 0)
                        continue;

                    var companions = Companions(rule, path, context);
                    if (companions.Length > 0)
                        stats = stats with { Size = stats.Size + companions.Sum(c => new FileInfo(c).Length), FileCount = stats.FileCount + companions.Length };

                    var isFile = File.Exists(path);
                    yield return new Finding
                    {
                        ProviderId = rule.Id,
                        Category = rule.Category,
                        Name = rule.Name,
                        Path = path,
                        Kind = isFile ? FindingKind.File : rule.ContentsOnly ? FindingKind.DirectoryContents : FindingKind.Directory,
                        SizeBytes = stats.Size,
                        FileCount = stats.FileCount,
                        LastModifiedUtc = stats.LastModifiedUtc,
                        Risk = rule.Risk,
                        Note = rule.Note,
                        Processes = rule.Processes,
                        Companions = companions,
                    };
                }
            }
        }
    }

    /// <summary>Existing, non-excluded plain files named by the rule's companion patterns, next to <paramref name="path"/>.</summary>
    private static string[] Companions(CleanupRule rule, string path, ScanContext context)
    {
        if (rule.Companions.Length == 0 || Path.GetDirectoryName(path) is not { } dir)
            return [];

        var stem = Path.GetFileNameWithoutExtension(path);
        return rule.Companions
            .Select(c => Path.Combine(dir, c.Replace("{stem}", stem, StringComparison.OrdinalIgnoreCase)))
            .Where(p => !OsInfo.PathComparer.Equals(p, path) && File.Exists(p)
                && !new FileInfo(p).Attributes.HasFlag(FileAttributes.ReparsePoint) && !context.Exclusions.IsMatch(p))
            .ToArray();
    }
}
