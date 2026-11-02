using DevCleaner.Core.Abstractions;
using DevCleaner.Core.IO;

namespace DevCleaner.Core.Engine;

public enum CleanStatus
{
    Cleaned,
    Partial,
    Skipped,
    Failed,
    DryRun,
}

public sealed class CleanOptions
{
    public bool DryRun { get; init; }
    public bool Permanent { get; init; }

    /// <summary>Clean even if a related process (browser, IDE) is running.</summary>
    public bool Force { get; init; }
}

public sealed record CleanItemResult(Finding Finding, CleanStatus Status, long BytesFreed, string? Message = null);

public sealed record CleanReport(DateTime StartedUtc, DateTime FinishedUtc, bool DryRun, IReadOnlyList<CleanItemResult> Items)
{
    public long TotalBytesFreed => Items.Sum(i => i.BytesFreed);
}

public sealed class CleanEngine(IPlatformServices platform, IReadOnlyList<ICleanupProvider> providers, PathMatcher exclusions, ICleanRecorder? recorder = null)
{
    private const int RecycleBatch = 256;

    public CleanReport Clean(IReadOnlyList<Finding> findings, CleanOptions options, IProgress<CleanItemResult>? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var results = new List<CleanItemResult>();
        foreach (var f in findings)
        {
            ct.ThrowIfCancellationRequested();
            CleanItemResult r;
            try
            {
                r = CleanOne(f, options, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                r = new CleanItemResult(f, CleanStatus.Failed, 0, ex.Message);
            }

            results.Add(r);
            progress?.Report(r);
        }

        var report = new CleanReport(started, DateTime.UtcNow, options.DryRun, results);
        if (!options.DryRun)
            recorder?.Record(report);
        return report;
    }

    private CleanItemResult CleanOne(Finding f, CleanOptions o, CancellationToken ct)
    {
        if (!o.Force && f.Processes.Count > 0)
        {
            var running = ProcessRunner.RunningOf(f.Processes);
            if (running.Count > 0)
                return new(f, CleanStatus.Skipped, 0, $"Close {string.Join(", ", running)} first (or use force)");
        }

        if (!f.IsFileSystem)
        {
            if (o.DryRun)
                return new(f, CleanStatus.DryRun, f.SizeBytes);
            if (providers.FirstOrDefault(p => p.Id == f.ProviderId) is not ICustomCleaner cleaner)
                return new(f, CleanStatus.Failed, 0, $"No cleaner for provider '{f.ProviderId}'");
            return new(f, CleanStatus.Cleaned, cleaner.Clean(f, ct));
        }

        if (exclusions.IsMatch(f.Path))
            return new(f, CleanStatus.Skipped, 0, "Excluded");
        if (!FsUtil.Exists(f.Path))
            return new(f, CleanStatus.Skipped, 0, "No longer exists");
        if (f.Kind != FindingKind.DirectoryContents && !SafetyGuard.IsSafeToDelete(f.Path, out var reason))
            return new(f, CleanStatus.Skipped, 0, reason);
        if (o.DryRun)
            return new(f, CleanStatus.DryRun, f.SizeBytes);
        if (!o.Permanent && !platform.RecycleBin.IsSupported)
            return new(f, CleanStatus.Skipped, 0, "Recycle Bin not supported on this OS; use permanent delete");

        var targets = f.Kind == FindingKind.DirectoryContents
            ? FsUtil.SafeEntries(f.Path).Select(e => e.FullName).Where(p => SafetyGuard.IsSafeToDelete(p, out _)).ToList()
            : [f.Path];

        var failures = o.Permanent ? DeletePermanent(targets, ct) : Recycle(targets, ct);

        var remaining = FsUtil.Exists(f.Path) ? FsUtil.Measure(f.Path, ct).Size : 0;
        var freed = Math.Max(0, f.SizeBytes - remaining);
        if (failures == 0)
            return new(f, CleanStatus.Cleaned, freed);

        string? lockInfo = null;
        if (f.Kind == FindingKind.File)
        {
            var lockers = platform.LockDetector.GetLockingProcesses(f.Path);
            if (lockers.Count > 0)
                lockInfo = $" (in use by {string.Join(", ", lockers)})";
        }

        return new(f, freed > 0 ? CleanStatus.Partial : CleanStatus.Failed, freed, $"{failures} item(s) in use or inaccessible{lockInfo}");
    }

    private static int DeletePermanent(List<string> targets, CancellationToken ct)
    {
        var failed = 0;
        foreach (var t in targets)
        {
            var info = Directory.Exists(t) ? (FileSystemInfo)new DirectoryInfo(t) : new FileInfo(t);
            if (info is DirectoryInfo d && !FsUtil.IsReparsePoint(d))
                failed += FsUtil.DeleteTree(t, deleteRoot: true, ct);
            else if (!FsUtil.TryDelete(info))
                failed++;
        }

        return failed;
    }

    private int Recycle(List<string> targets, CancellationToken ct)
    {
        var failed = 0;
        foreach (var batch in targets.Chunk(RecycleBatch))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                platform.RecycleBin.Recycle(batch);
            }
            catch (IOException)
            {
                // A locked item aborts the whole batch; retry one by one so the rest still gets recycled.
                foreach (var p in batch.Where(FsUtil.Exists))
                {
                    try
                    {
                        platform.RecycleBin.Recycle([p]);
                    }
                    catch (IOException)
                    {
                        failed++;
                    }
                }
            }
        }

        return failed;
    }
}
