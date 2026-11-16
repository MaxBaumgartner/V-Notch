namespace VNotch.Services.Spotlight;

internal static class SpotlightTodo
{
    /// <summary>Turns a plus-prefixed search into a to-do.</summary>
    internal static string? Parse(string query)
    {
        if (!query.StartsWith('+')) return null;
        string text = query[1..].Trim();
        return text.Length == 0 ? null : text;
    }
}
