using System.Globalization;

namespace DevCleaner.Core;

public static class Bytes
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    public static string Format(long bytes)
    {
        double v = bytes;
        var i = 0;
        while (Math.Abs(v) >= 1024 && i < Units.Length - 1)
        {
            v /= 1024;
            i++;
        }

        return i == 0 ? $"{bytes} B" : v.ToString("0.##", CultureInfo.InvariantCulture) + " " + Units[i];
    }

    /// <summary>Parses "500", "10KB", "1.5GB" (binary units).</summary>
    public static bool TryParse(string text, out long bytes)
    {
        bytes = 0;
        var s = text.Trim().ToUpperInvariant();
        long mul = 1;
        foreach (var (suffix, m) in new[] { ("TB", 1L << 40), ("GB", 1L << 30), ("MB", 1L << 20), ("KB", 1L << 10), ("B", 1L) })
        {
            if (s.EndsWith(suffix, StringComparison.Ordinal))
            {
                mul = m;
                s = s[..^suffix.Length].Trim();
                break;
            }
        }

        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0)
            return false;
        bytes = (long)(n * mul);
        return true;
    }
}
