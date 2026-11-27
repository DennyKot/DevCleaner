using System.Runtime.InteropServices;
using SkiaSharp;

namespace DevCleaner.Core.Images;

public static partial class ImageCompressor
{
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
}
