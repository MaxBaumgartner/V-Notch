using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VNotch.Services;

/// <summary>
/// A layered window lets clicks on its fully transparent pixels fall through to the windows below by itself. A
/// window made see-through by DWM instead (see MainWindow.Transparency) takes every click inside its rectangle,
/// so this watches the cursor and makes the window click-through (WS_EX_TRANSPARENT) wherever nothing visible
/// is under it. "Visible" follows the same rule as the layered window did: anything drawn, except transparent
/// backgrounds and elements faded to nothing.
/// </summary>
internal sealed class ClickThroughController : IDisposable
{
    private const int NearIntervalMs = 15;
    private const int FarIntervalMs = 120;
    // Poll quickly once the cursor is this close to the window, so the style is right before it arrives.
    private const int NearMarginPx = 32;

    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer _timer;
    private bool? _applied;
    private bool _disposed;

    public ClickThroughController(Window window, IntPtr hwnd)
    {
        _window = window;
        _hwnd = hwnd;
        // Render priority: at Input priority the timer starved while an animation was running, leaving the
        // window solid for a moment after the cursor left the notch.
        _timer = new DispatcherTimer(DispatcherPriority.Render, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(FarIntervalMs),
        };
        _timer.Tick += (_, _) => Update();
        // Windows lets mouse input fall through a WS_EX_TRANSPARENT top-level window only when it is also layered
        // (MainWindow.Transparency makes it so). WPF rewrites the extended style from its own cache now and then,
        // so the hook keeps the transparent bit as this controller last set it.
        HwndSource.FromHwnd(hwnd)?.AddHook(KeepStyle);
        SetClickThrough(true);
        _timer.Start();
    }

    /// <summary>Re-evaluates at once, e.g. after the notch changed shape under a resting cursor.</summary>
    public void Update()
    {
        if (_disposed || !GetCursorPos(out var cursor) || !GetWindowRect(_hwnd, out var rect)) return;

        bool near = cursor.X >= rect.Left - NearMarginPx && cursor.X < rect.Right + NearMarginPx &&
                    cursor.Y >= rect.Top - NearMarginPx && cursor.Y < rect.Bottom + NearMarginPx;
        var interval = TimeSpan.FromMilliseconds(near ? NearIntervalMs : FarIntervalMs);
        if (_timer.Interval != interval) _timer.Interval = interval;

        bool inside = cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;
        bool solid = inside && _window.IsVisible &&
                     IsSolidAt(_window, _window.PointFromScreen(new Point(cursor.X, cursor.Y)));

        // A press that started on the notch (a swipe, a drag) keeps the window solid until it is released.
        if (!solid && _applied == false && AnyButtonDown()) return;
        if (solid && _applied != false && Environment.GetEnvironmentVariable("VNOTCH_CLICK_DEBUG") == "1")
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vnotch-clickthrough.log"),
                $"{DateTime.Now:HH:mm:ss.fff} solid at {cursor.X},{cursor.Y}: {Describe(LastSolidHit)}{Environment.NewLine}");
        SetClickThrough(!solid);
    }

    /// <summary>The visual that made the last solid verdict (for diagnostics).</summary>
    internal static DependencyObject? LastSolidHit { get; private set; }

    /// <summary>True when something visible is drawn at <paramref name="point"/> (in the root's coordinates).</summary>
    internal static bool IsSolidAt(Visual root, Point point)
    {
        bool solid = false;
        VisualTreeHelper.HitTest(root,
            // VisualTreeHelper.HitTest, unlike input hit testing, ignores IsHitTestVisible and visibility.
            candidate => candidate is UIElement element &&
                         (!element.IsHitTestVisible || element.Visibility != Visibility.Visible || element.Opacity <= 0.01)
                ? HitTestFilterBehavior.ContinueSkipSelfAndChildren
                : HitTestFilterBehavior.Continue,
            result =>
            {
                if (!Draws(result.VisualHit)) return HitTestResultBehavior.Continue;
                solid = true;
                LastSolidHit = result.VisualHit;
                return HitTestResultBehavior.Stop;
            },
            new PointHitTestParameters(point));
        return solid;
    }

    // A hit on a panel, border or control comes from its background; a transparent one draws nothing.
    private static bool Draws(DependencyObject hit) => hit switch
    {
        Panel panel => IsVisibleBrush(panel.Background),
        Border border => IsVisibleBrush(border.Background) || IsVisibleBrush(border.BorderBrush),
        Control control => IsVisibleBrush(control.Background),
        Shape shape => IsVisibleBrush(shape.Fill) || IsVisibleBrush(shape.Stroke),
        _ => true,
    };

    private static string Describe(DependencyObject? hit)
    {
        var parts = new System.Collections.Generic.List<string>();
        for (var node = hit; node != null && parts.Count < 8; node = VisualTreeHelper.GetParent(node))
            parts.Add(node is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name) ? $"{node.GetType().Name}#{fe.Name}" : node.GetType().Name);
        return string.Join(" < ", parts);
    }

    private static bool IsVisibleBrush(Brush? brush) => brush switch
    {
        null => false,
        SolidColorBrush solid => solid.Color.A > 0 && solid.Opacity > 0,
        _ => brush.Opacity > 0,
    };

    private void SetClickThrough(bool clickThrough)
    {
        if (_applied == clickThrough) return;
        _applied = clickThrough;
        int style = GetWindowLong(_hwnd, GWL_EXSTYLE);
        int next = Desired(style);
        if (next != style) SetWindowLong(_hwnd, GWL_EXSTYLE, next);
    }

    private int Desired(int style)
    {
        return _applied == true ? style | WS_EX_TRANSPARENT : style & ~WS_EX_TRANSPARENT;
    }

    private IntPtr KeepStyle(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_STYLECHANGING || (int)wParam != GWL_EXSTYLE || _disposed) return IntPtr.Zero;
        var change = Marshal.PtrToStructure<StyleStruct>(lParam);
        int desired = Desired(change.StyleNew);
        if (desired != change.StyleNew)
        {
            change.StyleNew = desired;
            Marshal.StructureToPtr(change, lParam, false);
        }
        return IntPtr.Zero;
    }

    private static bool AnyButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
    }

    private const int GWL_EXSTYLE = -20, WM_STYLECHANGING = 0x007C;
    private const int WS_EX_TRANSPARENT = 0x20;

    [StructLayout(LayoutKind.Sequential)] private struct StyleStruct { public int StyleOld, StyleNew; }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
}
