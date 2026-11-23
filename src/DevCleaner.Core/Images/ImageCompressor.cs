using System.Runtime.InteropServices;
using SkiaSharp;

namespace DevCleaner.Core.Images;

/// <summary>
/// Shrinks images with as little visible change as possible. Lossless routes (JPEG repacking, PNG/TIFF re-compression,
/// BMP → PNG, PNG → lossless WebP/JPEG XL) are pixel-verified. Lossy routes pick the lowest quality whose worst-area SSIM
/// against the original stays above the mode's target. Exif/XMP/IPTC/ICC metadata, orientation and motion-photo trailers
/// are kept wherever the output format can hold them.
/// </summary>
public static class ImageCompressor
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

    private readonly record struct QualityRange(int MinQ, int MaxQ, double Target);

    // Decisions use the worst 1% of SSIM windows (see Ssim): mean SSIM is dominated by smooth areas and hides blocking.
    // Quality scales differ per encoder, so each format has its own range; the SSIM target is the same.
    private static QualityRange Params(ImageQualityMode mode, ImageOutputFormat format)
    {
        var smaller = mode == ImageQualityMode.Smaller;
        var target = smaller ? 0.95 : 0.98;
        var (lo, hi) = format switch
        {
            ImageOutputFormat.WebP => smaller ? (50, 95) : (65, 99), // ImageMagick treats WebP quality 100 as lossless
            ImageOutputFormat.Avif => smaller ? (30, 92) : (45, 97),
            ImageOutputFormat.JpegXl => smaller ? (40, 95) : (55, 98), // 100 would be lossless
            _ => smaller ? (60, 92) : (75, 95),
        };
        return new QualityRange(lo, hi, target);
    }

    private const int TileSize = 128;              // multiple of the 16-px MCU so tiles keep the full image's block grid
    private const long FullSearchPixels = 2_500_000;

    /// <summary>
    /// Lowest quality in the range whose worst-case SSIM meets the target. Large images are searched on an evenly spread
    /// mosaic of tiles (fast), then the pick is verified on the whole image. <paramref name="encode"/> gets "true" for the
    /// full image, which is the one that is kept (and gets metadata).
    /// </summary>
    private static (byte[] Data, int Quality, SsimScore Ssim) Search(SKBitmap src, QualityRange p, Func<SKBitmap, int, bool, byte[]> encode,
        Func<byte[], SKBitmap, SKBitmap?> decode, CancellationToken ct)
    {
        var reference = Luma(src);
        var useMosaic = (long)src.Width * src.Height > FullSearchPixels;
        using var mosaic = useMosaic ? Mosaic(src) : null;
        var sample = mosaic ?? src;
        var sampleLuma = mosaic is null ? reference : Luma(mosaic);
        var tried = new Dictionary<int, double>();

        double Try(int q)
        {
            if (tried.TryGetValue(q, out var hit))
                return hit;
            ct.ThrowIfCancellationRequested();
            var data = encode(sample, q, !useMosaic);
            return tried[q] = Measure(data, sample, sampleLuma, decode).Worst;
        }

        int lo = p.MinQ, hi = p.MaxQ;
        if (Try(hi) >= p.Target)
        {
            while (lo < hi)
            {
                var mid = (lo + hi) / 2;
                if (Try(mid) >= p.Target)
                    hi = mid;
                else
                    lo = mid + 1;
            }
        }

        // Verify on the full image; step up if the sample missed a weak area.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var data = encode(src, hi, true);
            var score = Measure(data, src, reference, decode);
            if (score.Worst >= p.Target || hi >= p.MaxQ || !useMosaic)
                return (data, hi, score);
            hi = Math.Min(p.MaxQ, hi + 4);
        }
    }

    private static SsimScore Measure(byte[] encoded, SKBitmap like, byte[] referenceLuma, Func<byte[], SKBitmap, SKBitmap?> decode)
    {
        using var decoded = decode(encoded, like) ?? throw new InvalidDataException("Encoder produced an unreadable image");
        return Ssim.Compute(referenceLuma, Luma(decoded), like.Width, like.Height);
    }

    /// <summary>An evenly spread sample of up to 48 tiles, so its worst-case SSIM tracks the whole image's.</summary>
    private static SKBitmap Mosaic(SKBitmap src)
    {
        int cols = src.Width / TileSize, rows = src.Height / TileSize, cells = cols * rows;
        var count = Math.Min(48, cells);
        // Stride through the cells so the sample covers every row and column band.
        var picked = Enumerable.Range(0, count)
            .Select(i => (long)i * cells / count)
            .Select(c => (X: (int)(c % cols) * TileSize, Y: (int)(c / cols) * TileSize))
            .ToList();

        const int perRow = 8;
        var mw = Math.Min(perRow, picked.Count) * TileSize;
        var mh = (picked.Count + perRow - 1) / perRow * TileSize;
        var bpp = src.BytesPerPixel;
        var buffer = new byte[mw * mh * bpp];
        var px = src.GetPixelSpan();
        for (var i = 0; i < picked.Count; i++)
        {
            int dx = i % perRow * TileSize, dy = i / perRow * TileSize;
            for (var y = 0; y < TileSize; y++)
                px.Slice((picked[i].Y + y) * src.RowBytes + picked[i].X * bpp, TileSize * bpp).CopyTo(buffer.AsSpan(((dy + y) * mw + dx) * bpp));
        }

        var bmp = new SKBitmap(src.Info.WithSize(mw, mh));
        Marshal.Copy(buffer, 0, bmp.GetPixels(), buffer.Length);
        return bmp;
    }

    private static byte[] Encode(SKBitmap bmp, SKJpegEncoderOptions options)
    {
        using var pixmap = bmp.PeekPixels();
        using var data = pixmap.Encode(options) ?? throw new InvalidDataException("JPEG encoding failed");
        return data.ToArray();
    }

    private static byte[] Encode(SKBitmap bmp, SKPngEncoderOptions options)
    {
        using var pixmap = bmp.PeekPixels();
        using var data = pixmap.Encode(options) ?? throw new InvalidDataException("PNG encoding failed");
        return data.ToArray();
    }

    /// <summary>Decodes without colour conversion (null colour space) so re-encoded pixels match the original's values.</summary>
    private static SKBitmap? Decode(byte[] bytes, SKImageInfo? as_, out SKEncodedOrigin origin, out string? error)
    {
        origin = SKEncodedOrigin.TopLeft;
        error = null;
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null)
        {
            error = "Unrecognized or corrupt image";
            return null;
        }

        origin = codec.EncodedOrigin;
        var info = codec.Info;
        if ((long)info.Width * info.Height > MaxPixels)
        {
            error = "Image too large";
            return null;
        }

        var target = as_ ?? new SKImageInfo(
            info.Width,
            info.Height,
            info.ColorType == SKColorType.Gray8 ? SKColorType.Gray8 : SKColorType.Rgba8888,
            info.ColorType == SKColorType.Gray8 || info.AlphaType == SKAlphaType.Opaque ? SKAlphaType.Opaque : SKAlphaType.Unpremul,
            null);
        if (target.Width != info.Width || target.Height != info.Height)
        {
            error = "Size mismatch";
            return null;
        }

        var bmp = new SKBitmap(target);
        var result = codec.GetPixels(target, bmp.GetPixels());
        if (result != SKCodecResult.Success)
        {
            bmp.Dispose();
            error = result == SKCodecResult.IncompleteInput ? "Truncated or corrupt image" : $"Decode failed ({result})";
            return null;
        }

        return bmp;
    }

    private static SKBitmap Resize(SKBitmap src, int maxEdge)
    {
        var scale = (double)maxEdge / Math.Max(src.Width, src.Height);
        var info = src.Info.WithSize(Math.Max(1, (int)Math.Round(src.Width * scale)), Math.Max(1, (int)Math.Round(src.Height * scale)));
        return src.Resize(info, SKFilterQuality.High) ?? throw new InvalidDataException("Resize failed");
    }

    private static bool SamePixels(SKBitmap a, SKBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height || a.ColorType != b.ColorType)
            return false;
        var rowLength = a.Width * a.BytesPerPixel;
        var pa = a.GetPixelSpan();
        var pb = b.GetPixelSpan();
        for (var y = 0; y < a.Height; y++)
        {
            if (!pa.Slice(y * a.RowBytes, rowLength).SequenceEqual(pb.Slice(y * b.RowBytes, rowLength)))
                return false;
        }

        return true;
    }

    private static byte[] Luma(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, rb = bmp.RowBytes;
        var px = bmp.GetPixelSpan();
        var y = new byte[w * h];
        if (bmp.ColorType == SKColorType.Gray8)
        {
            for (var r = 0; r < h; r++)
                px.Slice(r * rb, w).CopyTo(y.AsSpan(r * w));
            return y;
        }

        for (var r = 0; r < h; r++)
        {
            var row = px.Slice(r * rb, w * 4);
            var o = r * w;
            for (var x = 0; x < w; x++)
                y[o + x] = (byte)((77 * row[4 * x] + 150 * row[4 * x + 1] + 29 * row[4 * x + 2]) >> 8);
        }

        return y;
    }

    /// <summary>Scales down to <paramref name="maxEdge"/>, applies the EXIF orientation and returns RGBA bytes.</summary>
    private static PreviewImage ToPreview(SKBitmap bmp, SKEncodedOrigin origin, int maxEdge)
    {
        var scaled = maxEdge > 0 && Math.Max(bmp.Width, bmp.Height) > maxEdge ? Resize(bmp, maxEdge) : null;
        try
        {
            var (pixels, w, h) = OrientedPixels(scaled ?? bmp, origin);
            return new PreviewImage(w, h, MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray());
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>Upright RGBA8888 copy (Gray8 expanded); returns <paramref name="bmp"/> itself when nothing changes.</summary>
    private static SKBitmap Orient(SKBitmap bmp, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft && bmp.ColorType == SKColorType.Rgba8888)
            return bmp;
        var (pixels, w, h) = OrientedPixels(bmp, origin);
        var alpha = bmp.ColorType == SKColorType.Gray8 ? SKAlphaType.Opaque : bmp.AlphaType;
        var result = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, alpha, null));
        var bytes = MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();
        Marshal.Copy(bytes, 0, result.GetPixels(), bytes.Length);
        return result;
    }

    /// <summary>RGBA pixels (one uint each) rotated/mirrored per EXIF orientation.</summary>
    private static (uint[] Pixels, int Width, int Height) OrientedPixels(SKBitmap src, SKEncodedOrigin origin)
    {
        int w = src.Width, h = src.Height, rb = src.RowBytes;
        var px = src.GetPixelSpan();
        var gray = src.ColorType == SKColorType.Gray8;
        var natural = new uint[w * h];
        var bytes = MemoryMarshal.AsBytes(natural.AsSpan());
        for (var r = 0; r < h; r++)
        {
            if (gray)
            {
                var row = px.Slice(r * rb, w);
                for (var x = 0; x < w; x++)
                {
                    var i = (r * w + x) * 4;
                    bytes[i] = bytes[i + 1] = bytes[i + 2] = row[x];
                    bytes[i + 3] = 255;
                }
            }
            else
            {
                px.Slice(r * rb, w * 4).CopyTo(bytes.Slice(r * w * 4));
                if (src.AlphaType == SKAlphaType.Opaque)
                {
                    for (var x = 0; x < w; x++)
                        bytes[(r * w + x) * 4 + 3] = 255;
                }
            }
        }

        if (origin == SKEncodedOrigin.TopLeft)
            return (natural, w, h);
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int dw = swap ? h : w, dh = swap ? w : h;
        var oriented = new uint[w * h];
        for (var sy = 0; sy < h; sy++)
        {
            for (var sx = 0; sx < w; sx++)
            {
                var (dx, dy) = origin switch
                {
                    SKEncodedOrigin.TopRight => (w - 1 - sx, sy),
                    SKEncodedOrigin.BottomRight => (w - 1 - sx, h - 1 - sy),
                    SKEncodedOrigin.BottomLeft => (sx, h - 1 - sy),
                    SKEncodedOrigin.LeftTop => (sy, sx),
                    SKEncodedOrigin.RightTop => (h - 1 - sy, sx),
                    SKEncodedOrigin.RightBottom => (h - 1 - sy, w - 1 - sx),
                    SKEncodedOrigin.LeftBottom => (sy, w - 1 - sx),
                    _ => (sx, sy),
                };
                oriented[dy * dw + dx] = natural[sy * w + sx];
            }
        }

        return (oriented, dw, dh);
    }
}
internal readonly record struct SsimScore(double Mean, double Worst);

/// <summary>
/// SSIM on luma over 8×8 windows offset by 4 px, so every window straddles a JPEG block edge and sees blocking.
/// Reports the mean and the 1st percentile (worst areas).
/// </summary>
internal static class Ssim
{
    private const double C1 = 6.5025;   // (0.01 * 255)^2
    private const double C2 = 58.5225;  // (0.03 * 255)^2

    public static SsimScore Compute(byte[] a, byte[] b, int w, int h)
    {
        var bs = Math.Max(1, Math.Min(8, Math.Min(w, h)));
        var off = Math.Min(w, h) >= 16 ? 4 : 0;
        int bx = (w - off) / bs, by = (h - off) / bs;
        if (bx * by == 0)
            return new SsimScore(1, 1);
        double n = bs * bs, total = 0;
        var scores = new double[bx * by];
        for (var y0 = 0; y0 < by; y0++)
        {
            for (var x0 = 0; x0 < bx; x0++)
            {
                long sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
                for (var y = 0; y < bs; y++)
                {
                    var row = (off + y0 * bs + y) * w + off + x0 * bs;
                    for (var x = 0; x < bs; x++)
                    {
                        int va = a[row + x], vb = b[row + x];
                        sa += va;
                        sb += vb;
                        saa += va * va;
                        sbb += vb * vb;
                        sab += va * vb;
                    }
                }

                double ma = sa / n, mb = sb / n;
                double varA = saa / n - ma * ma, varB = sbb / n - mb * mb, cov = sab / n - ma * mb;
                var s = (2 * ma * mb + C1) * (2 * cov + C2) / ((ma * ma + mb * mb + C1) * (varA + varB + C2));
                scores[y0 * bx + x0] = s;
                total += s;
            }
        }

        Array.Sort(scores);
        return new SsimScore(total / scores.Length, scores[scores.Length / 100]);
    }
}
