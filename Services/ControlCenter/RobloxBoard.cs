using System.Text.Json.Nodes;

namespace VNotch.Services.ControlCenter;

/// <summary>One day of all games together: visits, the most players at once and minutes played.</summary>
internal sealed record RobloxDay(string Date, int Visits, int Peak, int Minutes);

/// <summary>Today's daily levels of the co-op obbies: teams and players, per game the teams.</summary>
internal sealed record RobloxDaily(int Teams, int Players, IReadOnlyList<(string Game, int Teams)> Games);

/// <summary>
/// The Pi's Roblox tab: every public game of the creator added up. Players now, visits, the record, playtime,
/// rating and favourites, the last 24 hours in ten-minute steps, today's daily levels and how many different
/// players came. Only counts; the players' names stay on the Pi.
/// </summary>
internal sealed record RobloxBoard(
    int Playing, int PlayingGames, int Games, long Visits, int Favorites, int Up, int Down,
    int Record, DateTimeOffset? RecordAt, int Minutes, RobloxDay Today, RobloxDay? Yesterday,
    IReadOnlyList<string> Icons, IReadOnlyList<int> Last24Hours, RobloxDaily? Daily,
    int? DifferentPlayers, int? Robux, string Profile)
{
    /// <summary>Likes of all votes, 0–100; null without votes.</summary>
    public int? Rating => Up + Down > 0 ? (int)Math.Round(100.0 * Up / (Up + Down)) : null;

    public static RobloxBoard? Parse(JsonObject? roblox, JsonObject? history, JsonObject? people)
    {
        if (roblox == null || roblox["configured"] is JsonValue configured && configured.TryGetValue(out bool on) && !on) return null;
        if (roblox["total"] is not JsonObject total) return null;
        var days = (roblox["days"] as JsonArray)?.OfType<JsonObject>().Select(Day).ToList() ?? [];
        var today = roblox["today"] is JsonObject now ? Day(now) : new RobloxDay("", 0, 0, 0);
        var yesterday = days.LastOrDefault(d => string.CompareOrdinal(d.Date, today.Date) < 0);
        var peak = roblox["peak"] as JsonObject;
        long? peakAt = peak == null ? null : Long(peak, "at");
        var icons = (roblox["icons"] as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue(out string? s) ? s : null)
            .OfType<string>().Where(IsIconPath).ToList() ?? [];
        var last = (history?["points"] as JsonArray)?.OfType<JsonObject>().Select(p => Int(p, "max")).ToList() ?? [];
        string profile = Str(roblox, "profile");
        return new RobloxBoard(
            Int(total, "playing"), Int(total, "playingGames"), Int(total, "games"), Long(total, "visits") ?? 0,
            Int(total, "favorites"), Int(total, "up"), Int(total, "down"),
            peak == null ? 0 : Int(peak, "value"), peakAt is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(peakAt.Value) : null,
            Int(roblox, "minutes"), today, yesterday, icons, last, ReadDaily(roblox["daily"] as JsonObject),
            people != null && people["players"] is JsonValue ? Int(people, "players") : null,
            people != null && people["robux"] is JsonValue ? Int(people, "robux") : null,
            profile.StartsWith("https://www.roblox.com/", StringComparison.Ordinal) ? profile : "");
    }

    /// <summary>Only the Pi's own icon route, so a tile never fetches from anywhere else.</summary>
    internal static bool IsIconPath(string path) =>
        path.StartsWith("/api/roblox/gameicon/", StringComparison.Ordinal) && !path.Contains("..", StringComparison.Ordinal) &&
        !path.Contains("//", StringComparison.Ordinal);

    private static RobloxDaily? ReadDaily(JsonObject? daily)
    {
        if (daily?["today"] is not JsonObject today) return null;
        var games = (today["games"] as JsonObject)?.Select(g => (g.Key, g.Value is JsonValue v && v.TryGetValue(out int n) ? n : 0)).ToList() ?? [];
        return new RobloxDaily(Int(today, "teams"), Int(today, "players"), games);
    }

    private static RobloxDay Day(JsonObject day) => new(Str(day, "d"), Int(day, "visits"), Int(day, "peak"), Int(day, "minutes"));

    private static string Str(JsonObject obj, string key) => obj[key] is JsonValue value && value.TryGetValue(out string? text) ? text : "";

    private static int Int(JsonObject obj, string key) =>
        Number(obj, key) is { } number && number is >= int.MinValue and <= int.MaxValue ? (int)number : 0;

    private static long? Long(JsonObject obj, string key) =>
        Number(obj, key) is { } number && number is >= long.MinValue and <= long.MaxValue ? (long)number : null;

    /// <summary>A number as the Pi sends it (read from text) or as code builds it (an int or a long).</summary>
    private static double? Number(JsonObject obj, string key) => obj[key] is not JsonValue value ? null
        : value.TryGetValue(out double real) ? real
        : value.TryGetValue(out long whole) ? whole
        : value.TryGetValue(out int small) ? small
        : null;

    /// <summary>276 minutes → "4.6 h" (short enough for a tile in every language); under an hour "36 min".</summary>
    internal static string Playtime(int minutes) =>
        minutes >= 60 ? Loc.Get("roblox.hours", (minutes / 60.0).ToString(minutes >= 6000 ? "N0" : "N1", Loc.GetCulture()))
            : Loc.Get("roblox.minutes", minutes);

    internal static string Count(long value) => value.ToString("N0", Loc.GetCulture());

    internal static string ShortDate(DateTimeOffset when) =>
        when.ToLocalTime().ToString("d MMM", Loc.GetCulture());
}
