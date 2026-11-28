using System.Collections;

namespace VNotch.Services.ClaudeCode;

/// <summary>
/// When a Claude Code session restarts the notch (an install, a test run), the notch inherits that session's private
/// variables: CLAUDE_CODE_CHILD_SESSION, CLAUDE_CODE_SESSION_ID, its messaging pipe and token, CLAUDE_EFFORT …
/// Every Claude the notch then opens ("Study with Claude", resume) would believe it is that session's child: it saves
/// no transcript and runs at the parent's effort. So the notch drops them at start, keeping any variable the user set
/// for themselves in Windows (user or machine environment).
/// </summary>
internal static class ClaudeSessionEnvironment
{
    internal static void ScrubInherited() =>
        Scrub(Environment.GetEnvironmentVariables(), name => Environment.SetEnvironmentVariable(name, null),
            name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) != null
                || Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine) != null);

    internal static IReadOnlyList<string> Scrub(IDictionary variables, Action<string> remove, Func<string, bool> setByUser)
    {
        var removed = new List<string>();
        foreach (string name in variables.Keys.OfType<string>().ToArray())
        {
            if (!IsSessionVariable(name) || setByUser(name)) continue;
            remove(name);
            removed.Add(name);
        }
        return removed;
    }

    internal static bool IsSessionVariable(string name) =>
        name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("CLAUDE_", StringComparison.OrdinalIgnoreCase);
}
