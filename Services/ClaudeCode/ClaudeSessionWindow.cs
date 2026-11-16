using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Automation;

namespace VNotch.Services.ClaudeCode;

/// <summary>
/// Brings a Claude Code session to the front: the terminal tab it runs in, or the desktop app or editor
/// that started it.
/// </summary>
internal static class ClaudeSessionWindow
{
    private const uint GA_ROOTOWNER = 3;
    private const int SW_RESTORE = 9;
    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const int MaxAncestors = 8;
    private const string TerminalWindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    private const string PseudoConsoleClass = "PseudoConsoleWindow";
    // AttachConsole is per process: one console at a time, shared with ClaudeSessionInput.
    internal static readonly object ConsoleGate = new();

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);
    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("kernel32.dll")]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    /// <summary>False when the session's process or its window is gone.</summary>
    public static bool TryActivate(string sessionId, string? title)
    {
        IntPtr window = FindWindow(sessionId);
        if (window == IntPtr.Zero) return false;

        if (Win32Interop.IsIconic(window)) ShowWindow(window, SW_RESTORE);
        // Allowed without tricks: the click that asked for it was the last input.
        Win32Interop.SetForegroundWindow(window);
        if (title is { Length: > 0 } && Win32Interop.WindowClassContains(window, TerminalWindowClass))
            _ = Task.Run(() => SelectTerminalTab(window, title));
        return true;
    }

    /// <summary>The window the session shows in: its terminal, or the app that started it. Zero when it is gone.</summary>
    public static IntPtr FindWindow(string sessionId)
    {
        int processId = FindProcessId(sessionId);
        if (processId == 0) return IntPtr.Zero;
        IntPtr window = ConsoleHostWindow(processId);
        return window != IntPtr.Zero ? window : AncestorWindow(processId);
    }

    /// <summary>
    /// True while that window is in front and, in Windows Terminal, the session's tab is the selected one:
    /// the terminal's window title follows its selected tab, which Claude Code titles with the session title.
    /// </summary>
    public static bool IsInFront(IntPtr window, string? title)
    {
        if (window == IntPtr.Zero || Win32Interop.GetForegroundWindow() != window) return false;
        if (title is not { Length: > 0 } || !Win32Interop.WindowClassContains(window, TerminalWindowClass)) return true;
        var text = new StringBuilder(512);
        return Win32Interop.GetWindowText(window, text, 512) > 0 && text.ToString().Contains(title, StringComparison.Ordinal);
    }

    /// <summary>Claude Code keeps a small file per running session, named after its process id.</summary>
    internal static int FindProcessId(string sessionId)
    {
        string directory = Path.Combine(ClaudeCodeSetup.ClaudeDirectory, "sessions");
        if (!Directory.Exists(directory)) return 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                if (JsonNode.Parse(File.ReadAllText(file)) is not JsonObject entry) continue;
                if (entry["sessionId"] is not JsonValue id || !id.TryGetValue(out string? value) || value != sessionId) continue;
                return entry["pid"] is JsonValue pid && pid.TryGetValue(out int processId) && IsRunning(processId) ? processId : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                RuntimeLog.Warn("CLAUDE", $"Session file {Path.GetFileName(file)} unreadable: {ex.Message}");
            }
        }
        return 0;
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The window that shows the process's console. Windows Terminal draws consoles itself: the console's own
    /// window is then a hidden stand-in owned by the terminal's window.
    /// </summary>
    private static IntPtr ConsoleHostWindow(int processId)
    {
        lock (ConsoleGate)
        {
            // Fails when the session has no console (desktop app), so the parent chain decides.
            if (!AttachConsole((uint)processId)) return IntPtr.Zero;
            try
            {
                IntPtr console = GetConsoleWindow();
                if (console == IntPtr.Zero) return IntPtr.Zero;
                IntPtr owner = GetAncestor(console, GA_ROOTOWNER);
                IntPtr window = owner != IntPtr.Zero ? owner : console;
                return Win32Interop.IsWindowVisible(window) && !Win32Interop.WindowClassContains(window, PseudoConsoleClass)
                    ? window
                    : IntPtr.Zero;
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    /// <summary>The first window up the parent chain: the desktop app or editor that started Claude Code.</summary>
    private static IntPtr AncestorWindow(int processId)
    {
        var processes = ProcessTable();
        int current = processId;
        for (int depth = 0; depth < MaxAncestors; depth++)
        {
            if (!processes.TryGetValue(current, out var entry)) break;
            current = entry.Parent;
            if (current <= 4 || !processes.TryGetValue(current, out var parent)) break;
            // Explorer only launched the console; its windows are not the session's.
            if (string.Equals(parent.Exe, "explorer.exe", StringComparison.OrdinalIgnoreCase)) break;
            IntPtr window = MainWindowOf(current);
            if (window != IntPtr.Zero) return window;
        }
        return IntPtr.Zero;
    }

    private static IntPtr MainWindowOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainWindowHandle;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return IntPtr.Zero;
        }
    }

    private static Dictionary<int, (int Parent, string Exe)> ProcessTable()
    {
        var table = new Dictionary<int, (int Parent, string Exe)>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return table;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (bool ok = Process32FirstW(snapshot, ref entry); ok; ok = Process32NextW(snapshot, ref entry))
                table[(int)entry.ProcessId] = ((int)entry.ParentProcessId, entry.ExeFile);
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return table;
    }

    /// <summary>Picks the session's tab, which Claude Code titles with the session title after a spinner.</summary>
    private static void SelectTerminalTab(IntPtr window, string title)
    {
        try
        {
            var tabs = AutomationElement.FromHandle(window).FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            if (tabs.Count < 2) return;
            foreach (AutomationElement tab in tabs)
            {
                if (!tab.Current.Name.Contains(title, StringComparison.Ordinal)) continue;
                if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object pattern))
                    ((SelectionItemPattern)pattern).Select();
                return;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            RuntimeLog.Warn("CLAUDE", $"Terminal tab not selected: {ex.Message}");
        }
    }
}
