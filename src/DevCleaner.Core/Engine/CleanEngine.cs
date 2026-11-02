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

/// <summary>
/// Progress of a clean run. Reported when an item starts (<see cref="Completed"/> is null), on sub-steps
/// of large items, and when an item finishes. <see cref="BytesDone"/> is weighted by the findings' scanned sizes.
/// </summary>
public sealed record CleanProgress(int ItemsDone, int ItemsTotal, Finding Current, long BytesDone, long BytesTotal, CleanItemResult? Completed = null)
{
    public double Fraction => BytesTotal > 0
        ? Math.Clamp((double)BytesDone / BytesTotal, 0, 1)
        : ItemsTotal == 0 ? 1 : (double)ItemsDone / ItemsTotal;
}

public sealed record CleanReport(DateTime StartedUtc, DateTime FinishedUtc, bool DryRun, IReadOnlyList<CleanItemResult> Items)
{
    public long TotalBytesFreed => Items.Sum(i => i.BytesFreed);
}

public sealed class CleanEngine(IPlatformServices platform, IReadOnlyList<ICleanupProvider> providers, PathMatcher exclusions, ICleanRecorder? recorder = null)
{
    // Smaller batches give finer progress; the per-call overhead is small next to moving the files.
    private const int RecycleBatch = 64;

    public CleanReport Clean(IReadOnlyList<Finding> findings, CleanOptions options, IProgress<CleanProgress>? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        var results = new List<CleanItemResult>();
        var bytesTotal = findings.Sum(f => Math.Max(0, f.SizeBytes));
        var bytesDone = 0L;
        for (var i = 0; i < findings.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var f = findings[i];
            var size = Math.Max(0, f.SizeBytes);
            var itemsDone = i;
            var before = bytesDone;
            progress?.Report(new CleanProgress(itemsDone, findings.Count, f, before, bytesTotal));
            Action<int, int>? onStep = progress is null
                ? null
                : (done, count) => progress.Report(new CleanProgress(itemsDone, findings.Count, f, before + (long)(size * ((double)done / count)), bytesTotal));
            CleanItemResult r;
            try
            {
                r = CleanOne(f, options, onStep, ct);
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
            bytesDone += size;
            progress?.Report(new CleanProgress(i + 1, findings.Count, f, bytesDone, bytesTotal, r));
        }

        var report = new CleanReport(started, DateTime.UtcNow, options.DryRun, results);
        if (!options.DryRun)
            recorder?.Record(report);
        return report;
    }

    private CleanItemResult CleanOne(Finding f, CleanOptions o, Action<int, int>? onStep, CancellationToken ct)
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

        var failures = o.Permanent ? DeletePermanent(targets, onStep, ct) : Recycle(targets, onStep, ct);

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

    private static int DeletePermanent(List<string> targets, Action<int, int>? onStep, CancellationToken ct)
    {
        var failed = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            var info = Directory.Exists(t) ? (FileSystemInfo)new DirectoryInfo(t) : new FileInfo(t);
            if (info is DirectoryInfo d && !FsUtil.IsReparsePoint(d))
                failed += FsUtil.DeleteTree(t, deleteRoot: true, ct);
            else if (!FsUtil.TryDelete(info))
                failed++;
            onStep?.Invoke(i + 1, targets.Count);
        }

        return failed;
    }

    private int Recycle(List<string> targets, Action<int, int>? onStep, CancellationToken ct)
    {
        var failed = 0;
        var done = 0;
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

            done += batch.Length;
            onStep?.Invoke(done, targets.Count);
        }

        return failed;
    }
}
