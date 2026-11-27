using System.Runtime.InteropServices;
using SkiaSharp;

namespace DevCleaner.Core.Images;

/// <summary>
/// Shrinks images with as little visible change as possible. Lossless routes (JPEG repacking, PNG/TIFF re-compression,
/// BMP → PNG, PNG → lossless WebP/JPEG XL) are pixel-verified. Lossy routes pick the lowest quality whose worst-area SSIM
/// against the original stays above the mode's target. Exif/XMP/IPTC/ICC metadata, orientation and motion-photo trailers
/// are kept wherever the output format can hold them.
/// </summary>
public static partial class ImageCompressor
{
    private const long MaxFileBytes = 512L * 1024 * 1024;
    private const long MaxPixels = 200_000_000;

    /// <summary>Workers for batch analysis: each holds a few decoded copies of a photo, so memory caps this, not cores.</summary>
    public static int DefaultParallelism => Math.Clamp(Environment.ProcessorCount / 2, 1, 6);

    public static ImageAnalysis Analyze(string path, ImageCompressionOptions options, CancellationToken ct = default) =>
        Run(path, options, previewEdge: null, ct).Analysis;

    /// <summary>The bytes to write (null unless the result is <see cref="ImageAnalysisStatus.Compressible"/>).</summary>
    public static (ImageAnalysis Analysis, byte[]? Data) Compress(string path, ImageCompressionOptions options, CancellationToken ct = default)
    {
        var r = Run(path, options, previewEdge: null, ct);
        return (r.Analysis, r.Data);
    }

    /// <summary>Analysis plus decoded original and compressed pixels for a side-by-side view.</summary>
    public static ImagePreview Preview(string path, ImageCompressionOptions options, int maxEdge = 6000, CancellationToken ct = default)
    {
        var r = Run(path, options, maxEdge, ct);
        return new ImagePreview(r.Analysis, r.Original, r.Compressed);
    }

    public static ImageKind? KindFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jpe" or ".jfif" => ImageKind.Jpeg,
        ".png" => ImageKind.Png,
        ".bmp" or ".dib" => ImageKind.Bmp,
        ".tif" or ".tiff" => ImageKind.Tiff,
        ".heic" or ".heif" or ".hif" => ImageKind.Heic,
        _ => null,
    };

    /// <summary>Who can open files in this format; null for formats that open everywhere.</summary>
    public static string? CompatibilityNote(ImageOutputFormat format) => format switch
    {
        ImageOutputFormat.WebP =>
            "WebP opens in all current browsers, Windows 10/11 Photos, macOS, Android and iOS 14+, but some older photo editors, printers, photo kiosks and TVs cannot read it. Files get the .webp extension.",
        ImageOutputFormat.Avif =>
            "AVIF needs Windows 11 or the AV1 Video Extension, macOS 13 / iOS 16 or newer, and a current browser. Many photo editors, printers and older devices cannot open it. Files get the .avif extension.",
        ImageOutputFormat.JpegXl =>
            "JPEG XL has very limited support: Safari and macOS 14 / iOS 17+, Windows 11 with the JPEG XL extension, and a few editors. Chrome and Edge cannot show it. Use it only for archives you open with compatible software. Files get the .jxl extension.",
        _ => null,
    };

    private sealed record Outcome(ImageAnalysis Analysis, byte[]? Data = null, PreviewImage? Original = null, PreviewImage? Compressed = null);

    private enum Route
    {
        JpegLossy,
        JpegRepack,
        Png,
        ConvertLossless,
        Convert,
        TiffRepack,
    }

    private static Outcome Fail(ImageAnalysisStatus status, long size, string message, int? quality = null) =>
        new(new ImageAnalysis { Status = status, OriginalSize = size, CompressedSize = size, OriginalQuality = quality, Message = message });

    private static Outcome Run(string path, ImageCompressionOptions o, int? previewEdge, CancellationToken ct)
    {
        try
        {
            return RunCore(path, o, previewEdge, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One unreadable or exotic file must not stop a batch.
            return Fail(ImageAnalysisStatus.Failed, 0, ex.Message);
        }
    }

    /// <summary>Which encoder handles this source under these options (or why none does).</summary>
    private static (Route Route, string? Unsupported) Plan(ImageKind kind, ImageCompressionOptions o)
    {
        var lossy = o.Mode != ImageQualityMode.Lossless;
        var f = o.Format;
        return kind switch
        {
            ImageKind.Jpeg => (!lossy ? Route.JpegRepack : f is ImageOutputFormat.Original or ImageOutputFormat.Jpeg ? Route.JpegLossy : Route.Convert, null),
            ImageKind.Png or ImageKind.Bmp => (f is ImageOutputFormat.WebP or ImageOutputFormat.JpegXl ? Route.ConvertLossless : Route.Png, null),
            ImageKind.Tiff => (!lossy || f == ImageOutputFormat.Original ? Route.TiffRepack : Route.Convert, null),
            _ => !lossy
                ? (Route.Convert, "HEIC cannot shrink losslessly; pick a lossy mode and WebP, AVIF or JPEG XL output")
                : f == ImageOutputFormat.Original
                    ? (Route.Convert, "HEIC is already compact; pick WebP, AVIF or JPEG XL output to convert it")
                    : (Route.Convert, null),
        };
    }

    private static Outcome RunCore(string path, ImageCompressionOptions o, int? previewEdge, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            var length = new FileInfo(path).Length;
            if (length > MaxFileBytes)
                return Fail(ImageAnalysisStatus.Unsupported, length, "Larger than 512 MB");
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Fail(ImageAnalysisStatus.Failed, 0, ex.Message);
        }

        ct.ThrowIfCancellationRequested();
        if (DetectKind(bytes) is not { } kind)
            return Fail(ImageAnalysisStatus.Failed, bytes.Length, "Not a JPEG, PNG, BMP, TIFF or HEIC file");
        var jpeg = kind == ImageKind.Jpeg ? JpegInfo.Parse(bytes) : null;
        if (jpeg?.UnsupportedReason is { } reason)
            return Fail(ImageAnalysisStatus.Unsupported, bytes.Length, reason, jpeg.Quality);
        if (jpeg?.HasMpf == true && !o.DropEmbeddedExtras)
            return Fail(ImageAnalysisStatus.Unsupported, bytes.Length, "Has an embedded HDR gain map or depth map that would be lost", jpeg.Quality);
        if (kind == ImageKind.Png && PngChunks.BitDepth(bytes) == 16)
            return Fail(ImageAnalysisStatus.Unsupported, bytes.Length, "16-bit PNG (would lose precision)");

        var (route, unsupported) = Plan(kind, o);
        if (route == Route.Convert && jpeg?.Trailer is not null && !o.DropEmbeddedExtras)
            unsupported = "Motion photo: its video can only be kept in JPEG output";
        if (route == Route.TiffRepack)
            return RepackTiff(bytes, Path.GetExtension(path), o, previewEdge, ct);

        // Decode. Skia handles JPEG/PNG/BMP without colour conversion; TIFF/HEIC come upright from ImageMagick.
        var origin = SKEncodedOrigin.TopLeft;
        ImageMetadata? metadata = null;
        string? error;
        var pages = 1;
        SKBitmap? decoded;
        if (kind is ImageKind.Tiff or ImageKind.Heic)
        {
            decoded = MagickCodec.DecodeSource(bytes, kind, MaxPixels, out var read, out pages, out error);
            metadata = read;
        }
        else
        {
            decoded = Decode(bytes, null, out origin, out error);
        }

        using var source = decoded;
        if (source is null)
            return Fail(ImageAnalysisStatus.Failed, bytes.Length, error ?? "Cannot decode", jpeg?.Quality);
        if (pages > 1 && unsupported is null)
            unsupported = $"Multi-page TIFF ({pages} pages) can only be re-packed in its original format";

        var baseline = new ImageAnalysis
        {
            Status = ImageAnalysisStatus.Unsupported,
            OriginalSize = bytes.Length,
            CompressedSize = bytes.Length,
            Width = source.Width,
            Height = source.Height,
            OutputWidth = source.Width,
            OutputHeight = source.Height,
            OriginalQuality = jpeg?.Quality,
        };
        var originalPreview = previewEdge is { } edge ? ToPreview(source, origin, edge) : null;
        if (unsupported is not null)
            return new Outcome(baseline with { Message = unsupported }, null, originalPreview);

        var lossy = o.Mode != ImageQualityMode.Lossless;
        var resized = lossy && o.MaxDimension > 0 && Math.Max(source.Width, source.Height) > o.MaxDimension;
        var work = resized ? Resize(source, o.MaxDimension) : source;
        var owned = new List<SKBitmap>();
        if (resized)
            owned.Add(work);
        try
        {
            ct.ThrowIfCancellationRequested();
            byte[] data = [];
            int? quality = null;
            double? ssim = null, worst = null;
            string? blocked = null;
            var lossless = false;
            var outFormat = o.Format;
            var outOrigin = origin;
            string extension, formatName;

            // Converted output is written upright (AVIF readers ignore Exif orientation), so orient before encoding.
            if (route is Route.Convert or Route.ConvertLossless)
            {
                var upright = Orient(work, origin);
                if (!ReferenceEquals(upright, work))
                {
                    owned.Add(upright);
                    work = upright;
                }

                outOrigin = SKEncodedOrigin.TopLeft;
                metadata ??= o.KeepMetadata ? ImageMetadata.Read(bytes) : null;
                if (!o.KeepMetadata)
                    metadata = null;
            }

            if (route == Route.ConvertLossless)
            {
                data = MagickCodec.Encode(work, outFormat, 100, lossless: true, metadata);
                using var check = MagickCodec.Decode(data, work);
                if (check is not null && SamePixels(work, check))
                {
                    lossless = !resized;
                    ssim = worst = resized ? null : 1;
                }
                else
                {
                    route = Route.Png; // fall back to the safe, verified PNG route
                }
            }

            switch (route)
            {
                case Route.JpegLossy:
                {
                    var p = Params(o.Mode, ImageOutputFormat.Jpeg);
                    // Re-encoding above the original's quality only grows the file.
                    if (jpeg!.Quality is { } estimated)
                        p = p with { MaxQ = Math.Clamp(estimated, p.MinQ, p.MaxQ) };
                    var downsample = jpeg.Subsampling switch
                    {
                        ChromaSubsampling.Yuv444 => SKJpegEncoderDownsample.Downsample444,
                        ChromaSubsampling.Yuv422 => SKJpegEncoderDownsample.Downsample422,
                        _ => SKJpegEncoderDownsample.Downsample420,
                    };
                    (data, var picked, var score) = Search(work, p, (b, q, _) => Encode(b, new SKJpegEncoderOptions(q, downsample, SKJpegEncoderAlphaOption.Ignore)),
                        (d, like) => Decode(d, like.Info, out _, out _), ct);
                    (quality, ssim, worst) = (picked, score.Mean, score.Worst);
                    if (score.Worst < p.Target)
                        blocked = "Would lose visible detail even at the highest quality";
                    // Free extra: optimal Huffman tables + progressive coding of our own output.
                    data = MagickCodec.RepackJpeg(data, SameJpegPixels) ?? data;
                    data = FinishJpeg(data, jpeg, o);

                    // Lossy not worth it (or not allowed by the target)? A pure lossless repack may still be.
                    if ((blocked is not null || !Worth(data.Length, bytes.Length, o)) && !resized
                        && MagickCodec.RepackJpeg(bytes, SameJpegPixels) is { } repacked
                        && FinishJpeg(JpegInfo.StripMetadata(repacked), jpeg, o) is var r && r.Length < bytes.Length && (blocked is not null || r.Length < data.Length))
                    {
                        (data, quality, ssim, worst, blocked, lossless) = (r, null, 1, 1, null, true);
                    }

                    (extension, formatName) = (Path.GetExtension(path), "JPEG");
                    break;
                }

                case Route.JpegRepack:
                {
                    var repacked = MagickCodec.RepackJpeg(bytes, SameJpegPixels);
                    data = repacked is null ? bytes : FinishJpeg(JpegInfo.StripMetadata(repacked), jpeg!, o);
                    (ssim, worst, lossless) = (1, 1, true);
                    (extension, formatName) = (Path.GetExtension(path), "JPEG");
                    break;
                }

                case Route.Convert:
                {
                    if (outFormat is ImageOutputFormat.Original)
                        outFormat = ImageOutputFormat.Jpeg;
                    var p = Params(o.Mode, outFormat);
                    var target = outFormat;
                    var meta = metadata;
                    (data, var picked, var score) = Search(work, p, (b, q, full) => MagickCodec.Encode(b, target, q, lossless: false, full ? meta : null),
                        MagickCodec.Decode, ct);
                    (quality, ssim, worst) = (picked, score.Mean, score.Worst);
                    if (score.Worst < p.Target)
                        blocked = "Would lose visible detail even at the highest quality";
                    (extension, formatName) = (MagickCodec.Extension(outFormat), MagickCodec.Name(outFormat));
                    break;
                }

                case Route.ConvertLossless:
                    (extension, formatName) = (MagickCodec.Extension(outFormat), MagickCodec.Name(outFormat));
                    break;

                default: // Route.Png
                {
                    data = Encode(work, new SKPngEncoderOptions(SKPngEncoderFilterFlags.AllFilters, 9));
                    using (var check = Decode(data, work.Info, out _, out _))
                    {
                        if (check is null || !SamePixels(work, check))
                            return new Outcome(baseline with { Status = ImageAnalysisStatus.Failed, Message = "Lossless verification failed" }, null, originalPreview);
                    }

                    if (o.KeepMetadata && kind == ImageKind.Png)
                        data = PngChunks.CopyMetadata(bytes, data);
                    ssim = worst = resized ? null : 1;
                    lossless = !resized;
                    (extension, formatName) = (kind == ImageKind.Bmp ? ".png" : Path.GetExtension(path), "PNG");
                    break;
                }
            }

            ct.ThrowIfCancellationRequested();
            var worthIt = blocked is null && Worth(data.Length, bytes.Length, o);
            var analysis = baseline with
            {
                Status = worthIt ? ImageAnalysisStatus.Compressible : ImageAnalysisStatus.NoGain,
                CompressedSize = data.Length,
                OutputWidth = work.Width,
                OutputHeight = work.Height,
                Quality = quality,
                Ssim = ssim,
                SsimWorst = worst,
                Lossless = lossless,
                OutputFormatName = formatName,
                OutputExtension = extension,
                Message = worthIt ? null : blocked ?? (data.Length >= bytes.Length ? "Already optimal" : $"Saves under {o.MinSavingFraction:P0}"),
            };

            PreviewImage? compressedPreview = null;
            if (previewEdge is { } e)
            {
                using var output = DecodeOutput(data, work);
                compressedPreview = output is null ? null : ToPreview(output, outOrigin, e);
            }

            return new Outcome(analysis, worthIt ? data : null, originalPreview, compressedPreview);
        }
        finally
        {
            foreach (var b in owned)
                b.Dispose();
        }
    }

    private static bool Worth(long newSize, long originalSize, ImageCompressionOptions o) => newSize <= originalSize * (1 - o.MinSavingFraction);

    /// <summary>Curated metadata back in; motion-photo/vendor trailer appended (it is addressed from the end of the file).</summary>
    private static byte[] FinishJpeg(byte[] data, JpegInfo original, ImageCompressionOptions o)
    {
        if (o.KeepMetadata)
            data = original.InsertMetadata(data);
        return original.HasMpf ? data : original.AppendTrailer(data);
    }

    private static bool SameJpegPixels(byte[] a, byte[] b)
    {
        using var da = Decode(a, null, out _, out _);
        using var db = da is null ? null : Decode(b, da.Info, out _, out _);
        return da is not null && db is not null && SamePixels(da, db);
    }

    private static SKBitmap? DecodeOutput(byte[] data, SKBitmap like) =>
        Decode(data, like.Info, out _, out _) ?? MagickCodec.Decode(data, like);

    /// <summary>TIFF stays TIFF: every page re-compressed with ZIP, bit depth kept, verified frame by frame.</summary>
    private static Outcome RepackTiff(byte[] bytes, string extension, ImageCompressionOptions o, int? previewEdge, CancellationToken ct)
    {
        using var source = MagickCodec.DecodeSource(bytes, ImageKind.Tiff, MaxPixels, out _, out _, out var error);
        if (source is null)
            return Fail(ImageAnalysisStatus.Failed, bytes.Length, error ?? "Cannot decode");
        var (data, problem) = MagickCodec.RepackTiff(bytes, ct);
        var baseline = new ImageAnalysis
        {
            Status = ImageAnalysisStatus.Unsupported,
            OriginalSize = bytes.Length,
            CompressedSize = data?.Length ?? bytes.Length,
            Width = source.Width,
            Height = source.Height,
            OutputWidth = source.Width,
            OutputHeight = source.Height,
            Lossless = true,
            Ssim = 1,
            SsimWorst = 1,
            OutputFormatName = "TIFF",
            OutputExtension = extension,
        };
        var preview = previewEdge is { } e ? ToPreview(source, SKEncodedOrigin.TopLeft, e) : null;
        if (data is null)
            return new Outcome(baseline with { Message = problem }, null, preview);
        var worthIt = Worth(data.Length, bytes.Length, o);
        var analysis = baseline with
        {
            Status = worthIt ? ImageAnalysisStatus.Compressible : ImageAnalysisStatus.NoGain,
            Message = worthIt ? null : data.Length >= bytes.Length ? "Already optimal" : $"Saves under {o.MinSavingFraction:P0}",
        };
        return new Outcome(analysis, worthIt ? data : null, preview, preview);
    }

    private static ImageKind? DetectKind(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
            return ImageKind.Jpeg;
        if (PngChunks.IsPng(d))
            return ImageKind.Png;
        if (d.Length >= 26 && d[0] == (byte)'B' && d[1] == (byte)'M')
            return ImageKind.Bmp;
        if (d.Length >= 8 && ((d[0] == 'I' && d[1] == 'I' && d[2] is 42 or 43 && d[3] == 0) || (d[0] == 'M' && d[1] == 'M' && d[2] == 0 && d[3] is 42 or 43)))
            return ImageKind.Tiff;
        if (d.Length >= 12 && d.Slice(4, 4).SequenceEqual("ftyp"u8)
            && System.Text.Encoding.ASCII.GetString(d.Slice(8, 4)) is "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1" or "avif" or "avis")
            return ImageKind.Heic;
        return null;
    }
}
