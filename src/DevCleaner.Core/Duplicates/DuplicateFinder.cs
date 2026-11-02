using System.IO.Hashing;

namespace DevCleaner.Core.Duplicates;

public sealed record DuplicateFile(string Path, long Size, DateTime LastWriteUtc, DateTime CreationUtc);

public sealed record DuplicateGroup(long Size, ulong Hash, IReadOnlyList<DuplicateFile> Files)
{
    public long WastedBytes => Size * (Files.Count - 1);

    /// <summary>The copy to keep: oldest creation time.</summary>
    public DuplicateFile Keeper => Files.OrderBy(f => f.CreationUtc).ThenBy(f => f.Path.Length).First();
}

public sealed record DuplicateOptions
{
    public IReadOnlyList<string> Roots { get; init; } = [];
    public long MinSize { get; init; } = 1024 * 1024;
    public PathMatcher? Exclusions { get; init; }
}

public static class DuplicateFinder
{
    private const int PartialBytes = 4096;

    /// <summary>Size grouping, then 4 KB partial hash, then full xxHash64.</summary>
    public static List<DuplicateGroup> Find(DuplicateOptions o, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var seen = new HashSet<string>(OsInfo.PathComparer);
        var bySize = new Dictionary<long, List<FileInfo>>();
        foreach (var root in o.Roots.Where(Directory.Exists))
        {
            progress?.Report($"Listing {root}");
            foreach (var f in new DirectoryInfo(root).EnumerateFiles("*", opts))
            {
                ct.ThrowIfCancellationRequested();
                if (f.Length < o.MinSize || !seen.Add(f.FullName) || o.Exclusions?.IsMatch(f.FullName) == true)
                    continue;
                if (!bySize.TryGetValue(f.Length, out var list))
                    bySize[f.Length] = list = [];
                list.Add(f);
            }
        }

        var candidates = bySize.Values.Where(l => l.Count > 1).ToList();
        progress?.Report($"Hashing {candidates.Sum(c => c.Count)} candidate files");

        var groups = new System.Collections.Concurrent.ConcurrentBag<DuplicateGroup>();
        Parallel.ForEach(candidates, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 }, sameSize =>
        {
            var size = sameSize[0].Length;
            foreach (var partial in GroupBy(sameSize, f => Hash(f, PartialBytes)))
            {
                List<List<FileInfo>> full = size <= PartialBytes ? [partial] : GroupBy(partial, f => Hash(f, long.MaxValue));
                foreach (var g in full)
                {
                    var hash = size <= PartialBytes ? Hash(g[0], PartialBytes) : Hash(g[0], long.MaxValue);
                    if (hash is { } h)
                        groups.Add(new DuplicateGroup(size, h, g.Select(f => new DuplicateFile(f.FullName, size, f.LastWriteTimeUtc, f.CreationTimeUtc)).ToList()));
                }
            }
        });

        return groups.OrderByDescending(g => g.WastedBytes).ToList();
    }

    public static IEnumerable<Finding> ToFindings(DuplicateGroup group) =>
        group.Files.Where(f => f != group.Keeper).Select(f => new Finding
        {
            ProviderId = "duplicates",
            Category = Category.Duplicates,
            Name = Path.GetFileName(f.Path),
            Path = f.Path,
            Kind = FindingKind.File,
            SizeBytes = f.Size,
            FileCount = 1,
            LastModifiedUtc = f.LastWriteUtc,
            Risk = RiskLevel.Review,
            Note = $"Duplicate of {group.Keeper.Path}",
        });

    private static List<List<FileInfo>> GroupBy(List<FileInfo> files, Func<FileInfo, ulong?> key) =>
        files.Select(f => (f, k: key(f)))
            .Where(x => x.k is not null)
            .GroupBy(x => x.k)
            .Where(g => g.Count() > 1)
            .Select(g => g.Select(x => x.f).ToList())
            .ToList();

    private static ulong? Hash(FileInfo f, long maxBytes)
    {
        try
        {
            using var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);
            var h = new XxHash64();
            if (maxBytes >= f.Length)
            {
                h.Append(s);
            }
            else
            {
                var buf = new byte[maxBytes];
                var read = s.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
                h.Append(buf.AsSpan(0, read));
            }

            return h.GetCurrentHashAsUInt64();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
