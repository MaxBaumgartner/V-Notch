using System;
using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;

namespace VNotch.Services.ClaudeCode;

internal enum ClaudeAttachmentKind
{
    Text,
    Lines,
    Image,
    Files
}

/// <summary>
/// Something that goes along with a message to a session: how it reads in the prompt (short text inline,
/// anything else as an @-mention of a file Claude Code can open) and what the notch shows for it.
/// </summary>
internal sealed record ClaudeAttachment(ClaudeAttachmentKind Kind, string Mention, int Count, string? ImagePath = null);

/// <summary>Clipboard text, pictures and copied files, turned into something a prompt can carry.</summary>
internal static class ClaudeAttachments
{
    private const int InlineChars = 200;
    private const int MaxFiles = 5;
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    /// <summary>Changes whenever anything is copied; the screenshot flow waits for it to move.</summary>
    public static uint ClipboardSequence => GetClipboardSequenceNumber();

    public static string Folder => Path.Combine(Path.GetTempPath(), "V-Notch", "claude");

    /// <summary>What the clipboard holds right now, or null when it is empty or unreadable. UI thread only.</summary>
    public static ClaudeAttachment? FromClipboard()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsFileDropList()) return FromFiles(System.Windows.Clipboard.GetFileDropList());
            if (System.Windows.Clipboard.ContainsImage() && System.Windows.Clipboard.GetImage() is { } image) return FromImage(image);
            if (!System.Windows.Clipboard.ContainsText()) return null;
            string text = System.Windows.Clipboard.GetText().Trim();
            if (text.Length == 0) return null;
            int lines = 1;
            foreach (char c in text) if (c == '\n') lines++;
            if (lines == 1 && text.Length <= InlineChars) return new ClaudeAttachment(ClaudeAttachmentKind.Text, text, 1);
            string path = NewFile(".txt");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return new ClaudeAttachment(ClaudeAttachmentKind.Lines, Mention(path), lines);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or IOException or UnauthorizedAccessException)
        {
            // Another app holds the clipboard open.
            RuntimeLog.Warn("CLAUDE", $"Clipboard not readable: {ex.Message}");
            return null;
        }
    }

    public static ClaudeAttachment FromImage(BitmapSource image)
    {
        string path = NewFile(".png");
        using (var stream = File.Create(path))
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(stream);
        }
        return new ClaudeAttachment(ClaudeAttachmentKind.Image, Mention(path), 1, path);
    }

    private static ClaudeAttachment? FromFiles(StringCollection files)
    {
        var mentions = new StringBuilder();
        int count = 0;
        foreach (string? file in files)
        {
            if (string.IsNullOrEmpty(file) || count == MaxFiles) continue;
            if (mentions.Length > 0) mentions.Append(' ');
            mentions.Append(Mention(file));
            count++;
        }
        return count == 0 ? null : new ClaudeAttachment(ClaudeAttachmentKind.Files, mentions.ToString(), count);
    }

    /// <summary>"@path" lets Claude Code open the file; a path with spaces would end the mention early, so it goes in plain.</summary>
    private static string Mention(string path) => path.Contains(' ', StringComparison.Ordinal) ? path : "@" + path;

    private static string NewFile(string extension)
    {
        Directory.CreateDirectory(Folder);
        CleanUp();
        return Path.Combine(Folder, $"notch-{DateTime.Now:yyyyMMdd-HHmmss-fff}{extension}");
    }

    /// <summary>Yesterday's attachments were read long ago.</summary>
    private static void CleanUp()
    {
        foreach (string file in Directory.EnumerateFiles(Folder, "notch-*"))
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > KeepFor) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open somewhere: next time.
            }
        }
    }
}
