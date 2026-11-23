using System.Runtime.InteropServices;
using ImageMagick;
using SkiaSharp;

namespace DevCleaner.Core.Images;

/// <summary>Exif/ICC/XMP/IPTC of an original, re-attached to converted output.</summary>
internal sealed record ImageMetadata(IExifProfile? Exif, IColorProfile? Icc, IXmpProfile? Xmp, IIptcProfile? Iptc)
{
    public static readonly ImageMetadata Empty = new(null, null, null, null);

    public static ImageMetadata From(IMagickImage<ushort> img) =>
        new(img.GetExifProfile(), img.GetColorProfile(), img.GetXmpProfile(), img.GetIptcProfile());

    /// <summary>Reads only the headers (no pixel decode).</summary>
    public static ImageMetadata Read(byte[] bytes)
    {
        try
        {
            using var img = new MagickImage();
            img.Ping(bytes);
            return From(img);
        }
        catch (MagickException)
        {
            return Empty;
        }
    }

    /// <summary>Output pixels are already upright, so the orientation tag is reset to "top-left".</summary>
    public void ApplyTo(IMagickImage<ushort> img)
    {
        if (Exif is not null)
        {
            Exif.SetValue(ExifTag.Orientation, (ushort)1);
            img.SetProfile(Exif);
        }

        if (Icc is not null)
            img.SetProfile(Icc);
        if (Xmp is not null)
            img.SetProfile(Xmp);
        if (Iptc is not null)
            img.SetProfile(Iptc);
        img.Orientation = OrientationType.TopLeft;
    }
}

/// <summary>
/// ImageMagick (Magick.NET) side of the pipeline: TIFF/HEIC decoding, WebP/AVIF/JPEG XL/JPEG encoding, lossless JPEG
/// repacking and lossless TIFF re-compression. Pixels are exchanged with SkiaSharp as raw 8-bit RGBA.
/// </summary>
internal static class MagickCodec
{
    static MagickCodec()
    {
        // GPU kernels are not worth their start-up cost here, and they vary between machines.
        OpenCL.IsEnabled = false;
    }

    public static string Name(ImageOutputFormat f) => f switch
    {
        ImageOutputFormat.WebP => "WebP",
        ImageOutputFormat.Avif => "AVIF",
        ImageOutputFormat.JpegXl => "JPEG XL",
        _ => "JPEG",
    };

    public static string Extension(ImageOutputFormat f) => f switch
    {
        ImageOutputFormat.WebP => ".webp",
        ImageOutputFormat.Avif => ".avif",
        ImageOutputFormat.JpegXl => ".jxl",
        _ => ".jpg",
    };

    private static MagickFormat Format(ImageOutputFormat f) => f switch
    {
        ImageOutputFormat.WebP => MagickFormat.WebP,
        ImageOutputFormat.Avif => MagickFormat.Avif,
        ImageOutputFormat.JpegXl => MagickFormat.Jxl,
        _ => MagickFormat.Jpeg,
    };

    public static byte[] Encode(SKBitmap bmp, ImageOutputFormat format, int quality, bool lossless, ImageMetadata? metadata)
    {
        using var img = ToMagick(bmp);
        img.Quality = lossless ? 100u : (uint)Math.Clamp(quality, 1, 100);
        var s = img.Settings;
        switch (format)
        {
            case ImageOutputFormat.WebP:
                s.SetDefine(MagickFormat.WebP, "method", "5");
                s.SetDefine(MagickFormat.WebP, "use-sharp-yuv", "true"); // sharper colour edges than plain 4:2:0
                if (lossless)
                {
                    s.SetDefine(MagickFormat.WebP, "lossless", "true");
                    s.SetDefine(MagickFormat.WebP, "exact", "true"); // keep colour under fully transparent pixels
                }

                break;
            case ImageOutputFormat.Avif:
                s.SetDefine(MagickFormat.Heic, "speed", "8");
                break;
            case ImageOutputFormat.JpegXl:
                s.SetDefine(MagickFormat.Jxl, "effort", lossless ? "7" : "5");
                break;
            default:
                s.SetDefine(MagickFormat.Jpeg, "optimize-coding", "true");
                s.Interlace = Interlace.Jpeg;
                break;
        }

        metadata?.ApplyTo(img);
        return img.ToByteArray(Format(format));
    }

    /// <summary>Decodes into the same layout as <paramref name="like"/> (RGBA8888, same size and alpha type).</summary>
    public static SKBitmap? Decode(byte[] data, SKBitmap like)
    {
        using var img = new MagickImage(data);
        if (img.Width != like.Width || img.Height != like.Height)
            return null;
        return ToSkia(img, like.AlphaType);
    }

    /// <summary>
    /// Decodes a TIFF (first page, upright) or HEIC (libheif already applies its rotation/mirror boxes) to RGBA8888.
    /// </summary>
    public static SKBitmap? DecodeSource(byte[] bytes, ImageKind kind, long maxPixels, out ImageMetadata metadata, out int pages, out string? error)
    {
        metadata = ImageMetadata.Empty;
        pages = 1;
        error = null;
        try
        {
            if (kind == ImageKind.Tiff)
            {
                using var all = new MagickImageCollection();
                all.Ping(bytes);
                pages = all.Count;
            }

            var info = new MagickImageInfo(bytes);
            if ((long)info.Width * info.Height > maxPixels)
            {
                error = "Image too large";
                return null;
            }

            using var img = new MagickImage(bytes);
            if (img.ColorSpace == ColorSpace.CMYK)
            {
                error = "CMYK image (print colour space)";
                return null;
            }

            if (kind == ImageKind.Tiff)
                img.AutoOrient();
            metadata = ImageMetadata.From(img);
            return ToSkia(img, img.HasAlpha ? SKAlphaType.Unpremul : SKAlphaType.Opaque);
        }
        catch (MagickException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// jpegtran-style lossless optimisation (optimal Huffman tables, progressive). Returns null when it does not help or
    /// the decoded pixels would differ.
    /// </summary>
    public static byte[]? RepackJpeg(byte[] jpeg, Func<byte[], byte[], bool> samePixels)
    {
        try
        {
            using var ms = new MemoryStream();
            ms.Write(jpeg);
            ms.Position = 0;
            if (!new ImageOptimizer { OptimalCompression = true }.LosslessCompress(ms))
                return null;
            var repacked = ms.ToArray();
            return repacked.Length < jpeg.Length && samePixels(jpeg, repacked) ? repacked : null;
        }
        catch (MagickException)
        {
            return null;
        }
    }

    /// <summary>Every page re-compressed with ZIP + horizontal predictor; bit depth, alpha and profiles are kept.</summary>
    public static (byte[]? Data, string? Problem) RepackTiff(byte[] bytes, CancellationToken ct)
    {
        try
        {
            using var original = new MagickImageCollection(bytes);
            if (original.Any(f => f.Depth > 16))
                return (null, "32-bit or floating-point TIFF");
            using var copy = original.Clone();
            foreach (var frame in copy)
            {
                frame.Settings.Compression = CompressionMethod.Zip;
                frame.Settings.SetDefine(MagickFormat.Tiff, "predictor", "2");
            }

            var data = copy.ToByteArray(MagickFormat.Tiff);
            ct.ThrowIfCancellationRequested();
            using var back = new MagickImageCollection(data);
            if (back.Count != original.Count)
                return (null, "Page count changed");
            for (var i = 0; i < back.Count; i++)
            {
                var (a, b) = (original[i], back[i]);
                if (a.Width != b.Width || a.Height != b.Height || a.Depth != b.Depth || a.HasAlpha != b.HasAlpha || a.ColorSpace != b.ColorSpace
                    || a.Compare(b, ErrorMetric.Absolute) != 0)
                    return (null, "Lossless verification failed");
            }

            return (data, null);
        }
        catch (MagickException ex)
        {
            return (null, ex.Message);
        }
    }

    private static MagickImage ToMagick(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height, rb = bmp.RowBytes;
        var px = bmp.GetPixelSpan();
        var alpha = bmp.AlphaType != SKAlphaType.Opaque;
        var channels = alpha ? 4 : 3;
        var data = new byte[w * h * channels];
        for (var y = 0; y < h; y++)
        {
            var row = px.Slice(y * rb, w * 4);
            if (alpha)
            {
                row.CopyTo(data.AsSpan(y * w * 4));
                continue;
            }

            var o = y * w * 3;
            for (var x = 0; x < w; x++)
            {
                data[o + 3 * x] = row[4 * x];
                data[o + 3 * x + 1] = row[4 * x + 1];
                data[o + 3 * x + 2] = row[4 * x + 2];
            }
        }

        var settings = new PixelReadSettings((uint)w, (uint)h, StorageType.Char, alpha ? PixelMapping.RGBA : PixelMapping.RGB);
        return new MagickImage(data, settings);
    }

    private static SKBitmap ToSkia(IMagickImage<ushort> img, SKAlphaType alphaType)
    {
        using var pixels = img.GetPixelsUnsafe();
        var rgba = pixels.ToByteArray(PixelMapping.RGBA) ?? throw new InvalidDataException("Cannot read pixels");
        if (!img.HasAlpha)
        {
            for (var i = 3; i < rgba.Length; i += 4)
                rgba[i] = 255;
        }

        var bmp = new SKBitmap(new SKImageInfo((int)img.Width, (int)img.Height, SKColorType.Rgba8888, alphaType, null));
        Marshal.Copy(rgba, 0, bmp.GetPixels(), rgba.Length);
        return bmp;
    }
}
