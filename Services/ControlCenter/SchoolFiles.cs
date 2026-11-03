using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services.ControlCenter;

/// <summary>
/// The PC side of the Pi's hand-in panel: an assignment's files land in Downloads\Schule\&lt;subject&gt; and open,
/// a POSE exercise opens through the pi-pose:// link of the dashboard's own setup tool, and photos become the
/// JPEG pages the maths hand-in takes (long edge 2000 px, quality 80, like the dashboard in the browser).
/// </summary>
internal static class SchoolFiles
{
    private const string LogTag = "SCHOOL-FILES";
    internal const int PageEdge = 2000;
    internal const int PageQuality = 80;

    // Opened straight away; anything else (an .exe in a task, say) is only shown in Explorer.
    private static readonly HashSet<string> Openable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".odt", ".rtf", ".txt", ".md", ".xls", ".xlsx", ".ods", ".csv", ".ppt", ".pptx", ".odp",
        ".png", ".jpg", ".jpeg", ".gif", ".zip", ".html", ".htm", ".java", ".py", ".sql", ".c", ".cpp", ".cs", ".json", ".xml"
    };

    internal static readonly string[] PhotoExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp"];

    /// <summary>"DBI 3AHIF Aistleitner 2627" → Downloads\Schule\DBI.</summary>
    internal static string FolderFor(string course)
    {
        string subject = SafeName((course ?? "").Trim().Split(' ', 2)[0], "Moodle");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Schule", subject);
    }

    /// <summary>A file name from Moodle made safe for the disk: no folders, no reserved characters.</summary>
    internal static string SafeName(string name, string fallback = "Angabe")
    {
        // Last path segment, then every reserved character (':' too, so "c:x" is no drive) becomes '_'.
        string file = (name ?? "").Replace('\\', '/').Split('/')[^1];
        foreach (char bad in Path.GetInvalidFileNameChars()) file = file.Replace(bad, '_');
        file = file.Trim().Trim('.');
        if (file.Length > 120) file = file[..(120 - Path.GetExtension(file).Length)] + Path.GetExtension(file);
        return file.Length == 0 ? fallback : file;
    }

    /// <summary>Writes the files; one already there with the same size is kept as it is (opened again, not copied).</summary>
    internal static List<string> Save(string folder, IReadOnlyList<(string Name, byte[] Bytes)> files)
    {
        Directory.CreateDirectory(folder);
        var paths = new List<string>(files.Count);
        foreach (var (name, bytes) in files)
        {
            string path = Path.Combine(folder, SafeName(name));
            if (File.Exists(path) && new FileInfo(path).Length != bytes.Length)
            {
                // A different file of the same name (a newer version of the task): keep both.
                string stem = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
                for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}){ext}");
            }
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>One document opens in its app; several (or one that is not a document) show in Explorer.</summary>
    internal static void Open(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        try
        {
            if (paths.Count == 1 && Openable.Contains(Path.GetExtension(paths[0])))
                Process.Start(new ProcessStartInfo(paths[0]) { UseShellExecute = true })?.Dispose();
            else
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{paths[0]}\"") { UseShellExecute = false })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            RuntimeLog.Warn(LogTag, $"Open failed: {ex.Message}");
        }
    }

    /// <summary>
    /// A POSE exercise: the dashboard's tools/pose-setup.mjs (registered for pi-pose://) makes the folder,
    /// restores the repo from the task's zip and opens IntelliJ. False when the link is not registered.
    /// </summary>
    internal static bool OpenPoseProject(int cmid)
    {
        if (cmid <= 0) return false;
        try
        {
            Process.Start(new ProcessStartInfo($"pi-pose://open/{cmid}") { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            RuntimeLog.Warn(LogTag, $"pi-pose link failed: {ex.Message}");
            return false;
        }
    }

    internal static bool IsPhoto(string path) => PhotoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Photos in page order: by name, numbers counted as numbers (IMG_2 before IMG_10).</summary>
    internal static List<string> InPageOrder(IEnumerable<string> paths)
    {
        var list = paths.Where(IsPhoto).ToList();
        list.Sort((a, b) => NaturalCompare(Path.GetFileName(a), Path.GetFileName(b)));
        return list;
    }

    private static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                int c = long.Parse(a[si..i]).CompareTo(long.Parse(b[sj..j]));
                if (c != 0) return c;
                continue;
            }
            int d = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
            if (d != 0) return d;
            i++;
            j++;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    /// <summary>Size of a page with its long edge at most <paramref name="maxEdge"/>; never enlarged.</summary>
    internal static (int Width, int Height) Fit(int width, int height, int maxEdge)
    {
        double scale = Math.Min(1.0, maxEdge / (double)Math.Max(1, Math.Max(width, height)));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// One photo as a hand-in page: turned upright by its camera orientation, scaled down, on white (a
    /// transparent PNG would turn black in a JPEG) and JPEG encoded. Null when the file is no readable image.
    /// </summary>
    internal static byte[]? ToJpegPage(string path, int maxEdge = PageEdge, int quality = PageQuality)
    {
        try
        {
            BitmapFrame frame;
            using (var stream = File.OpenRead(path))
                frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];

            BitmapSource image = frame;
            int turn = Orientation(frame) switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
            // shortcut: mirrored orientations (2, 4, 5, 7) are not unmirrored; phones do not write them.
            if (turn != 0) image = new TransformedBitmap(image, new RotateTransform(turn));
            var (w, h) = Fit(image.PixelWidth, image.PixelHeight, maxEdge);
            if (w != image.PixelWidth) image = new TransformedBitmap(image, new ScaleTransform(w / (double)image.PixelWidth, h / (double)image.PixelHeight));

            var page = OnWhite(image);
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(page));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException)
        {
            RuntimeLog.Warn(LogTag, $"Not a usable photo: {Path.GetFileName(path)} ({ex.GetType().Name})");
            return null;
        }
    }

    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            return frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort value ? value : 1;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException)
        {
            return 1;
        }
    }

    /// <summary>The image over a white page, as opaque 24-bit pixels.</summary>
    private static BitmapSource OnWhite(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        var rgb = new byte[w * 3 * h];
        for (int p = 0, q = 0; p < pixels.Length; p += 4, q += 3)
        {
            int a = pixels[p + 3];
            rgb[q] = (byte)((pixels[p] * a + 255 * (255 - a)) / 255);
            rgb[q + 1] = (byte)((pixels[p + 1] * a + 255 * (255 - a)) / 255);
            rgb[q + 2] = (byte)((pixels[p + 2] * a + 255 * (255 - a)) / 255);
        }
        var page = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, rgb, w * 3);
        page.Freeze();
        return page;
    }
}
