using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VNotch.Services;

namespace VNotch;

/// <summary>
/// The notch window is see-through by DWM composition rather than by WPF's per-pixel-alpha layered window
/// (AllowsTransparency). WPF draws such a window on the GPU, copies every frame back to memory and hands it to
/// UpdateLayeredWindow; composed by DWM, WPF presents from the GPU and DWM blends the window's alpha. With music
/// playing that took the opening on a 360 Hz screen from about 150 to 190 frames a second, animations unchanged.
/// The window still carries WS_EX_LAYERED (constant alpha 255, set while it is created) because Windows only lets
/// clicks fall through a layered window; ClickThroughController decides where. VNOTCH_LAYERED=1 brings back
/// WPF's layered window.
/// </summary>
public partial class MainWindow
{
    private ClickThroughController? _clickThrough;

    /// <summary>Must run before the window gets its handle (right after InitializeComponent).</summary>
    private void InitializeTransparency()
    {
        if (Environment.GetEnvironmentVariable("VNOTCH_LAYERED") == "1")
        {
            AllowsTransparency = true;
            return;
        }

        // Click-through needs a layered window (see ClickThroughController). WPF clears WS_EX_LAYERED from windows
        // without per-pixel opacity, so the bit is added as the window is created and a subclass keeps it there.
        InstallLayeredCreationHook();

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
                target.BackgroundColor = Colors.Transparent;
            // Blur-behind with an empty, off-window region: DWM then honours the window's per-pixel alpha
            // without blurring anything behind it.
            IntPtr region = CreateRectRgn(-2, -2, -1, -1);
            var blur = new DwmBlurBehind { Flags = DWM_BB_ENABLE | DWM_BB_BLURREGION, Enable = true, BlurRegion = region };
            DwmEnableBlurBehindWindow(hwnd, ref blur);
            DeleteObject(region);
            int doNotRound = 1, noBorder = unchecked((int)0xFFFFFFFE);
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
            RemoveLayeredCreationHook();
            // Belt and braces: a layered window without its alpha is invisible.
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
            _clickThrough = new ClickThroughController(this, hwnd);
        };
        Closed += (_, _) => _clickThrough?.Dispose();
    }

    // One static hook procedure, so the delegate Windows calls can never be collected; it unhooks itself as soon
    // as it has seen the notch window being created.
    private static IntPtr _creationHook;
    private static string? _layeredTitle;
    private static readonly HookProc CreationHookProc = OnCreationHook;

    private void InstallLayeredCreationHook()
    {
        _layeredTitle = Title;
        if (_creationHook == IntPtr.Zero)
            _creationHook = SetWindowsHookEx(WH_CBT, CreationHookProc, IntPtr.Zero, GetCurrentThreadId());
    }

    private static IntPtr OnCreationHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code == HCBT_CREATEWND && _layeredTitle != null)
        {
            var create = Marshal.PtrToStructure<CbtCreateWnd>(lParam);
            var cs = Marshal.PtrToStructure<CreateStruct>(create.CreateStruct);
            // WPF gives a window without a taskbar button a hidden owner, so Parent is not empty here.
            if (cs.Name != IntPtr.Zero && Marshal.PtrToStringUni(cs.Name) == _layeredTitle)
            {
                // The window exists now (wParam) but nothing presents to it yet, so it may still become layered;
                // the subclass keeps WPF's later style rewrites from taking the bit away again.
                SetWindowLong(wParam, GWL_EXSTYLE, GetWindowLong(wParam, GWL_EXSTYLE) | WS_EX_LAYERED);
                SetLayeredWindowAttributes(wParam, 0, 255, LWA_ALPHA);
                SetWindowSubclass(wParam, KeepLayeredProc, UIntPtr.Zero, UIntPtr.Zero);
                RemoveLayeredCreationHook();
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static void RemoveLayeredCreationHook()
    {
        _layeredTitle = null;
        if (_creationHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_creationHook);
        _creationHook = IntPtr.Zero;
    }

    // WPF's HwndTarget clears WS_EX_LAYERED on every style change unless the window uses per-pixel opacity (the
    // slow UpdateLayeredWindow path). Setting the bit again after WPF has handled the change keeps it. A window
    // that turns layered starts invisible until it gets its alpha, so the alpha is set again whenever that happens.
    private static readonly SubclassProc KeepLayeredProc = (hwnd, msg, wParam, lParam, id, data) =>
    {
        IntPtr result = DefSubclassProc(hwnd, msg, wParam, lParam);
        if ((int)wParam != GWL_EXSTYLE) return result;
        var change = Marshal.PtrToStructure<StyleStruct>(lParam);
        if (msg == WM_STYLECHANGING && (change.StyleNew & WS_EX_LAYERED) == 0)
        {
            change.StyleNew |= WS_EX_LAYERED;
            Marshal.StructureToPtr(change, lParam, false);
        }
        else if (msg == WM_STYLECHANGED && (change.StyleOld & WS_EX_LAYERED) == 0 && (change.StyleNew & WS_EX_LAYERED) != 0)
        {
            SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        }
        return result;
    };

    private const int WH_CBT = 5, HCBT_CREATEWND = 3, WS_EX_LAYERED = 0x80000, GWL_EXSTYLE = -20, LWA_ALPHA = 0x2, WM_STYLECHANGING = 0x007C, WM_STYLECHANGED = 0x007D;
    private delegate IntPtr SubclassProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);
    [StructLayout(LayoutKind.Sequential)] private struct StyleStruct { public int StyleOld, StyleNew; }
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct CbtCreateWnd { public IntPtr CreateStruct; public IntPtr InsertAfter; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CreateStruct
    {
        public IntPtr CreateParams, Instance, Menu, Parent;
        public int Cy, Cx, Y, X, Style;
        public IntPtr Name, Class;
        public int ExStyle;
    }
    // The W entry point: an ANSI hook would hand over ANSI window names.
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")] private static extern IntPtr SetWindowsHookEx(int hookType, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    private const int DWM_BB_ENABLE = 0x1, DWM_BB_BLURREGION = 0x2;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmBlurBehind { public int Flags; public bool Enable; public IntPtr BlurRegion; public bool TransitionOnMaximized; }
    [DllImport("dwmapi.dll")] private static extern int DwmEnableBlurBehindWindow(IntPtr hwnd, ref DwmBlurBehind blur);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);
}
