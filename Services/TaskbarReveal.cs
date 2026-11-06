using System.Runtime.InteropServices;

namespace VNotch.Services;

/// <summary>
/// Brings an auto-hidden taskbar up over the windows while a Win tap has Spotlight open, as the Start menu does.
/// Windows keeps an auto-hidden taskbar up while it is active or while the active window is one of its own, and
/// that is what Spotlight becomes for as long as it is open: it is lent to the taskbar as an owned window, the
/// taskbar is activated (it slides up) and Spotlight takes the keyboard back. The auto-hide setting itself stays
/// as it is: pausing it would shrink the work area and make every maximized window jump.
/// </summary>
internal static partial class TaskbarReveal
{
    private const string LogTag = "TASKBAR-REVEAL";
    private const uint AbmGetState = 4;
    private const uint AbmGetTaskbarPos = 5;
    private const uint AbeBottom = 3;
    private const uint SwpNoSize = 0x1, SwpNoZOrder = 0x4, SwpNoActivate = 0x10;
    private const uint AutoHideFlag = 1;
    private const int GwlpHwndParent = -8;

    /// <summary>A window lent to the taskbar, with the owner it goes back to.</summary>
    internal sealed record Loan(nint Window, nint Owner, nint Taskbar);

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int Size;
        public nint Window;
        public uint CallbackMessage;
        public uint Edge;
        public int Left, Top, Right, Bottom;
        public nint Param;
    }

    [LibraryImport("shell32.dll")]
    private static partial nuint SHAppBarMessage(uint message, ref AppBarData data);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial nint SetWindowLongPtr(nint window, int index, nint value);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>Whether a taskbar state from <c>ABM_GETSTATE</c> is auto-hide: only then is there something to bring up.</summary>
    internal static bool AutoHides(uint state) => (state & AutoHideFlag) != 0;

    /// <summary>Makes <paramref name="window"/> one of the taskbar's own windows, if the taskbar auto-hides.</summary>
    internal static Loan? Lend(nint window)
    {
        if (window == 0) return null;
        var data = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
        if (!AutoHides((uint)SHAppBarMessage(AbmGetState, ref data))) return null;
        nint taskbar = Win32Interop.FindWindow("Shell_TrayWnd");
        if (taskbar == 0) return null;
        nint owner = GetWindowLongPtr(window, GwlpHwndParent);
        SetWindowLongPtr(window, GwlpHwndParent, taskbar);
        RuntimeLog.Log(LogTag, $"Taskbar shown while Spotlight is open (owner 0x{owner:X} -> 0x{GetWindowLongPtr(window, GwlpHwndParent):X})");
        return new Loan(window, owner, taskbar);
    }

    /// <summary>Activates the taskbar so it slides up; the lent window takes the keyboard back right after.</summary>
    internal static void ActivateTaskbar(Loan loan) => Win32Interop.SetForegroundWindow(loan.Taskbar);

    /// <summary>
    /// Gives the window back to its own owner as Spotlight starts closing, and asks the taskbar to slide away first
    /// (see <see cref="HideNow"/>): it goes with the closing animation instead of half a second after it.
    /// </summary>
    internal static void GiveBack(Loan loan)
    {
        nint current = GetWindowLongPtr(loan.Window, GwlpHwndParent);
        if (current != loan.Taskbar)
        {
            RuntimeLog.Log(LogTag, $"Taskbar loan already ended (owner 0x{current:X})");
            return;
        }
        string? stays = HideNow(loan.Taskbar);
        SetWindowLongPtr(loan.Window, GwlpHwndParent, loan.Owner);
        RuntimeLog.Log(LogTag, stays == null ? "Taskbar asked to slide away" : $"Taskbar left to Windows ({stays})");
    }

    /// <summary>
    /// Windows waits about half a second before an auto-hidden taskbar slides away. Asked to move to where it rests
    /// hidden, it often starts sooner (the move itself it does not take, so nothing jumps). Not while the taskbar is
    /// active or under the mouse: then it stays up, as it would anyway.
    /// </summary>
    private static string? HideNow(nint taskbar)
    {
        bool placed = GetWindowRect(taskbar, out Rect bar) & GetCursorPos(out Point cursor);
        string? stays = Win32Interop.GetForegroundWindow() == taskbar ? "active"
            : !placed ? "no position"
            : IsInside(cursor.X, cursor.Y, bar.Left, bar.Top, bar.Right, bar.Bottom) ? "under the mouse"
            : !AtBottom() ? "not at the bottom"
            : null;
        if (stays != null) return stays;
        SetWindowPos(taskbar, 0, bar.Left, HiddenTop(bar.Bottom), 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
        return null;
    }

    private static bool AtBottom()
    {
        var data = new AppBarData { Size = Marshal.SizeOf<AppBarData>() };
        SHAppBarMessage(AbmGetTaskbarPos, ref data);
        return data.Edge == AbeBottom;
    }

    /// <summary>Where a shown bottom taskbar rests when hidden: two pixels left on screen to catch the mouse.</summary>
    internal static int HiddenTop(int shownBottom) => shownBottom - 2;

    internal static bool IsInside(int x, int y, int left, int top, int right, int bottom) =>
        x >= left && x < right && y >= top && y < bottom;

    /// <summary>A window for the log: its handle, class and process, never its title.</summary>
    internal static string Describe(nint window)
    {
        if (window == 0) return "none";
        var name = new System.Text.StringBuilder(128);
        Win32Interop.GetClassName(window, name, name.Capacity);
        return $"0x{window:X} {name} {FullscreenDetector.TryGetProcessName(window)}";
    }
}
