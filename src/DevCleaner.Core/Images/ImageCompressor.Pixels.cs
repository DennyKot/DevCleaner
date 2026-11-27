using System.Runtime.InteropServices;
using SkiaSharp;

namespace DevCleaner.Core.Images;

public static partial class ImageCompressor
{
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
