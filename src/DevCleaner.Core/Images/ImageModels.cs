namespace DevCleaner.Core.Images;

public enum ImageKind
{
    Jpeg,
    Png,
    Bmp,
    Tiff,

    /// <summary>HEIC/HEIF (iPhone photos). Read only: HEVC cannot be encoded, so these are only converted.</summary>
    Heic,
}

/// <summary>Format of the written files.</summary>
public enum ImageOutputFormat
{
    /// <summary>JPEG stays JPEG, PNG stays PNG, TIFF is re-packed losslessly, BMP becomes PNG. HEIC is not touched.</summary>
    Original,

    /// <summary>Like <see cref="Original"/>, but TIFF and HEIC photos become JPEG (the most compatible choice).</summary>
    Jpeg,

    /// <summary>Lossy WebP for photos, lossless WebP for PNG/BMP.</summary>
    WebP,

    /// <summary>Lossy AVIF for photos; PNG/BMP stay lossless PNG (AVIF lossless is not competitive).</summary>
    Avif,

    /// <summary>Lossy JPEG XL for photos, lossless JPEG XL for PNG/BMP.</summary>
    JpegXl,
}

public enum ImageQualityMode
{
    /// <summary>Pixel-identical output only: JPEG Huffman/progressive repacking, PNG/TIFF re-compression, BMP → PNG.</summary>
    Lossless,

    /// <summary>JPEGs re-encoded at the lowest quality whose worst-area SSIM stays ≥ 0.98 (q75–95); PNG/BMP lossless.</summary>
    VisuallyLossless,

    /// <summary>More aggressive JPEG target (worst-area SSIM ≥ 0.95, q60–92); PNG/BMP lossless.</summary>
    Smaller,
}

public sealed record ImageCompressionOptions
{
    public ImageQualityMode Mode { get; init; } = ImageQualityMode.VisuallyLossless;

    public ImageOutputFormat Format { get; init; } = ImageOutputFormat.Original;

    /// <summary>Downscale so the longest edge is at most this many pixels; 0 keeps the original resolution. Ignored in lossless mode.</summary>
    public int MaxDimension { get; init; }

    /// <summary>Results that save less than this fraction of the original are reported as <see cref="ImageAnalysisStatus.NoGain"/>.</summary>
    public double MinSavingFraction { get; init; } = 0.10;

    /// <summary>Copy EXIF, XMP, IPTC, ICC profiles and text chunks from the original.</summary>
    public bool KeepMetadata { get; init; } = true;

    /// <summary>
    /// Allow compressing JPEGs with an MPF index (Ultra HDR gain map, depth map, burst frames); those extra images are
    /// dropped. Off by default: such files are reported as unsupported. Motion-photo videos are always kept.
    /// </summary>
    public bool DropEmbeddedExtras { get; init; }
}

public sealed record ImageScanOptions
{
    public IReadOnlyList<string> Roots { get; init; } = [];
    public long MinSize { get; init; } = 200 * 1024;
    public bool IncludePng { get; init; } = true;
    public bool IncludeBmp { get; init; } = true;
    public bool IncludeTiff { get; init; } = true;
    public bool IncludeHeic { get; init; } = true;
    public PathMatcher? Exclusions { get; init; }

    /// <summary>Return true to drop a file (e.g. already optimized by an earlier run).</summary>
    public Func<FileInfo, bool>? Skip { get; init; }

    public int MaxResults { get; init; } = 100_000;
}

public sealed record ImageCandidate(string Path, string Root, ImageKind Kind, long SizeBytes, DateTime LastWriteUtc)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

public enum ImageAnalysisStatus
{
    /// <summary>Saves at least <see cref="ImageCompressionOptions.MinSavingFraction"/>.</summary>
    Compressible,

    /// <summary>Already well compressed.</summary>
    NoGain,

    /// <summary>Format variant we do not touch (CMYK/12-bit JPEG, 16-bit PNG, lossless mode for JPEG, ...).</summary>
    Unsupported,

    /// <summary>Could not read or decode.</summary>
    Failed,
}

public sealed record ImageAnalysis
{
    public required ImageAnalysisStatus Status { get; init; }
    public long OriginalSize { get; init; }
    public long CompressedSize { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int OutputWidth { get; init; }
    public int OutputHeight { get; init; }

    /// <summary>JPEG quality used for the output (null for lossless output).</summary>
    public int? Quality { get; init; }

    /// <summary>Estimated quality of the original JPEG from its quantization tables.</summary>
    public int? OriginalQuality { get; init; }

    /// <summary>Mean structural similarity vs. the original (1 = identical).</summary>
    public double? Ssim { get; init; }

    /// <summary>SSIM of the worst 1% of the image: where artefacts would show first.</summary>
    public double? SsimWorst { get; init; }

    public bool Lossless { get; init; }

    /// <summary>Display name of the output format: JPEG, PNG, TIFF, WebP, AVIF or JPEG XL.</summary>
    public string OutputFormatName { get; init; } = "";

    /// <summary>Extension of the output file, including the dot (".png" when a BMP is converted).</summary>
    public string OutputExtension { get; init; } = "";

    public string? Message { get; init; }

    public long SavedBytes => Status == ImageAnalysisStatus.Compressible ? OriginalSize - CompressedSize : 0;
    public double SavedFraction => OriginalSize > 0 ? (double)(OriginalSize - CompressedSize) / OriginalSize : 0;
}

/// <summary>RGBA8888 (unpremultiplied) pixels, already rotated per EXIF orientation.</summary>
public sealed record PreviewImage(int Width, int Height, byte[] Rgba);

public sealed record ImagePreview(ImageAnalysis Analysis, PreviewImage? Original, PreviewImage? Compressed);

public enum ImageOriginalsMode
{
    RecycleBin,
    DeletePermanently,

    /// <summary>Leave originals untouched and write compressed copies to <see cref="ImageApplyOptions.OutputFolder"/>.</summary>
    SaveCopies,
}

public sealed record ImageApplyOptions
{
    public ImageCompressionOptions Compression { get; init; } = new();
    public ImageOriginalsMode Originals { get; init; } = ImageOriginalsMode.RecycleBin;
    public string? OutputFolder { get; init; }
    public bool DryRun { get; init; }
}

public enum ImageApplyStatus
{
    Compressed,
    DryRun,
    NoGain,
    Skipped,
    Failed,
}

public sealed record ImageApplyResult(ImageCandidate Candidate, ImageApplyStatus Status, long OriginalSize, long NewSize, string? OutputPath = null, string? Message = null)
{
    public long SavedBytes => Status is ImageApplyStatus.Compressed or ImageApplyStatus.DryRun ? Math.Max(0, OriginalSize - NewSize) : 0;
}

public sealed record ImageApplyProgress(int Done, int Total, ImageCandidate Current);
