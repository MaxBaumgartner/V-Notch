using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace VNotch.Services.ClaudeCode;

/// <summary>The tokens of a stretch of work: fresh input, input written to and read from the cache, and output.</summary>
internal readonly record struct ClaudeTokens(long Input, long CacheWrite, long CacheRead, long Output)
{
    public long Total => Input + CacheWrite + CacheRead + Output;

    /// <summary>
    /// Roughly what it weighs against the plan: a cache read costs a tenth of fresh input, a cache write a
    /// quarter more and output five times as much. These are the ratios in Anthropic's price list, the same
    /// for every model, so they share a limit out fairly between sessions without knowing the prices.
    /// </summary>
    public double Weight => Input + CacheWrite * 1.25 + CacheRead * 0.1 + Output * 5;

    public static ClaudeTokens operator +(ClaudeTokens a, ClaudeTokens b) =>
        new(a.Input + b.Input, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Output + b.Output);
}

/// <summary>What a session used: in its current (or last) task, in the whole chat, how full its context is, and the task's share of the five-hour limit.</summary>
internal sealed record ClaudeSessionTokens(ClaudeTokens Task, ClaudeTokens Chat, long Context, double? TaskLimitPercent);

/// <summary>
/// Counts tokens from the transcripts Claude Code writes, the way its own usage view does: every reply carries
/// its usage, written again with each of its content blocks, so each message counts once. A subagent's
/// transcript in the session's folder belongs to the session. Files are read from where the last read
/// stopped, so a call only parses what was appended. Thread-safe; meant to run off the UI thread.
/// </summary>
internal sealed class ClaudeTokenLedger
{
    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    private const int FirstChunk = 1 << 20;
    private static readonly byte[] AssistantMarker = "\"type\":\"assistant\""u8.ToArray();
    private static readonly byte[] UsageMarker = "\"usage\""u8.ToArray();

    private readonly string _projectsRoot;
    private readonly object _gate = new();
    private readonly Dictionary<string, Transcript> _files = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Transcript
    {
        public long Offset;
        public readonly Dictionary<string, Reply> Replies = new(StringComparer.Ordinal);
    }

    private readonly record struct Reply(DateTime Utc, ClaudeTokens Tokens, bool Sidechain);

    /// <param name="projectsRoot">Where Claude Code keeps its transcripts; its own folder when null.</param>
    public ClaudeTokenLedger(string? projectsRoot = null)
    {
        _projectsRoot = projectsRoot ?? Path.Combine(ClaudeCodeSetup.ClaudeDirectory, "projects");
    }

    /// <summary>
    /// The session behind <paramref name="transcriptPath"/>: its task (replies since <paramref name="turnStartUtc"/>),
    /// the chat, its context and, with <paramref name="fiveHour"/>, the task's share of that limit. Null without a transcript.
    /// </summary>
    public ClaudeSessionTokens? Read(string transcriptPath, DateTime? turnStartUtc, ClaudeLimitWindow? fiveHour, DateTime utcNow)
    {
        if (!transcriptPath.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) || !File.Exists(transcriptPath)) return null;
        lock (_gate)
        {
            ClaudeTokens task = default, chat = default;
            long context = 0;
            DateTime latest = DateTime.MinValue;
            DateTime? windowStart = fiveHour?.ResetsAt is { } resets && resets.UtcDateTime > utcNow ? resets.UtcDateTime - FiveHours : null;
            double taskWeight = 0;
            foreach (string path in SessionFiles(transcriptPath))
            {
                bool main = string.Equals(path, transcriptPath, StringComparison.OrdinalIgnoreCase);
                foreach (var reply in Update(path).Replies.Values)
                {
                    chat += reply.Tokens;
                    if (turnStartUtc is { } start && reply.Utc >= start)
                    {
                        task += reply.Tokens;
                        if (windowStart is { } from && reply.Utc >= from) taskWeight += reply.Tokens.Weight;
                    }
                    // The context is what the latest reply of the conversation itself saw and said.
                    if (main && !reply.Sidechain && reply.Utc >= latest)
                    {
                        latest = reply.Utc;
                        context = reply.Tokens.Total;
                    }
                }
            }

            double? share = null;
            if (windowStart is { } since && fiveHour != null && turnStartUtc != null)
            {
                double all = WeightSince(since);
                share = all > 0 ? Math.Clamp(fiveHour.UsedPercent * taskWeight / all, 0, 100) : 0;
            }
            return new ClaudeSessionTokens(task, chat, context, share);
        }
    }

    /// <summary>The transcript and its subagents' (in a folder named like the session, next to it).</summary>
    private static IEnumerable<string> SessionFiles(string transcriptPath)
    {
        yield return transcriptPath;
        string folder = Path.Combine(Path.GetDirectoryName(transcriptPath) ?? "", Path.GetFileNameWithoutExtension(transcriptPath));
        if (!Directory.Exists(folder)) yield break;
        string[] subagents;
        try
        {
            subagents = Directory.GetFiles(folder, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (string path in subagents) yield return path;
    }

    /// <summary>All sessions' weight since <paramref name="since"/>: what the five-hour limit's use is shared out over.</summary>
    private double WeightSince(DateTime since)
    {
        double weight = 0;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_projectsRoot, "*.jsonl", SearchOption.AllDirectories);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
        foreach (string path in files)
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < since) continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }
            foreach (var reply in Update(path).Replies.Values)
                if (reply.Utc >= since) weight += reply.Tokens.Weight;
        }
        return weight;
    }

    /// <summary>Parses what was appended to <paramref name="path"/> since the last read; a line still being written waits.</summary>
    private Transcript Update(string path)
    {
        if (!_files.TryGetValue(path, out var transcript)) _files[path] = transcript = new Transcript();
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            if (length < transcript.Offset)
            {
                // Rewritten from the start: count it again.
                transcript.Offset = 0;
                transcript.Replies.Clear();
            }
            byte[] buffer = ArrayPool<byte>.Shared.Rent(FirstChunk);
            try
            {
                while (transcript.Offset < length)
                {
                    stream.Seek(transcript.Offset, SeekOrigin.Begin);
                    int want = (int)Math.Min(buffer.Length, length - transcript.Offset);
                    stream.ReadExactly(buffer, 0, want);
                    int lastBreak = Array.LastIndexOf(buffer, (byte)'\n', want - 1, want);
                    if (lastBreak < 0)
                    {
                        // One line longer than the buffer (a big tool result): read it whole, or wait for its end.
                        if (want < buffer.Length) break;
                        byte[] bigger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = bigger;
                        continue;
                    }
                    ParseLines(buffer.AsMemory(0, lastBreak + 1), transcript);
                    transcript.Offset += lastBreak + 1;
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Gone or locked for a moment; what was read stays.
        }
        return transcript;
    }

    private static void ParseLines(ReadOnlyMemory<byte> chunk, Transcript transcript)
    {
        int start = 0;
        var span = chunk.Span;
        while (start < span.Length)
        {
            int end = span[start..].IndexOf((byte)'\n');
            int length = end < 0 ? span.Length - start : end;
            var line = span.Slice(start, length);
            // Most lines are prompts, tool output and bookkeeping: only replies with usage are parsed.
            if (line.IndexOf(AssistantMarker) >= 0 && line.IndexOf(UsageMarker) >= 0 &&
                TryReadReply(chunk.Slice(start, length), out string? id, out var reply))
                transcript.Replies[id!] = reply;
            start += length + 1;
        }
    }

    private static bool TryReadReply(ReadOnlyMemory<byte> line, out string? id, out Reply reply)
    {
        id = null;
        reply = default;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !type.ValueEquals("assistant")) return false;
            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return false;
            if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return false;
            id = message.TryGetProperty("id", out var idValue) && idValue.ValueKind == JsonValueKind.String
                ? idValue.GetString()
                : root.TryGetProperty("uuid", out var uuid) && uuid.ValueKind == JsonValueKind.String ? uuid.GetString() : null;
            if (string.IsNullOrEmpty(id)) return false;
            DateTime utc = root.TryGetProperty("timestamp", out var stamp) && stamp.ValueKind == JsonValueKind.String && stamp.TryGetDateTime(out var at)
                ? at.ToUniversalTime()
                : DateTime.MinValue;
            bool sidechain = root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True;
            reply = new Reply(utc, new ClaudeTokens(
                Count(usage, "input_tokens"), Count(usage, "cache_creation_input_tokens"),
                Count(usage, "cache_read_input_tokens"), Count(usage, "output_tokens")), sidechain);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static long Count(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long n) ? n : 0;
}
