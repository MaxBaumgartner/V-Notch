using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace VNotch.Services.ControlCenter;

/// <summary>The Roblox tab's reads: the Pi adds up every game of the creator; the icons come from the Pi too.</summary>
internal sealed partial class PiDashboardClient
{
    private const int MaxRobloxIconBytes = 512 * 1024;

    internal async Task<RobloxBoard?> GetRobloxAsync()
    {
        var board = GetAsync("/api/roblox");
        var history = GetAsync("/api/roblox/history");
        var people = GetAsync("/api/roblox/people");
        await Task.WhenAll(board, history, people).ConfigureAwait(false);
        return RobloxBoard.Parse(board.Result as JsonObject, history.Result as JsonObject, people.Result as JsonObject);
    }

    /// <summary>A game's icon as the Pi keeps it; null when it is not one of the Pi's icon routes or too big.</summary>
    internal async Task<byte[]?> GetRobloxIconAsync(string path)
    {
        if (!IsConfigured || !RobloxBoard.IsIconPath(path)) return null;
        try
        {
            using var response = await _http.GetAsync(Url(path), HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxRobloxIconBytes) return null;
            byte[] bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return bytes.Length is > 0 and <= MaxRobloxIconBytes ? bytes : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            RuntimeLog.Warn(LogTag, $"Roblox icon failed: {ex.Message}");
            return null;
        }
    }
}
