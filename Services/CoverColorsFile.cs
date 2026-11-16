using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media;

namespace VNotch.Services;

/// <summary>
/// The playing track's cover colours as a small file (%LOCALAPPDATA%\V-Notch\cover-colors.json), for the Govee
/// strip's music sync ("Govee PC-Sync" on this PC), which lights the room in the cover's colours instead of
/// running through the rainbow. Written only when the colours change; emptied when nothing plays.
/// </summary>
internal static class CoverColorsFile
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "V-Notch", "cover-colors.json");
    private static string? _last;

    /// <summary>The playing track's cover colours (main first); empty when nothing plays.</summary>
    public static IReadOnlyList<Color> Current { get; private set; } = Array.Empty<Color>();

    /// <summary>The colours changed; raised on the thread that published them (the UI thread).</summary>
    public static event Action? Changed;

    public static void Publish(Color main, Color sub) => Set(new[] { main, sub });

    public static void Clear() => Set(Array.Empty<Color>());

    private static void Set(Color[] colors)
    {
        if (colors.SequenceEqual(Current)) return;
        Current = colors;
        Write(colors.Select(Hex).ToArray());
        Changed?.Invoke();
    }

    private static void Write(string[] colors)
    {
        string json = JsonSerializer.Serialize(new { colors });
        if (json == _last) return;
        _last = json;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _last = null;
            RuntimeLog.Warn("COVER-COLORS", ex.Message);
        }
    }

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
