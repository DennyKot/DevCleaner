using DevCleaner.Core.Engine;

namespace DevCleaner.Core.Images;

/// <summary>
/// Writes compressed images. The compressed file is written and read back next to the original first; the original is
/// then removed only through <see cref="CleanEngine"/> (exclusions, SafetyGuard, Recycle Bin), and the new file takes its
/// place with the original timestamps and attributes.
/// </summary>
public static class ImageApplier
{
    public static List<ImageApplyResult> Apply(IReadOnlyList<ImageCandidate> items, ImageApplyOptions o, CleanEngine engine,
        IProgress<ImageApplyProgress>? progress = null, CancellationToken ct = default)
    {
        if (o.Originals == ImageOriginalsMode.SaveCopies && string.IsNullOrWhiteSpace(o.OutputFolder))
            throw new ArgumentException("Choose a folder for the compressed copies");
        var results = new List<ImageApplyResult>(items.Count);
        for (var i = 0; i < items.Count && !ct.IsCancellationRequested; i++)
        {
            progress?.Report(new ImageApplyProgress(i, items.Count, items[i]));
            try
            {
                results.Add(ApplyOne(items[i], o, engine, ct));
            }
            catch (OperationCanceledException)
            {
                // Cancellation only interrupts compression, never the swap; finished items stay in the results.
                break;
            }
        }

        return results;
    }

    /// <summary>History/stats view of a run: freed bytes are the savings, not the originals' size.</summary>
    public static CleanReport ToReport(IReadOnlyList<ImageApplyResult> results, DateTime startedUtc, ImageApplyOptions o)
    {
        var items = results
            .Where(r => r.Status is not ImageApplyStatus.NoGain)
            .Select(r => new CleanItemResult(
                new Finding
                {
                    ProviderId = "images",
                    Category = Category.Images,
                    Name = Path.GetFileName(r.OutputPath ?? r.Candidate.Path),
                    Path = r.OutputPath ?? r.Candidate.Path,
                    Kind = FindingKind.File,
                    SizeBytes = r.OriginalSize,
                    FileCount = 1,
                },
                r.Status switch
                {
                    ImageApplyStatus.Compressed => CleanStatus.Cleaned,
                    ImageApplyStatus.DryRun => CleanStatus.DryRun,
                    ImageApplyStatus.Skipped => CleanStatus.Skipped,
                    _ => CleanStatus.Failed,
                },
                o.Originals == ImageOriginalsMode.SaveCopies && !o.DryRun ? 0 : r.SavedBytes,
                r.Message ?? (r.SavedBytes > 0 ? $"{Bytes.Format(r.OriginalSize)} → {Bytes.Format(r.NewSize)}" : null)))
            .ToList();
        return new CleanReport(startedUtc, DateTime.UtcNow, o.DryRun, items);
    }

    private static ImageApplyResult ApplyOne(ImageCandidate c, ImageApplyOptions o, CleanEngine engine, CancellationToken ct)
    {
        var original = new FileInfo(c.Path);
        if (!original.Exists)
            return new(c, ImageApplyStatus.Skipped, c.SizeBytes, c.SizeBytes, Message: "No longer exists");
        var (created, written, attributes) = (original.CreationTimeUtc, original.LastWriteTimeUtc, original.Attributes);

        ImageAnalysis analysis;
        byte[]? data;
        try
        {
            (analysis, data) = ImageCompressor.Compress(c.Path, o.Compression, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(c, ImageApplyStatus.Failed, original.Length, original.Length, Message: ex.Message);
        }

        if (data is null)
        {
            var status = analysis.Status switch
            {
                ImageAnalysisStatus.NoGain => ImageApplyStatus.NoGain,
                ImageAnalysisStatus.Unsupported => ImageApplyStatus.Skipped,
                _ => ImageApplyStatus.Failed,
            };
            return new(c, status, analysis.OriginalSize, analysis.OriginalSize, Message: analysis.Message);
        }

        var copies = o.Originals == ImageOriginalsMode.SaveCopies;
        var target = copies ? CopyTarget(c, analysis.OutputExtension, o.OutputFolder!) : Path.ChangeExtension(c.Path, analysis.OutputExtension);
        var renamed = !string.Equals(target, c.Path, OsInfo.PathComparison);
        if (renamed && File.Exists(target))
            return new(c, ImageApplyStatus.Skipped, analysis.OriginalSize, data.Length, target, $"{target} already exists");
        if (o.DryRun)
            return new(c, ImageApplyStatus.DryRun, analysis.OriginalSize, data.Length, target);

        if (copies)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var fs = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                    fs.Write(data);
                File.SetCreationTimeUtc(target, created);
                File.SetLastWriteTimeUtc(target, written);
                return new(c, ImageApplyStatus.Compressed, analysis.OriginalSize, data.Length, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(c, ImageApplyStatus.Failed, analysis.OriginalSize, analysis.OriginalSize, target, ex.Message);
            }
        }

        var temp = Path.Combine(Path.GetDirectoryName(c.Path)!, "." + Path.GetFileName(c.Path) + ImageScanner.TempSuffix);
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }

            if (!File.ReadAllBytes(temp).AsSpan().SequenceEqual(data))
                throw new IOException("Written file does not match");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return new(c, ImageApplyStatus.Failed, analysis.OriginalSize, analysis.OriginalSize, Message: ex.Message);
        }

        var finding = new Finding
        {
            ProviderId = "images",
            Category = Category.Images,
            Name = c.Name,
            Path = c.Path,
            Kind = FindingKind.File,
            SizeBytes = original.Length,
            FileCount = 1,
            LastModifiedUtc = written,
            Risk = RiskLevel.Review,
        };
        var permanent = o.Originals == ImageOriginalsMode.DeletePermanently;
        var removed = engine.Clean([finding], new CleanOptions { Permanent = permanent }, null, ct).Items[0];
        if (removed.Status != CleanStatus.Cleaned)
        {
            TryDelete(temp);
            return new(c, ImageApplyStatus.Skipped, analysis.OriginalSize, analysis.OriginalSize, Message: removed.Message ?? removed.Status.ToString());
        }

        try
        {
            File.Move(temp, target);
            File.SetCreationTimeUtc(target, created);
            File.SetLastWriteTimeUtc(target, written);
            File.SetAttributes(target, attributes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var where = permanent ? "was deleted" : "is in the Recycle Bin";
            return new(c, ImageApplyStatus.Failed, analysis.OriginalSize, analysis.OriginalSize, temp,
                $"Original {where}, but the compressed file could not be moved into place ({ex.Message}); it is at {temp}");
        }

        return new(c, ImageApplyStatus.Compressed, analysis.OriginalSize, data.Length, target);
    }

    private static string CopyTarget(ImageCandidate c, string extension, string outputFolder)
    {
        var relative = Path.GetRelativePath(c.Root, c.Path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            relative = Path.GetFileName(c.Path);
        return Path.GetFullPath(Path.Combine(outputFolder, Path.ChangeExtension(relative, extension)));
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
