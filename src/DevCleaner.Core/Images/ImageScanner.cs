namespace DevCleaner.Core.Images;

/// <summary>Finds JPEG/PNG/BMP/TIFF/HEIC files worth looking at. Skips cloud-only placeholders, hidden/dev folders and our own temp files.</summary>
public static class ImageScanner
{
    internal const string TempSuffix = ".devcleaner-tmp";

    // FILE_ATTRIBUTE_RECALL_ON_OPEN / RECALL_ON_DATA_ACCESS: OneDrive/Dropbox "online-only" files; reading would download them.
    private const FileAttributes CloudOnly = FileAttributes.Offline | (FileAttributes)0x00040000 | (FileAttributes)0x00400000;

    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "$RECYCLE.BIN", "System Volume Information", "AppData",
    };

    /// <summary>Identifies one exact file version, so images DevCleaner already compressed are not offered again.</summary>
    public static string OptimizedKey(string path, long size, DateTime lastWriteUtc) =>
        $"{PathMatcher.Normalize(path)}|{size}|{lastWriteUtc.Ticks}";

    public static List<ImageCandidate> Find(ImageScanOptions o, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var results = new List<ImageCandidate>();
        var seen = new HashSet<string>(OsInfo.PathComparer);
        foreach (var root in o.Roots.Where(Directory.Exists).Select(Path.GetFullPath))
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(root));
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                if (o.Exclusions?.IsMatch(dir.FullName) == true)
                    continue;
                progress?.Report(dir.FullName);
                IEnumerable<FileSystemInfo> entries;
                try
                {
                    entries = dir.EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint });
                    foreach (var e in entries)
                    {
                        if (e is DirectoryInfo sub)
                        {
                            if (!sub.Name.StartsWith('.') && !SkippedFolders.Contains(sub.Name))
                                stack.Push(sub);
                            continue;
                        }

                        if (e is not FileInfo f || ImageCompressor.KindFromExtension(f.Name) is not { } kind)
                            continue;
                        if ((kind == ImageKind.Png && !o.IncludePng) || (kind == ImageKind.Bmp && !o.IncludeBmp)
                            || (kind == ImageKind.Tiff && !o.IncludeTiff) || (kind == ImageKind.Heic && !o.IncludeHeic))
                            continue;
                        if (f.Length < o.MinSize || (f.Attributes & CloudOnly) != 0 || f.Name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!seen.Add(f.FullName) || o.Exclusions?.IsMatch(f.FullName) == true || o.Skip?.Invoke(f) == true)
                            continue;
                        results.Add(new ImageCandidate(f.FullName, root, kind, f.Length, f.LastWriteTimeUtc));
                        if (results.Count >= o.MaxResults)
                            return results;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return results;
    }
}
