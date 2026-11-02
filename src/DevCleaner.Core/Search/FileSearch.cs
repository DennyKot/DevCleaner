using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace DevCleaner.Core.Search;

public sealed record SearchCriteria
{
    public IReadOnlyList<string> Roots { get; init; } = [];
    public string? NamePattern { get; init; }
    public string? Regex { get; init; }
    public IReadOnlyList<string> Extensions { get; init; } = [];
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public DateTime? ModifiedBeforeUtc { get; init; }
    public DateTime? ModifiedAfterUtc { get; init; }
    public DateTime? AccessedBeforeUtc { get; init; }
    public bool IncludeHidden { get; init; } = true;

    /// <summary>Restrict to typical leftover temp files (*.tmp, ~*, *.bak, *.dmp, ...).</summary>
    public bool TempFilesPreset { get; init; }

    public int MaxResults { get; init; } = 10_000;
}

public static class FileSearch
{
    public static readonly string[] TempPatterns =
    [
        // temp & backup leftovers
        "*.tmp", "*.temp", "~*", "*~", "*.bak", "*.old", "*.orig", "*.rej", "*.$$$", "*.~*", ".~lock.*#", "*.chk", "*.gid",
        // editor swap / undo files
        "*.swp", "*.swo", "*.un~",
        // incomplete downloads
        "*.crdownload", "*.part", "*.partial", "*.download",
        // crash dumps & diagnostics
        "*.dmp", "*.mdmp", "*.hdmp", "*.stackdump", "*.hprof", "hs_err_pid*.log", "*.etl", "*.wer",
        // logs
        "*.log", "*.log.*", "npm-debug.log*", "yarn-error.log*", "pnpm-debug.log*",
        // build / IDE byproducts that are always regenerable
        "*.pyc", "*.pyo", "*.ilk", "*.ipch", "*.VC.db", "*.cache", "*.tlog",
        // OS metadata
        "Thumbs.db", "ehthumbs.db", ".DS_Store", "._*",
    ];

    public static IEnumerable<Finding> Search(SearchCriteria c, PathMatcher? exclusions, CancellationToken ct)
    {
        Regex? regex = string.IsNullOrWhiteSpace(c.Regex)
            ? null
            : new Regex(c.Regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
        var extensions = c.Extensions.Select(e => e.StartsWith('.') ? e : "." + e).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | (c.IncludeHidden ? 0 : FileAttributes.Hidden),
        };

        var count = 0;
        foreach (var root in c.Roots.Where(Directory.Exists))
        {
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                if (!Matches(f, c, regex, extensions) || exclusions?.IsMatch(f.FullName) == true)
                    continue;

                yield return new Finding
                {
                    ProviderId = "search",
                    Category = Category.Custom,
                    Name = f.Name,
                    Path = f.FullName,
                    Kind = FindingKind.File,
                    SizeBytes = f.Length,
                    FileCount = 1,
                    LastModifiedUtc = f.LastWriteTimeUtc,
                    Risk = RiskLevel.Review,
                };
                if (++count >= c.MaxResults)
                    yield break;
            }
        }
    }

    private static bool Matches(FileInfo f, SearchCriteria c, Regex? regex, HashSet<string> extensions)
    {
        if (c.TempFilesPreset && !TempPatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, f.Name)))
            return false;
        if (!string.IsNullOrWhiteSpace(c.NamePattern) && !FileSystemName.MatchesSimpleExpression(c.NamePattern, f.Name))
            return false;
        if (extensions.Count > 0 && !extensions.Contains(f.Extension))
            return false;
        if (c.MinSize is { } min && f.Length < min)
            return false;
        if (c.MaxSize is { } max && f.Length > max)
            return false;
        if (c.ModifiedBeforeUtc is { } mb && f.LastWriteTimeUtc >= mb)
            return false;
        if (c.ModifiedAfterUtc is { } ma && f.LastWriteTimeUtc <= ma)
            return false;
        if (c.AccessedBeforeUtc is { } ab && f.LastAccessTimeUtc >= ab)
            return false;
        if (regex is not null)
        {
            try
            {
                if (!regex.IsMatch(f.FullName))
                    return false;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        return true;
    }
}
