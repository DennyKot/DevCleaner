using System.Buffers.Binary;
using System.Text;

namespace DevCleaner.Core.Images;

internal enum ChromaSubsampling
{
    Yuv420,
    Yuv422,
    Yuv444,
}

/// <summary>Reads the JPEG marker stream up to the first scan: quantization tables, frame layout and metadata segments.</summary>
internal sealed class JpegInfo
{
    // IJG standard luminance table (natural order) and the zigzag → natural index map.
    private static readonly int[] StdLuma =
    [
        16, 11, 10, 16, 24, 40, 51, 61, 12, 12, 14, 19, 26, 58, 60, 55, 14, 13, 16, 24, 40, 57, 69, 56, 14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77, 24, 35, 55, 64, 81, 104, 113, 92, 49, 64, 78, 87, 103, 121, 120, 101, 72, 92, 95, 98, 112, 100, 103, 99,
    ];

    private static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    public bool IsJpeg { get; private set; }
    public string? UnsupportedReason { get; private set; }
    public int Components { get; private set; }
    public ChromaSubsampling Subsampling { get; private set; } = ChromaSubsampling.Yuv420;
    public bool HasJfif { get; private set; }

    /// <summary>Estimated IJG quality (1-100) of the luminance table.</summary>
    public int? Quality { get; private set; }

    /// <summary>
    /// Multi-Picture Format index (Ultra HDR gain map, depth map, burst frames). Its offsets are absolute, so the extra
    /// images cannot be carried over to a re-encoded file.
    /// </summary>
    public bool HasMpf { get; private set; }

    /// <summary>
    /// Bytes after the end of the image: Google/Samsung motion-photo video and vendor data. These formats locate their
    /// data from the end of the file, so the trailer stays valid when appended to the re-encoded image.
    /// </summary>
    public byte[]? Trailer { get; private set; }

    /// <summary>APP1 (Exif/XMP), APP2 ICC_PROFILE, APP13 (IPTC) and COM segments, each including marker and length.</summary>
    public List<byte[]> MetadataSegments { get; } = [];

    public static JpegInfo Parse(ReadOnlySpan<byte> data)
    {
        var info = new JpegInfo();
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return info;
        info.IsJpeg = true;
        var i = 2;
        var scanned = false;
        while (i + 4 <= data.Length)
        {
            if (data[i] != 0xFF)
                break;
            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            if (marker is 0xD8 or 0x01 or >= 0xD0 and <= 0xD7)
            {
                i += 2;
                continue;
            }

            if (marker == 0xD9)
            {
                var trailer = data[(i + 2)..];
                if (trailer.ContainsAnyExcept((byte)0x00, (byte)0xFF))
                    info.Trailer = trailer.ToArray();
                break;
            }

            var length = (data[i + 2] << 8) | data[i + 3];
            if (length < 2 || i + 2 + length > data.Length)
                break;
            var payload = data.Slice(i + 4, length - 2);
            var segment = data.Slice(i, length + 2);
            switch (scanned && marker is >= 0xE0 ? 0 : marker)
            {
                case 0xDB:
                    info.ReadQuantTables(payload);
                    break;
                case 0xC0 or 0xC1 or 0xC2:
                    info.ReadFrame(payload);
                    break;
                case 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF:
                    info.UnsupportedReason = "Lossless, hierarchical or arithmetic-coded JPEG";
                    break;
                case 0xE0:
                    if (payload.StartsWith("JFIF\0"u8))
                        info.HasJfif = true;
                    break;
                case 0xE1 or 0xED or 0xFE:
                    info.MetadataSegments.Add(segment.ToArray());
                    break;
                case 0xE2:
                    // Only colour profiles; MPF (multi-picture) offsets would point into data we do not copy.
                    if (payload.StartsWith("ICC_PROFILE\0"u8))
                        info.MetadataSegments.Add(segment.ToArray());
                    else if (payload.StartsWith("MPF\0"u8))
                        info.HasMpf = true;
                    break;
            }

            i += 2 + length;
            if (marker == 0xDA)
            {
                scanned = true;
                i = SkipEntropyData(data, i);
            }
        }

        return info;
    }

    /// <summary>Returns the index of the next real marker after entropy-coded scan data.</summary>
    private static int SkipEntropyData(ReadOnlySpan<byte> data, int i)
    {
        while (i + 1 < data.Length)
        {
            var next = data[i..].IndexOf((byte)0xFF);
            if (next < 0 || i + next + 1 >= data.Length)
                return data.Length;
            i += next;
            var m = data[i + 1];
            if (m == 0x00 || m is >= 0xD0 and <= 0xD7)
                i += 2;
            else if (m == 0xFF)
                i++;
            else
                return i;
        }

        return data.Length;
    }

    private void ReadQuantTables(ReadOnlySpan<byte> p)
    {
        var pos = 0;
        while (pos < p.Length)
        {
            var precision = p[pos] >> 4;
            var id = p[pos] & 0x0F;
            pos++;
            var step = precision == 0 ? 1 : 2;
            if (pos + 64 * step > p.Length)
                return;
            if (id == 0)
            {
                double sum = 0;
                for (var k = 0; k < 64; k++)
                {
                    var v = step == 1 ? p[pos + k] : (p[pos + 2 * k] << 8) | p[pos + 2 * k + 1];
                    sum += v * 100.0 / StdLuma[ZigZag[k]];
                }

                var scale = sum / 64;
                var q = scale <= 100 ? (200 - scale) / 2 : 5000 / scale;
                Quality = (int)Math.Clamp(Math.Round(q), 1, 100);
            }

            pos += 64 * step;
        }
    }

    private void ReadFrame(ReadOnlySpan<byte> p)
    {
        if (p.Length < 6)
            return;
        if (p[0] != 8)
            UnsupportedReason = $"{p[0]}-bit JPEG";
        Components = p[5];
        if (Components is not (1 or 3))
            UnsupportedReason = Components == 4 ? "CMYK JPEG" : $"{Components}-component JPEG";
        if (Components == 3 && p.Length >= 6 + 9)
        {
            var h = p[7] >> 4;
            var v = p[7] & 0x0F;
            Subsampling = (h, v) switch
            {
                (1, 1) => ChromaSubsampling.Yuv444,
                (2, 1) => ChromaSubsampling.Yuv422,
                _ => ChromaSubsampling.Yuv420,
            };
        }
    }

    public byte[] AppendTrailer(byte[] encoded) => Trailer is null ? encoded : [.. encoded, .. Trailer];

    /// <summary>
    /// Removes APP1–APP13, APP15 and COM segments before the first scan, so a curated set can be inserted again.
    /// APP0 (JFIF) and APP14 (Adobe colour transform) describe how to decode the data and are kept.
    /// </summary>
    public static byte[] StripMetadata(byte[] jpeg)
    {
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            return jpeg;
        using var ms = new MemoryStream(jpeg.Length);
        ms.Write(jpeg, 0, 2);
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF)
        {
            var marker = jpeg[i + 1];
            if (marker == 0xDA || marker == 0xD9)
                break;
            var length = (jpeg[i + 2] << 8) | jpeg[i + 3];
            if (length < 2 || i + 2 + length > jpeg.Length)
                break;
            var drop = marker is (>= 0xE1 and <= 0xED) or 0xEF or 0xFE;
            if (!drop)
                ms.Write(jpeg, i, length + 2);
            i += length + 2;
        }

        ms.Write(jpeg, i, jpeg.Length - i);
        return ms.ToArray();
    }

    /// <summary>
    /// Puts the original metadata segments into a freshly encoded JPEG. The encoder's JFIF header is kept only when the
    /// original had one, so Exif stays the first segment for camera files.
    /// </summary>
    public byte[] InsertMetadata(byte[] encoded)
    {
        if (MetadataSegments.Count == 0 || encoded.Length < 4)
            return encoded;
        var afterApp0 = 2;
        if (encoded[2] == 0xFF && encoded[3] == 0xE0 && encoded.Length >= 6)
            afterApp0 = 4 + ((encoded[4] << 8) | encoded[5]);
        using var ms = new MemoryStream(encoded.Length + MetadataSegments.Sum(s => s.Length));
        ms.Write(encoded, 0, 2);
        if (HasJfif)
            ms.Write(encoded, 2, afterApp0 - 2);
        foreach (var s in MetadataSegments)
            ms.Write(s);
        ms.Write(encoded, afterApp0, encoded.Length - afterApp0);
        return ms.ToArray();
    }
}

/// <summary>PNG chunk helpers: header checks and copying ancillary metadata chunks.</summary>
internal static class PngChunks
{
    private static readonly HashSet<string> Copyable = ["tEXt", "zTXt", "iTXt", "eXIf", "tIME", "pHYs", "iCCP", "sRGB", "gAMA", "cHRM"];
    private static readonly HashSet<string> ColorChunks = ["iCCP", "sRGB", "gAMA", "cHRM"];

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool IsPng(ReadOnlySpan<byte> d) => d.Length >= 33 && d[..8].SequenceEqual(Signature);

    /// <summary>Bit depth from IHDR (0 if not a PNG).</summary>
    public static int BitDepth(ReadOnlySpan<byte> d) => IsPng(d) ? d[24] : 0;

    private static List<(string Type, int Offset, int Length)> Read(byte[] d)
    {
        var list = new List<(string, int, int)>();
        var pos = 8;
        while (pos + 12 <= d.Length)
        {
            var len = BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(pos));
            if (len < 0 || pos + 12L + len > d.Length)
                break;
            var type = Encoding.ASCII.GetString(d, pos + 4, 4);
            list.Add((type, pos, len + 12));
            pos += len + 12;
            if (type == "IEND")
                break;
        }

        return list;
    }

    public static byte[] CopyMetadata(byte[] original, byte[] encoded)
    {
        if (!IsPng(original) || !IsPng(encoded))
            return encoded;
        var newChunks = Read(encoded);
        var newTypes = newChunks.Select(c => c.Type).ToHashSet();
        var hasColor = newTypes.Overlaps(ColorChunks);
        var toCopy = Read(original)
            .Where(c => Copyable.Contains(c.Type) && !newTypes.Contains(c.Type) && !(hasColor && ColorChunks.Contains(c.Type)))
            .ToList();
        if (toCopy.Count == 0 || newChunks.Count == 0 || newChunks[0].Type != "IHDR")
            return encoded;

        var afterIhdr = newChunks[0].Offset + newChunks[0].Length;
        using var ms = new MemoryStream(encoded.Length + toCopy.Sum(c => c.Length));
        ms.Write(encoded, 0, afterIhdr);
        foreach (var c in toCopy)
            ms.Write(original, c.Offset, c.Length);
        ms.Write(encoded, afterIhdr, encoded.Length - afterIhdr);
        return ms.ToArray();
    }
}
