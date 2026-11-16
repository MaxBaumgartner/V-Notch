namespace VNotch.Services.Displays;

/// <summary>Selects the compact mode choices shown beside a display.</summary>
public static class ModeChoices
{
    public static IReadOnlyList<int> RefreshRates(IReadOnlyList<DisplayMode> modes, int w, int h)
    {
        var rates = modes.Where(m => m.Width == w && m.Height == h && m.RefreshHz > 0)
            .Select(m => m.RefreshHz).Distinct().OrderByDescending(hz => hz).ToArray();
        return (rates.Contains(60) ? rates.Where(hz => hz != 60).Take(5).Append(60) : rates.Take(6))
            .OrderBy(hz => hz).ToArray();
    }

    public static IReadOnlyList<(int W, int H)> NearbySizes(IReadOnlyList<DisplayMode> modes, int w, int h)
    {
        if (w <= 0 || h <= 0) return [];
        return new[] { (W: w, H: h) }.Concat(modes
            .Where(m => m.Width > 0 && m.Height > 0 && m.Width <= w && m.Height <= h
                && (m.Width < w || m.Height < h)
                && Math.Abs((double)m.Width / m.Height - (double)w / h) < 0.02)
            .Select(m => (W: m.Width, H: m.Height)).Distinct()
            .OrderByDescending(size => (long)size.W * size.H).Take(2)).ToArray();
    }

    internal static int RoundRefreshRate(double hz) =>
        double.IsFinite(hz) && hz > 0 && hz <= int.MaxValue
            ? (int)Math.Round(hz, MidpointRounding.AwayFromZero) : 0;
}
