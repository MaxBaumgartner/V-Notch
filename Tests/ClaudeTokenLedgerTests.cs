using System;
using System.IO;
using VNotch.Services.ClaudeCode;
using Xunit;

namespace VNotch.Tests;

public sealed class ClaudeTokenLedgerTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 11, 7, 20, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vnotch-ledger-" + Guid.NewGuid().ToString("N"));
    private readonly string _project;

    public ClaudeTokenLedgerTests()
    {
        _project = Path.Combine(_root, "C--work-app");
        Directory.CreateDirectory(_project);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Reply(string id, int minute, long input, long write, long read, long output, bool sidechain = false) =>
        $$$"""{"type":"assistant","isSidechain":{{{(sidechain ? "true" : "false")}}},"timestamp":"{{{T0.AddMinutes(minute):yyyy-MM-ddTHH:mm:ss.fffZ}}}","message":{"id":"{{{id}}}","role":"assistant","content":[{"type":"text","text":"x"}],"usage":{"input_tokens":{{{input}}},"cache_creation_input_tokens":{{{write}}},"cache_read_input_tokens":{{{read}}},"output_tokens":{{{output}}}""" + "}}}\n";

    private static string Prompt(int minute) =>
        $$$"""{"type":"user","timestamp":"{{{T0.AddMinutes(minute):yyyy-MM-ddTHH:mm:ss.fffZ}}}","message":{"role":"user","content":"go"}}""" + "\n";

    [Fact]
    public void CountsEachReplyOnceAndSplitsTaskFromChat()
    {
        string session = Path.Combine(_project, "s1.jsonl");
        File.WriteAllText(session,
            Prompt(0) +
            Reply("a", 1, 10, 100, 1000, 50) +
            Reply("a", 1, 10, 100, 1000, 50) + // the same reply, written again with its next content block
            Prompt(10) +
            Reply("b", 11, 5, 0, 2000, 20));
        Directory.CreateDirectory(Path.Combine(_project, "s1", "subagents"));
        File.WriteAllText(Path.Combine(_project, "s1", "subagents", "agent-1.jsonl"), Reply("c", 12, 1, 0, 0, 10, sidechain: true));

        var tokens = new ClaudeTokenLedger(_root).Read(session, T0.AddMinutes(10), null, T0.AddMinutes(20))!;

        Assert.Equal(new ClaudeTokens(6, 0, 2000, 30), tokens.Task);
        Assert.Equal(new ClaudeTokens(16, 100, 3000, 80), tokens.Chat);
        Assert.Equal(2025, tokens.Context);
        Assert.Null(tokens.TaskLimitPercent);
    }

    [Fact]
    public void TheFiveHourLimitIsSharedOutByWeight()
    {
        string session = Path.Combine(_project, "s1.jsonl");
        File.WriteAllText(session, Prompt(10) + Reply("b", 11, 100, 0, 0, 100));
        // Another session in the same window used three times as much.
        File.WriteAllText(Path.Combine(_project, "s2.jsonl"), Reply("d", 12, 300, 0, 0, 300));
        // One from before the window does not count.
        string old = Path.Combine(_project, "s0.jsonl");
        File.WriteAllText(old, Reply("e", -400, 100000, 0, 0, 0));

        var window = new ClaudeLimitWindow(40, new DateTimeOffset(T0.AddHours(4)));
        var tokens = new ClaudeTokenLedger(_root).Read(session, T0.AddMinutes(10), window, T0.AddMinutes(20))!;

        Assert.Equal(10, tokens.TaskLimitPercent!.Value, 3);
    }

    [Fact]
    public void ReadsWhatIsAppendedAndWaitsForAHalfWrittenLine()
    {
        string session = Path.Combine(_project, "s1.jsonl");
        File.WriteAllText(session, Prompt(0) + Reply("a", 1, 1, 0, 0, 1));
        var ledger = new ClaudeTokenLedger(_root);
        Assert.Equal(2, ledger.Read(session, T0, null, T0)!.Chat.Total);

        string next = Reply("b", 2, 10, 0, 0, 10);
        File.AppendAllText(session, next[..40]);
        Assert.Equal(2, ledger.Read(session, T0, null, T0)!.Chat.Total);

        File.AppendAllText(session, next[40..]);
        Assert.Equal(22, ledger.Read(session, T0, null, T0)!.Chat.Total);
    }
}
