using System.Runtime.InteropServices;
using DevCleaner.Core.Images;
using ImageMagick;
using SkiaSharp;

namespace DevCleaner.Tests;

/// <summary>Lossless JPEG repacking, WebP/AVIF/JPEG XL conversion and TIFF/HEIC input.</summary>
public sealed class ImageFormatTests : IDisposable
{
    private readonly TempTree _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void LosslessMode_RepacksJpeg_KeepingPixelsExifAndTrailer()
    {
        var exif = ExifSegment(orientation: 1);
        var trailer = new byte[2048];
        new Random(3).NextBytes(trailer);
        var original = WithSegment(Jpeg(Photo(640, 480), 92), exif);
        var path = _t.File("a.jpg", [.. original, .. trailer]);

        var (a, data) = ImageCompressor.Compress(path, new ImageCompressionOptions { Mode = ImageQualityMode.Lossless, MinSavingFraction = 0 });

        Assert.True(a.Status == ImageAnalysisStatus.Compressible, $"{a.Status}: {a.Message} q{a.Quality} worst={a.SsimWorst} {a.OriginalSize}->{a.CompressedSize}");
        Assert.True(a.Lossless);
        Assert.Equal("JPEG", a.OutputFormatName);
        Assert.Equal(2, data!.AsSpan().IndexOf(exif));
        Assert.True(data.AsSpan().EndsWith(trailer));
        Assert.True(SamePixels(original, data!));
    }

    [Theory]
    [InlineData(ImageOutputFormat.WebP, ".webp", "WebP")]
    [InlineData(ImageOutputFormat.Avif, ".avif", "AVIF")]
    [InlineData(ImageOutputFormat.JpegXl, ".jxl", "JPEG XL")]
    public void Jpeg_ConvertsToModernFormat_UprightWithMetadata(ImageOutputFormat format, string extension, string name)
    {
        // Stored sideways with Exif orientation 6 (rotate 90° clockwise to view).
        var path = _t.File("rotated.jpg", WithSegment(Jpeg(Photo(320, 240, grain: false), 98), ExifSegment(orientation: 6)));

        var (a, data) = ImageCompressor.Compress(path, new ImageCompressionOptions { Format = format, MinSavingFraction = 0 });

        Assert.True(a.Status == ImageAnalysisStatus.Compressible, $"{a.Status}: {a.Message} q{a.Quality} worst={a.SsimWorst} {a.OriginalSize}->{a.CompressedSize}");
        Assert.Equal(extension, a.OutputExtension);
        Assert.Equal(name, a.OutputFormatName);
        Assert.True(a.SsimWorst >= 0.98);
        using var output = new MagickImage(data!);
        Assert.Equal((240u, 320u), (output.Width, output.Height));
        Assert.Equal((ushort)1, output.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value ?? 1);
    }

    [Fact]
    public void Png_ConvertsToLosslessWebP_PixelIdentical()
    {
        using var src = Photo(200, 150);
        var path = _t.File("shot.png", Png(src));

        var (a, data) = ImageCompressor.Compress(path, new ImageCompressionOptions { Format = ImageOutputFormat.WebP, MinSavingFraction = 0 });

        Assert.True(a.Status == ImageAnalysisStatus.Compressible, $"{a.Status}: {a.Message} q{a.Quality} worst={a.SsimWorst} {a.OriginalSize}->{a.CompressedSize}");
        Assert.True(a.Lossless);
        Assert.Equal(".webp", a.OutputExtension);
        using var back = new MagickImage(data!);
        using var orig = new MagickImage(path);
        Assert.Equal(0, orig.Compare(back, ErrorMetric.Absolute));
    }

    [Fact]
    public void Tiff16Bit_IsRepackedLosslessly_KeepingDepthAndPages()
    {
        using var pages = new MagickImageCollection();
        for (var i = 0; i < 2; i++)
        {
            var page = new MagickImage(Png(Photo(300, 200)));
            page.Depth = 16;
            page.Settings.Compression = CompressionMethod.NoCompression;
            pages.Add(page);
        }

        var path = _t.File("scan.tif", pages.ToByteArray(MagickFormat.Tiff));

        var (a, data) = ImageCompressor.Compress(path, new ImageCompressionOptions());

        Assert.True(a.Status == ImageAnalysisStatus.Compressible, $"{a.Status}: {a.Message} q{a.Quality} worst={a.SsimWorst} {a.OriginalSize}->{a.CompressedSize}");
        Assert.True(a.Lossless);
        Assert.Equal(".tif", a.OutputExtension);
        using var back = new MagickImageCollection(data!);
        Assert.Equal(2, back.Count);
        Assert.Equal(16u, back[0].Depth);
        Assert.Equal(0, pages[1].Compare(back[1], ErrorMetric.Absolute));
    }

    [Fact]
    public void Tiff_ConvertsToJpeg_WhenRequested()
    {
        using var img = new MagickImage(Png(Photo(400, 300)));
        img.Settings.Compression = CompressionMethod.NoCompression;
        var path = _t.File("old.tiff", img.ToByteArray(MagickFormat.Tiff));

        var a = ImageCompressor.Analyze(path, new ImageCompressionOptions { Format = ImageOutputFormat.Jpeg });

        Assert.True(a.Status == ImageAnalysisStatus.Compressible, $"{a.Status}: {a.Message} q{a.Quality} worst={a.SsimWorst} {a.OriginalSize}->{a.CompressedSize}");
        Assert.Equal(".jpg", a.OutputExtension);
        Assert.True(a.SavedFraction > 0.5);
    }

    [Fact]
    public void Heif_IsOnlyConverted()
    {
        // An AVIF is a HEIF file read through the same libheif path as an iPhone HEIC.
        using var img = new MagickImage(Png(Photo(320, 240)));
        img.Quality = 100;
        var path = _t.File("IMG_0001.heic", img.ToByteArray(MagickFormat.Avif));

        var kept = ImageCompressor.Analyze(path, new ImageCompressionOptions());
        var converted = ImageCompressor.Analyze(path, new ImageCompressionOptions { Format = ImageOutputFormat.JpegXl, MinSavingFraction = 0 });

        Assert.Equal(ImageAnalysisStatus.Unsupported, kept.Status);
        Assert.Contains("already compact", kept.Message);
        Assert.Equal((320, 240), (converted.Width, converted.Height));
        Assert.Equal(".jxl", converted.OutputExtension);
        Assert.NotEqual(ImageAnalysisStatus.Failed, converted.Status);
    }

    [Fact]
    public void MotionPhoto_IsNotConverted_BecauseItsVideoWouldBeLost()
    {
        var video = new byte[4096];
        new Random(1).NextBytes(video);
        var path = _t.File("motion.jpg", [.. Jpeg(Photo(320, 240), 98), .. video]);

        var a = ImageCompressor.Analyze(path, new ImageCompressionOptions { Format = ImageOutputFormat.Avif });

        Assert.Equal(ImageAnalysisStatus.Unsupported, a.Status);
        Assert.Contains("video", a.Message);
    }

    [Fact]
    public void Scanner_FindsTiffAndHeic()
    {
        _t.File("p/a.tif", 5000);
        _t.File("p/b.HEIC", 5000);
        var found = ImageScanner.Find(new ImageScanOptions { Roots = [_t.P("p")], MinSize = 1000 });
        Assert.Equal([ImageKind.Tiff, ImageKind.Heic], found.Select(f => f.Kind).Order());
        Assert.Empty(ImageScanner.Find(new ImageScanOptions { Roots = [_t.P("p")], MinSize = 1000, IncludeTiff = false, IncludeHeic = false }));
    }

    private static SKBitmap Photo(int w, int h, bool grain = true)
    {
        var rnd = new Random(42);
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                px[i] = (byte)Math.Clamp(x * 255 / w + (grain ? rnd.Next(-1, 2) : 0), 0, 255);
                px[i + 1] = (byte)Math.Clamp(y * 255 / h + (grain ? rnd.Next(-1, 2) : 0), 0, 255);
                px[i + 2] = (byte)(60 + 120 * (0.5 + 0.5 * Math.Sin(x / 15.0) * Math.Cos(y / 11.0)));
                px[i + 3] = 255;
            }
        }

        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        return bmp;
    }

    private static byte[] Jpeg(SKBitmap bmp, int quality)
    {
        using var pixmap = bmp.PeekPixels();
        using var data = pixmap.Encode(new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore));
        return data.ToArray();
    }

    private static byte[] Png(SKBitmap bmp)
    {
        using var pixmap = bmp.PeekPixels();
        using var data = pixmap.Encode(new SKPngEncoderOptions(SKPngEncoderFilterFlags.NoFilters, 1));
        return data.ToArray();
    }

    private static bool SamePixels(byte[] a, byte[] b)
    {
        using var x = SKBitmap.Decode(a);
        using var y = SKBitmap.Decode(b);
        return x.GetPixelSpan().SequenceEqual(y.GetPixelSpan());
    }

    /// <summary>APP1 Exif with a single Orientation entry.</summary>
    private static byte[] ExifSegment(ushort orientation)
    {
        byte[] payload =
        [
            .. "Exif\0\0"u8, .. "II*\0"u8, 8, 0, 0, 0,
            1, 0,                                        // one IFD entry
            0x12, 0x01, 3, 0, 1, 0, 0, 0, (byte)orientation, 0, 0, 0,
            0, 0, 0, 0,                                  // no next IFD
        ];
        return [0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];
    }

    private static byte[] WithSegment(byte[] jpeg, byte[] segment)
    {
        var afterApp0 = jpeg[3] == 0xE0 ? 4 + ((jpeg[4] << 8) | jpeg[5]) : 2;
        return [0xFF, 0xD8, .. segment, .. jpeg.AsSpan(afterApp0)];
    }
}
