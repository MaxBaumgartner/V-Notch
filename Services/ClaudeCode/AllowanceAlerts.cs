using System;
using System.Collections.Generic;
using System.Globalization;

namespace VNotch.Services.ClaudeCode;

/// <summary>
/// Decides when a plan limit (Claude or Codex, 5 hours or the week) has dropped below 50 % or 20 % left, so the notch
/// can say so once per threshold and window. A window is told apart by its reset time; the time is compared with
/// some slack because the endpoints round it differently from one read to the next.
/// </summary>
internal static class AllowanceAlerts
{
    /// <summary>Percent left at which to speak up, highest first.</summary>
    internal static readonly int[] Thresholds = [50, 20];

    private static readonly TimeSpan SameWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The threshold to announce now (the lowest one newly crossed) and every key to remember with it, or null.
    /// Crossing both at once (first read after a restart at 10 % left) announces 20 and remembers both.
    /// </summary>
    internal static (int Threshold, IReadOnlyList<string> Keys)? Due(string source, string kind, ClaudeLimitWindow? window,
        IReadOnlyCollection<string> sent)
    {
        if (window?.ResetsAt is not { } resets) return null;
        double left = 100 - window.UsedPercent;
        List<string>? keys = null;
        int threshold = 0;
        foreach (int t in Thresholds)
        {
            if (left >= t || WasSent(source, kind, t, resets, sent)) continue;
            (keys ??= new()).Add(Key(source, kind, t, resets));
            threshold = t;
        }
        return keys == null ? null : (threshold, keys);
    }

    /// <summary>Forgets windows that are over, so the saved list stays a handful of entries.</summary>
    internal static bool Prune(List<string> sent, DateTimeOffset now) =>
        sent.RemoveAll(key => !TryParse(key, out _, out _, out _, out var resets) || resets + SameWindow < now) > 0;

    internal static string Key(string source, string kind, int threshold, DateTimeOffset resets) =>
        string.Join('|', source, kind, threshold.ToString(CultureInfo.InvariantCulture),
            (resets.ToUnixTimeSeconds() / 60).ToString(CultureInfo.InvariantCulture));

    private static bool WasSent(string source, string kind, int threshold, DateTimeOffset resets, IReadOnlyCollection<string> sent)
    {
        foreach (var key in sent)
        {
            if (TryParse(key, out var s, out var k, out var t, out var r) && s == source && k == kind && t == threshold &&
                (r - resets).Duration() <= SameWindow)
                return true;
        }
        return false;
    }

    private static bool TryParse(string key, out string source, out string kind, out int threshold, out DateTimeOffset resets)
    {
        source = kind = ""; threshold = 0; resets = default;
        var parts = key.Split('|');
        if (parts.Length != 4 || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out threshold) ||
            !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long minutes))
            return false;
        source = parts[0]; kind = parts[1];
        resets = DateTimeOffset.FromUnixTimeSeconds(minutes * 60);
        return true;
    }
}
