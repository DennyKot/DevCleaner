namespace DevCleaner.Core.Images;

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
