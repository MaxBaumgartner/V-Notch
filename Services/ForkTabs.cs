using VNotch.Models;

namespace VNotch.Services;

/// <summary>
/// Tabs only this fork has. Kept out of the settings migrator, whose versions belong to upstream: a saved tab order
/// without the Roblox tab gets it once, next to the school tab and shown. Once the order names it, it is left alone,
/// so hiding it in the settings stays hidden.
/// </summary>
internal static class ForkTabs
{
    public const string Roblox = "Roblox";

    /// <returns>True when it changed the settings.</returns>
    public static bool AddRobloxOnce(NotchSettings settings)
    {
        var order = Tabs(settings.NavTabOrder);
        if (order.Contains(Roblox, StringComparer.OrdinalIgnoreCase)) return false;
        settings.NavTabOrder = string.Join(",", After(order, "School"));
        var visible = Tabs(settings.VisibleNavTabs);
        if (!visible.Contains(Roblox, StringComparer.OrdinalIgnoreCase)) settings.VisibleNavTabs = string.Join(",", After(visible, "School"));
        return true;
    }

    private static List<string> Tabs(string? list) =>
        [.. (list ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static List<string> After(List<string> tabs, string anchor)
    {
        int at = tabs.FindIndex(tab => string.Equals(tab, anchor, StringComparison.OrdinalIgnoreCase));
        tabs.Insert(at >= 0 ? at + 1 : tabs.Count, Roblox);
        return tabs;
    }
}
