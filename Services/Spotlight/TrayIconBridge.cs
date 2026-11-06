using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using static VNotch.Services.Win32Interop;

namespace VNotch.Services.Spotlight;

/// <summary>An app with an icon in the notification area, as Spotlight's "in the background" row shows it.</summary>
internal sealed record TrayApp(string Name, ImageSource? Icon)
{
    /// <summary>The program's name without the status a tooltip carries; <see cref="Name"/> stays the lookup key.</summary>
    public string DisplayName { get; init; } = Name;
}

/// <summary>
/// Reads the taskbar's notification area through UI Automation and opens an app's own tray menu there.
/// The tray itself is hidden by the taskbar styling, but its buttons stay in the automation tree, and
/// IUIAutomationElement3.ShowContextMenu opens the app's menu exactly like a right click would.
/// </summary>
internal static unsafe partial class TrayIconBridge
{
    private const string LogTag = "TRAY-BRIDGE";
    private const string NotifyIconSettingsKey = @"Control Panel\NotifyIconSettings";
    private const string AppButtonId = "NotifyItemIcon";
    private static readonly Guid ClsidCuiAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid IidIuiAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
    private static readonly Guid IidIuiAutomationElement3 = new("8471DF34-AEE0-4A01-A7DE-7DB9AF12C296");

    // vtable slots (IUnknown takes 0-2).
    private const int SlotElementFromHandle = 6;     // IUIAutomation
    private const int SlotCreateTrueCondition = 21;  // IUIAutomation
    private const int SlotFindAll = 6;               // IUIAutomationElement
    private const int SlotCurrentName = 23;          // IUIAutomationElement
    private const int SlotCurrentAutomationId = 29;  // IUIAutomationElement
    private const int SlotShowContextMenu = 91;      // IUIAutomationElement3, after 82 + 6 inherited slots
    private const int SlotArrayLength = 3;           // IUIAutomationElementArray
    private const int SlotArrayGetElement = 4;       // IUIAutomationElementArray
    private const int TreeScopeDescendants = 4;

    /// <summary>Names of the apps whose icons sit in the notification area, in tray order.</summary>
    internal static IReadOnlyList<string> ListAppNames()
    {
        var names = new List<string>();
        try
        {
            VisitAppButtons((_, name) => { names.Add(name); return false; });
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogTag, $"Tray listing failed: {ex.Message}");
        }
        return names;
    }

    /// <summary>Opens the tray menu of the app with this name. Returns whether the menu was asked to open.</summary>
    internal static bool ShowMenu(string name)
    {
        bool shown = false;
        try
        {
            VisitAppButtons((element, current) =>
            {
                if (!string.Equals(current, name, StringComparison.Ordinal)) return false;
                Guid iid = IidIuiAutomationElement3;
                if (Marshal.QueryInterface(element, ref iid, out nint element3) != 0 || element3 == 0) return true;
                try
                {
                    shown = Call0(element3, SlotShowContextMenu) == 0;
                }
                finally { Marshal.Release(element3); }
                return true;
            });
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogTag, $"Tray menu for '{name}' failed: {ex.Message}");
        }
        return shown;
    }

    /// <summary>
    /// Tray apps that belong to a known program outside Windows' own folder, shown under the program's name
    /// with Windows' snapshot of the tray icon (else the program's icon). System entries (Bluetooth,
    /// Windows Security, backup reminders) and unknown tooltips stay out.
    /// </summary>
    internal static IReadOnlyList<TrayApp> ListApps(Func<string, bool> include, int max = 16)
    {
        var names = ListAppNames().Where(include).ToList();
        if (names.Count == 0) return Array.Empty<TrayApp>();
        var known = ReadKnownTrayPrograms();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var apps = new List<TrayApp>(names.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            var match = known.Where(k => NameMatches(name, k.Label)).OrderBy(k => k.Label.Length).FirstOrDefault();
            if (match == null || match.ExePath == null) continue;
            if (match.ExePath.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
            string display = DisplayName(name, match.Label);
            if (!seen.Add(display)) continue;
            apps.Add(new TrayApp(name, match.Icon()) { DisplayName = display });
            if (apps.Count >= max) break;
        }
        return apps;
    }

    /// <summary>"Tailscale: Connected. Click for options." shows as "Tailscale".</summary>
    internal static string DisplayName(string trayName, string programLabel)
    {
        string label = programLabel.Trim();
        if (label.Length > 0 && label.Length < trayName.Length && trayName.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            return trayName[..label.Length];
        int colon = trayName.IndexOf(':');
        return (colon > 0 ? trayName[..colon] : trayName).Trim();
    }

    /// <summary>Tray tooltips often carry a status after the app name ("Tailscale: Connected").</summary>
    internal static bool NameMatches(string trayName, string? programLabel)
    {
        if (string.IsNullOrWhiteSpace(programLabel)) return false;
        string label = programLabel.Trim();
        if (trayName.StartsWith(label, StringComparison.OrdinalIgnoreCase)) return true;
        return trayName.Length >= 4 && label.StartsWith(trayName, StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveKnownFolderPath(string path)
    {
        if (path.Length < 38 || path[0] != '{') return path;
        int close = path.IndexOf('}');
        if (close < 0 || !Guid.TryParse(path[1..close], out Guid folder)) return path;
        if (SHGetKnownFolderPath(ref folder, 0, 0, out nint raw) != 0) return path;
        try
        {
            string? root = Marshal.PtrToStringUni(raw);
            return root == null ? path : root + path[(close + 1)..];
        }
        finally { Marshal.FreeCoTaskMem(raw); }
    }

    private sealed record KnownTrayProgram(string Label, string? ExePath, byte[]? Snapshot)
    {
        private ImageSource? _icon;
        internal ImageSource? Icon()
        {
            if (_icon != null) return _icon;
            _icon = DecodeSnapshot(Snapshot) ?? (ExePath != null && File.Exists(ExePath) ? FileIconProvider.GetAppIcon(ExePath, small: false) : null);
            return _icon;
        }
    }

    private static List<KnownTrayProgram> ReadKnownTrayPrograms()
    {
        var programs = new List<KnownTrayProgram>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(NotifyIconSettingsKey);
            if (root == null) return programs;
            foreach (string sub in root.GetSubKeyNames())
            {
                using var key = root.OpenSubKey(sub);
                if (key?.GetValue("ExecutablePath") is not string rawPath) continue;
                string exe = ResolveKnownFolderPath(rawPath);
                byte[]? snapshot = key.GetValue("IconSnapshot") as byte[];
                if (key.GetValue("InitialTooltip") is string tip && tip.Length > 0)
                    programs.Add(new KnownTrayProgram(tip, exe, snapshot));
                foreach (string label in ProgramLabels(exe))
                    programs.Add(new KnownTrayProgram(label, exe, snapshot));
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Log(LogTag, $"Reading tray programs failed: {ex.Message}");
        }
        return programs;
    }

    private static IEnumerable<string> ProgramLabels(string exe)
    {
        if (File.Exists(exe))
        {
            FileVersionInfo? info = null;
            try { info = FileVersionInfo.GetVersionInfo(exe); } catch { /* unreadable binaries just fall back */ }
            if (!string.IsNullOrWhiteSpace(info?.FileDescription)) yield return info!.FileDescription!;
            if (!string.IsNullOrWhiteSpace(info?.ProductName)) yield return info!.ProductName!;
        }
        string name = Path.GetFileNameWithoutExtension(exe);
        if (name.Length > 0) yield return name;
    }

    private static ImageSource? DecodeSnapshot(byte[]? png)
    {
        if (png == null || png.Length < 8) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(png);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Walks the app buttons of the notification area until the visitor returns true.</summary>
    private static void VisitAppButtons(Func<nint, string, bool> visitor)
    {
        IntPtr tray = FindWindow("Shell_TrayWnd");
        if (tray == IntPtr.Zero) return;
        var type = Type.GetTypeFromCLSID(ClsidCuiAutomation, throwOnError: true)!;
        object automation = Activator.CreateInstance(type)!;
        nint unknown = Marshal.GetIUnknownForObject(automation);
        nint uia = 0, root = 0, condition = 0, array = 0;
        try
        {
            Guid iid = IidIuiAutomation;
            if (Marshal.QueryInterface(unknown, ref iid, out uia) != 0) return;
            if (CallOut(uia, SlotElementFromHandle, tray, out root) != 0 || root == 0) return;
            if (CallOut(uia, SlotCreateTrueCondition, out condition) != 0 || condition == 0) return;
            var findAll = (delegate* unmanaged[Stdcall]<nint, int, nint, nint*, int>)Slot(root, SlotFindAll);
            nint found;
            if (findAll(root, TreeScopeDescendants, condition, &found) != 0 || found == 0) return;
            array = found;
            int length;
            ((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(array, SlotArrayLength))(array, &length);
            var getElement = (delegate* unmanaged[Stdcall]<nint, int, nint*, int>)Slot(array, SlotArrayGetElement);
            for (int i = 0; i < length; i++)
            {
                nint element;
                if (getElement(array, i, &element) != 0 || element == 0) continue;
                try
                {
                    if (ReadString(element, SlotCurrentAutomationId) != AppButtonId) continue;
                    string name = FirstLine(ReadString(element, SlotCurrentName));
                    if (name.Length == 0) continue;
                    if (visitor(element, name)) return;
                }
                finally { Marshal.Release(element); }
            }
        }
        finally
        {
            if (array != 0) Marshal.Release(array);
            if (condition != 0) Marshal.Release(condition);
            if (root != 0) Marshal.Release(root);
            if (uia != 0) Marshal.Release(uia);
            Marshal.Release(unknown);
            Marshal.FinalReleaseComObject(automation);
        }
    }

    internal static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        int end = text.IndexOfAny(['\r', '\n']);
        return (end < 0 ? text : text[..end]).Trim();
    }

    private static nint Slot(nint comObject, int slot) => ((nint*)*(nint*)comObject)[slot];

    private static int Call0(nint self, int slot) =>
        ((delegate* unmanaged[Stdcall]<nint, int>)Slot(self, slot))(self);

    private static int CallOut(nint self, int slot, out nint result)
    {
        nint value;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(self, slot))(self, &value);
        result = value;
        return hr;
    }

    private static int CallOut(nint self, int slot, IntPtr arg, out nint result)
    {
        nint value;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(self, slot))(self, arg, &value);
        result = value;
        return hr;
    }

    private static string ReadString(nint element, int slot)
    {
        nint bstr;
        if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(element, slot))(element, &bstr) != 0 || bstr == 0)
            return string.Empty;
        try { return Marshal.PtrToStringBSTR(bstr); }
        finally { Marshal.FreeBSTR(bstr); }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);
}
