using System.Text.RegularExpressions;

namespace VNotch.Services;

internal static class OneTimeCode
{
    private static readonly Regex Words = new(
        @"\b(?:code|otp|pin|passcode|verification|bestätigung|bestätigungscode|sicherheitscode|einmalcode|tan)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Numbers = new(
        @"(?<![\p{L}\p{N}])(?:[A-Za-z]-)?(?<digits>[0-9]+(?: [0-9]+)*)(?![\p{L}\p{N}])",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static string? Find(string title, IReadOnlyList<string> lines)
    {
        string? result = null;
        int bestGap = int.MaxValue;
        bool ambiguous = false;
        try
        {
            string text = string.Join(" ", new[] { title }.Concat(lines));
            var words = Words.Matches(text);
            foreach (Match number in Numbers.Matches(text))
            {
                string digits = number.Groups["digits"].Value.Replace(" ", "");
                if (digits.Length is < 4 or > 8) continue;
                foreach (Match word in words)
                {
                    int start = Math.Min(number.Index + number.Length, word.Index + word.Length);
                    int end = Math.Max(number.Index, word.Index);
                    int gap = end - start;
                    // A nearby cue may be in the title, but not across a sentence or another number.
                    if (gap < 0 || gap > 24 || text.AsSpan(start, gap).IndexOfAny("0123456789.!?;\r\n") >= 0) continue;
                    if (gap < bestGap)
                    {
                        result = digits;
                        bestGap = gap;
                        ambiguous = false;
                    }
                    else if (gap == bestGap && result != digits) ambiguous = true;
                }
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        return ambiguous ? null : result;
    }
}
