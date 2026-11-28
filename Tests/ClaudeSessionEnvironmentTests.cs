using System.Collections;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

public class ClaudeSessionEnvironmentTests
{
    [Fact]
    public void InheritedSessionVariablesGoUserSetOnesStay()
    {
        var variables = new Hashtable
        {
            ["CLAUDECODE"] = "1", ["CLAUDE_CODE_CHILD_SESSION"] = "1", ["CLAUDE_CODE_SESSION_ID"] = "x",
            ["CLAUDE_CODE_MESSAGING_TOKEN"] = "secret", ["CLAUDE_EFFORT"] = "xhigh", ["CLAUDE_CONFIG_DIR"] = @"D:\claude",
            ["PATH"] = @"C:\Windows", ["VNOTCH_SKIP_INTEGRITY_CHECK"] = "1", ["ANTHROPIC_API_KEY"] = "k"
        };
        var removed = ClaudeSessionEnvironment.Scrub(variables, name => variables.Remove(name), name => name == "CLAUDE_CONFIG_DIR");

        Assert.Equal(["CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_SESSION_ID", "CLAUDE_EFFORT"],
            removed.Order(StringComparer.Ordinal));
        Assert.Equal(["ANTHROPIC_API_KEY", "CLAUDE_CONFIG_DIR", "PATH", "VNOTCH_SKIP_INTEGRITY_CHECK"],
            variables.Keys.OfType<string>().Order(StringComparer.Ordinal));
    }
}
