using System.Globalization;
using System.IO;
using System.Text.Json;

namespace VNotch.Services.ClaudeCode;

internal sealed record CodexSlot(string Name, string Home);

internal sealed record CodexBudget(ClaudeLimitWindow? FiveHour, ClaudeLimitWindow? Week, DateTimeOffset? BlockedUntil,
    string? Plan);

internal sealed class CodexAccount(CodexSlot slot, CodexUsageClient client) : IDisposable
{
    public CodexSlot Slot { get; } = slot;
    public CodexUsageClient Client { get; } = client;
    public CodexBudget? Budget { get; set; }
    public ClaudeLimitWindow? FiveHour => Client.Latest?.FiveHour ?? Budget?.FiveHour;
    public ClaudeLimitWindow? Week => Client.Latest?.Week ?? Budget?.Week;
    public string? Plan => Client.Latest?.Plan ?? Budget?.Plan;

    public string DisplayName => "Codex " + Slot.Name;

    public DateTimeOffset? BlockedUntil(DateTimeOffset now)
    {
        var until = Budget?.BlockedUntil > now ? Budget.BlockedUntil : null;
        foreach (var window in new[] { FiveHour, Week })
            if (window is { UsedPercent: >= 100, ResetsAt: { } reset } && reset > now && (until == null || reset > until))
                until = reset;
        return until;
    }

    internal static CodexAccount? Next(IEnumerable<CodexAccount> accounts, DateTimeOffset now) => accounts
        .Where(account => account.BlockedUntil(now) == null && account.FiveHour != null && account.Week != null &&
            Math.Max(Used(account.FiveHour, now), Used(account.Week, now)) < 100)
        .OrderBy(account => Math.Max(Used(account.FiveHour!, now), Used(account.Week!, now)))
        .FirstOrDefault();

    private static double Used(ClaudeLimitWindow window, DateTimeOffset now) =>
        window.ResetsAt <= now ? 0 : window.UsedPercent;

    public void Dispose() => Client.Dispose();
}

/// <summary>The router's configuration and cached budget. Read with sharing because the router can replace either file.</summary>
internal static class CodexAccounts
{
    public static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "orchestra");
    public const string SlotsFile = "codex-accounts.json";
    public const string BudgetFile = "budget.json";

    public static IReadOnlyList<CodexSlot>? ReadSlots(string directory) =>
        Read(Path.Combine(directory, SlotsFile), ParseSlots, Array.Empty<CodexSlot>());

    public static IReadOnlyDictionary<string, CodexBudget>? ReadBudget(string directory) =>
        Read(Path.Combine(directory, BudgetFile), ParseBudget, new Dictionary<string, CodexBudget>());

    internal static IReadOnlyList<CodexSlot> ParseSlots(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new JsonException();
        var slots = new List<CodexSlot>();
        foreach (var entry in root.EnumerateArray())
        {
            string? name = Text(entry, "name")?.Trim();
            string? home = Text(entry, "home")?.Trim();
            if (string.IsNullOrEmpty(name) || name.Contains('@') || name.Contains('|') ||
                name.Any(char.IsControl) || string.IsNullOrEmpty(home) || !Path.IsPathFullyQualified(home) ||
                slots.Any(slot => string.Equals(slot.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            slots.Add(new CodexSlot(name, home));
            if (slots.Count == 5) break;
        }
        return slots;
    }

    internal static IReadOnlyDictionary<string, CodexBudget> ParseBudget(JsonElement root)
    {
        var budgets = new Dictionary<string, CodexBudget>(StringComparer.OrdinalIgnoreCase);
        if (!Property(root, "codexAccounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array) return budgets;
        foreach (var entry in accounts.EnumerateArray())
            if (Text(entry, "name") is { } name)
                budgets[name] = new CodexBudget(Window(entry, "fiveHour"), Window(entry, "week"),
                    Date(Text(entry, "blocked")), CodexUsageClient.PlanName(Text(entry, "plan")));
        return budgets;
    }

    private static T? Read<T>(string path, Func<JsonElement, T> parse, T missing) where T : class
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var document = JsonDocument.Parse(stream);
            return parse(document.RootElement);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return missing; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    private static ClaudeLimitWindow? Window(JsonElement account, string name) =>
        Property(account, name, out var window) && Property(window, "used", out var used) &&
        used.ValueKind == JsonValueKind.Number && used.TryGetDouble(out double value) && double.IsFinite(value)
            ? new ClaudeLimitWindow(Math.Clamp(value, 0, 100), Date(Text(window, "resetsAt"))) : null;

    private static DateTimeOffset? Date(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static bool Property(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value);
    }

    private static string? Text(JsonElement element, string name) =>
        Property(element, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
