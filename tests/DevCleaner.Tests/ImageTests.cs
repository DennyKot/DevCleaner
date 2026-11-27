using System.Buffers.Binary;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using DevCleaner.Core;
using DevCleaner.Core.Abstractions;
using DevCleaner.Core.Engine;
using DevCleaner.Core.Images;
using SkiaSharp;

namespace DevCleaner.Tests;

public sealed class ImageTests : IDisposable
{
    private readonly TempTree _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public void Jpeg_IsRecompressed_KeepingExifAndDimensions()
    {
        var exif = ExifSegment();
        var path = _t.File("photos/a.jpg", WithSegment(Jpeg(Photo(640, 480), 98), exif));

        var (analysis, data) = ImageCompressor.Compress(path, new ImageCompressionOptions { MinSavingFraction = 0.05 });

        Assert.Equal(ImageAnalysisStatus.Compressible, analysis.Status);
        Assert.NotNull(data);
        Assert.True(data.Length < new FileInfo(path).Length);
        Assert.InRange(analysis.OriginalQuality ?? 0, 96, 100);
        Assert.True(analysis.SsimWorst >= 0.98);
        Assert.True(data.AsSpan().IndexOf(exif) == 2, "Exif must stay the first segment");
        using var decoded = SKBitmap.Decode(data);
        Assert.Equal((640, 480), (decoded.Width, decoded.Height));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(75)]
    [InlineData(90)]
    public void Jpeg_QualityEstimate_MatchesEncoder(int quality)
    {
        var info = JpegInfo.Parse(Jpeg(Photo(64, 64), quality));
        Assert.InRange(info.Quality ?? 0, quality - 2, quality + 2);
    }

    [Fact]
    public void Jpeg_MotionPhotoTrailer_IsKeptAtTheEnd()
    {
        var video = new byte[4096];
        new Random(1).NextBytes(video);
        var path = _t.File("pxl.jpg", [.. Jpeg(Photo(320, 240), 98), .. video]);

        var (analysis, data) = ImageCompressor.Compress(path, new ImageCompressionOptions());

        Assert.Equal(ImageAnalysisStatus.Compressible, analysis.Status);
        Assert.True(data.AsSpan().EndsWith(video));
    }

    [Fact]
    public void Jpeg_WithMpfGainMap_IsLeftAloneUnlessAllowed()
    {
        byte[] mpf = [0xFF, 0xE2, 0x00, 0x0C, .. "MPF\0"u8, .. "MM\0*"u8, 0, 0];
        var path = _t.File("hdr.jpg", WithSegment(Jpeg(Photo(320, 240), 98), mpf));

        var kept = ImageCompressor.Analyze(path, new ImageCompressionOptions());
        var dropped = ImageCompressor.Analyze(path, new ImageCompressionOptions { DropEmbeddedExtras = true });

        Assert.Equal(ImageAnalysisStatus.Unsupported, kept.Status);
        Assert.Contains("gain map", kept.Message);
        Assert.Equal(ImageAnalysisStatus.Compressible, dropped.Status);
    }


    [Fact]
    public void Png_IsRecompressedLosslessly_KeepingTextChunks()
    {
        using var src = Photo(200, 150);
        var png = WithTextChunk(Encode(src, new SKPngEncoderOptions(SKPngEncoderFilterFlags.NoFilters, 0)), "Author", "Grandma");
        var path = _t.File("scan.png", png);

        var (analysis, data) = ImageCompressor.Compress(path, new ImageCompressionOptions());

        Assert.Equal(ImageAnalysisStatus.Compressible, analysis.Status);
        Assert.True(analysis.Lossless);
        Assert.True(data!.AsSpan().IndexOf("tEXtAuthor\0Grandma"u8) > 0);
        Assert.True(SamePixels(src, data!));
    }

    [Fact]
    public void Apply_ReplacesBmpWithPng_KeepingPixelsAndTimestamp()
    {
        using var src = Photo(120, 90);
        var bmp = _t.File("old/scan.bmp", Bmp(src));
        var stamp = new DateTime(2004, 6, 1, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(bmp, stamp);

        var results = ImageApplier.Apply([Candidate(bmp)], new ImageApplyOptions { Originals = ImageOriginalsMode.DeletePermanently }, Engine());

        var r = Assert.Single(results);
        Assert.Equal(ImageApplyStatus.Compressed, r.Status);
        Assert.False(File.Exists(bmp));
        var png = Path.GetFullPath(_t.P("old/scan.png"));
        Assert.Equal(png, r.OutputPath);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(png));
        Assert.True(SamePixels(src, File.ReadAllBytes(png)));
        Assert.Single(Directory.GetFiles(_t.P("old"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Apply_DryRun_ChangesNothing()
    {
        var path = _t.File("a.jpg", Jpeg(Photo(320, 240), 98));
        var before = File.ReadAllBytes(path);

        var r = Assert.Single(ImageApplier.Apply([Candidate(path)], new ImageApplyOptions { DryRun = true, Originals = ImageOriginalsMode.DeletePermanently }, Engine()));

        Assert.Equal(ImageApplyStatus.DryRun, r.Status);
        Assert.True(r.SavedBytes > 0);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_t.Root));
    }

    [Fact]
    public void Apply_ExcludedOriginal_IsSkippedAndTempRemoved()
    {
        var path = _t.File("keep/a.jpg", Jpeg(Photo(320, 240), 98));
        var before = File.ReadAllBytes(path);

        var r = Assert.Single(ImageApplier.Apply([Candidate(path)], new ImageApplyOptions { Originals = ImageOriginalsMode.DeletePermanently },
            Engine(new PathMatcher([_t.P("keep")]))));

        Assert.Equal(ImageApplyStatus.Skipped, r.Status);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Single(Directory.GetFiles(_t.P("keep"), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Apply_SaveCopies_KeepsOriginalAndMirrorsFolders()
    {
        var path = _t.File("photos/2001/a.jpg", Jpeg(Photo(320, 240), 98));
        var before = File.ReadAllBytes(path);
        var options = new ImageApplyOptions { Originals = ImageOriginalsMode.SaveCopies, OutputFolder = _t.P("out") };

        var r = Assert.Single(ImageApplier.Apply([Candidate(path, _t.P("photos"))], options, Engine()));

        Assert.Equal(ImageApplyStatus.Compressed, r.Status);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(new FileInfo(_t.P("out/2001/a.jpg")).Length < before.Length);
    }

    [Fact]
    public void Scanner_FiltersBySizeTypeFoldersAndSkipList()
    {
        _t.File("p/big.jpg", 5000);
        _t.File("p/small.jpg", 10);
        _t.File("p/notes.txt", 5000);
        _t.File("p/scan.bmp", 5000);
        _t.File("p/node_modules/x.png", 5000);
        _t.File("p/.cache/y.png", 5000);
        _t.File("p/.a.jpg" + ".devcleaner-tmp", 5000);
        _t.File("p/done.png", 5000);
        var done = new FileInfo(_t.P("p/done.png"));
        var key = ImageScanner.OptimizedKey(done.FullName, done.Length, done.LastWriteTimeUtc);

        var found = ImageScanner.Find(new ImageScanOptions
        {
            Roots = [_t.P("p")],
            MinSize = 1000,
            IncludeBmp = false,
            Skip = f => ImageScanner.OptimizedKey(f.FullName, f.Length, f.LastWriteTimeUtc) == key,
        });

        Assert.Equal(["big.jpg"], found.Select(f => f.Name));
    }

    [Fact]
    public void Ssim_IdenticalImagesScoreOne()
    {
        var luma = Enumerable.Range(0, 64 * 64).Select(i => (byte)(i * 7)).ToArray();
        var s = Ssim.Compute(luma, luma, 64, 64);
        Assert.Equal(1, s.Mean, 6);
        Assert.Equal(1, s.Worst, 6);
    }

    private ImageCandidate Candidate(string path, string? root = null)
    {
        var f = new FileInfo(path);
        return new ImageCandidate(f.FullName, root ?? _t.Root, ImageCompressor.KindFromExtension(path)!.Value, f.Length, f.LastWriteTimeUtc);
    }

    private static CleanEngine Engine(PathMatcher? exclusions = null) => new(new FakePlatform(), [], exclusions ?? PathMatcher.Empty);

    /// <summary>Smooth gradients, soft waves and light grain: compresses like a scanned photo.</summary>
    private static SKBitmap Photo(int w, int h)
    {
        var rnd = new Random(42);
        var px = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                px[i] = (byte)Math.Clamp(x * 255 / w + rnd.Next(-1, 2), 0, 255);
                px[i + 1] = (byte)Math.Clamp(y * 255 / h + rnd.Next(-1, 2), 0, 255);
                px[i + 2] = (byte)(60 + 120 * (0.5 + 0.5 * Math.Sin(x / 15.0) * Math.Cos(y / 11.0)));
                px[i + 3] = 255;
            }
        }

        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Opaque));
        Marshal.Copy(px, 0, bmp.GetPixels(), px.Length);
        return bmp;
    }

    private static byte[] Jpeg(SKBitmap bmp, int quality) =>
        Encode(bmp, new SKJpegEncoderOptions(quality, SKJpegEncoderDownsample.Downsample420, SKJpegEncoderAlphaOption.Ignore));

    private static byte[] Encode(SKBitmap bmp, SKJpegEncoderOptions o)
    {
        using var pixmap = bmp.PeekPixels();
        using var data = pixmap.Encode(o);
        return data.ToArray();
    }

    private static byte[] Encode(SKBitmap bmp, SKPngEncoderOptions o)
    {
        using var pixmap = bmp.PeekPixels();
        using var data = pixmap.Encode(o);
        return data.ToArray();
    }

    /// <summary>24-bit bottom-up BMP.</summary>
    private static byte[] Bmp(SKBitmap src)
    {
        int w = src.Width, h = src.Height, stride = (w * 3 + 3) & ~3;
        var data = new byte[54 + stride * h];
        "BM"u8.CopyTo(data);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(2), data.Length);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(18), w);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(22), h);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(28), 24);
        var px = src.GetPixelSpan();
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var s = y * src.RowBytes + x * 4;
                var d = 54 + (h - 1 - y) * stride + x * 3;
                (data[d], data[d + 1], data[d + 2]) = (px[s + 2], px[s + 1], px[s]);
            }
        }

        return data;
    }

    private static bool SamePixels(SKBitmap expected, byte[] encoded)
    {
        using var codec = SKCodec.Create(SKData.CreateCopy(encoded));
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Opaque, null);
        using var actual = new SKBitmap(info);
        codec.GetPixels(info, actual.GetPixels());
        return expected.GetPixelSpan().SequenceEqual(actual.GetPixelSpan());
    }

    private static byte[] ExifSegment()
    {
        // APP1 "Exif\0\0" + little-endian TIFF header with an empty IFD.
        byte[] payload = [.. "Exif\0\0"u8, .. "II*\0"u8, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        return [0xFF, 0xE1, (byte)((payload.Length + 2) >> 8), (byte)(payload.Length + 2), .. payload];
    }

    private static byte[] WithSegment(byte[] jpeg, byte[] segment)
    {
        // Drop the encoder's JFIF APP0 so the file looks like a camera JPEG (Exif first).
        var afterApp0 = jpeg[3] == 0xE0 ? 4 + ((jpeg[4] << 8) | jpeg[5]) : 2;
        return [0xFF, 0xD8, .. segment, .. jpeg.AsSpan(afterApp0)];
    }

    private static byte[] WithTextChunk(byte[] png, string keyword, string text)
    {
        var body = Encoding.Latin1.GetBytes($"tEXt{keyword}\0{text}");
        var chunk = new byte[body.Length + 8];
        BinaryPrimitives.WriteInt32BigEndian(chunk, body.Length - 4);
        body.CopyTo(chunk, 4);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(4 + body.Length), Crc32.HashToUInt32(body));
        const int afterIhdr = 33;
        return [.. png.AsSpan(0, afterIhdr), .. chunk, .. png.AsSpan(afterIhdr)];
    }
}
