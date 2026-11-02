namespace DevCleaner.Core.IO;

public readonly record struct DirStats(long Size, long FileCount, DateTime? LastModifiedUtc);

public static class FsUtil
{
    private static readonly EnumerationOptions RecursiveOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly EnumerationOptions FlatOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    public static DirStats Measure(string path, CancellationToken ct = default)
    {
        if (File.Exists(path))
        {
            var fi = new FileInfo(path);
            return new DirStats(fi.Length, 1, fi.LastWriteTimeUtc);
        }

        long size = 0, count = 0;
        var last = DateTime.MinValue;
        try
        {
            foreach (var f in new DirectoryInfo(path).EnumerateFiles("*", RecursiveOptions))
            {
                ct.ThrowIfCancellationRequested();
                size += f.Length;
                count++;
                if (f.LastWriteTimeUtc > last)
                    last = f.LastWriteTimeUtc;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new DirStats(size, count, last == DateTime.MinValue ? null : last);
    }

    public static IEnumerable<FileSystemInfo> SafeEntries(string dir)
    {
        try
        {
            return new DirectoryInfo(dir).EnumerateFileSystemInfos("*", FlatOptions).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static bool IsReparsePoint(FileSystemInfo e) => e.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Deletes a tree without following links; continues on failures.</summary>
    /// <returns>Number of entries that could not be deleted.</returns>
    public static int DeleteTree(string dir, bool deleteRoot, CancellationToken ct)
    {
        var failed = 0;
        foreach (var e in SafeEntries(dir))
        {
            ct.ThrowIfCancellationRequested();
            if (e is DirectoryInfo d && !IsReparsePoint(d))
                failed += DeleteTree(d.FullName, deleteRoot: true, ct);
            else if (!TryDelete(e))
                failed++;
        }

        if (deleteRoot && failed == 0 && !TryDelete(new DirectoryInfo(dir)))
            failed++;
        return failed;
    }

    public static bool TryDelete(FileSystemInfo e)
    {
        try
        {
            if (e.Attributes.HasFlag(FileAttributes.ReadOnly))
                e.Attributes &= ~FileAttributes.ReadOnly;
            if (e is DirectoryInfo d)
                d.Delete(recursive: false);
            else
                e.Delete();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);
}
