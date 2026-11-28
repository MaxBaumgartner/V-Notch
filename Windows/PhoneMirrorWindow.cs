using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.Phone;

namespace VNotch;

/// <summary>Where the closed notch sits, in device pixels of its screen.</summary>
internal readonly record struct NotchAnchor(int CenterX, int Bottom, int Width, double Scale, Int32Rect WorkArea, Int32Rect Monitor);

/// <summary>
/// The phone's screen hanging from the notch: scrcpy's picture with the phone's own round corners and no frame,
/// below a slim black strip that merges into the notch. It springs out of the notch; dragged away by the strip it
/// becomes a free window, dropped near the notch it docks again. The strip's buttons (on hover) switch to full
/// height, from the top of the screen to the bottom, and pull it back in; so does a click on the notch.
/// </summary>
internal sealed class PhoneMirrorWindow : Window
{
    private const double ScreenShare = 0.62;
    private const double GripHeight = 18;
    // The phone's screen corners, as a share of its width (Xiaomi 17 Ultra: about 8 %).
    private const double CornerShare = 0.08;
    private const double DockDistance = 90;
    private static readonly TimeSpan KeepWarm = TimeSpan.FromSeconds(30);

    private readonly Func<NotchAnchor?> _anchor;
    private readonly Grid _screenSlot = new() { Background = Brushes.Black };
    private readonly Border _close;
    private readonly Border _full;
    private readonly TextBlock _fullGlyph;
    private bool _fullscreen;
    private readonly DispatcherTimer _coolDown;
    private ScrcpyProcess? _scrcpy;
    private ScrcpyChild? _child;
    private Task<bool>? _ensuring;
    private CancellationTokenSource? _startCancel;
    private string? _startingSerial;
    private double _aspect = 1200.0 / 2608.0;
    private EventHandler? _frame;
    private readonly Func<bool> _motionAllowed;
    private SpringDoubleAnimation[]? _rectangleSprings;
    private readonly Stopwatch _springClock = new();
    private double _rectangleFrameTime;
    private Int32Rect _springTarget;
    private SpringMotion _rectangleMotion = Motion.Pop;
    private Action? _springDone;
    private IntPtr _hwnd;

    public PhoneMirrorWindow(Func<NotchAnchor?> anchor, Func<bool>? motionAllowed = null)
    {
        _anchor = anchor;
        _motionAllowed = motionAllowed ?? (() => true);
        IsVisibleChanged += (_, _) => RefreshMotionVisibility();
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Black;
        Title = "V-Notch Handy";
        SizeToContent = SizeToContent.Manual;

        var grip = new Grid { Height = GripHeight, Background = Brushes.Black, Cursor = Cursors.SizeAll };
        grip.Children.Add(new Border
        {
            Width = 34, Height = 4, CornerRadius = new CornerRadius(2),
            Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3D, 0x44)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        });
        _close = StripButton("\uE711", out _, rightMargin: 10);
        _close.MouseLeftButtonUp += (_, e) => { e.Handled = true; PullIn(); };
        _full = StripButton("\uE740", out _fullGlyph, rightMargin: 32);
        _full.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleFullscreen(); };
        grip.Children.Add(_full);
        grip.Children.Add(_close);
        grip.MouseEnter += (_, _) => FadeButtons(1);
        grip.MouseLeave += (_, _) => FadeButtons(0);
        grip.MouseLeftButtonDown += Grip_MouseLeftButtonDown;

        _screenSlot.SizeChanged += (_, _) => PlaceChild();
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(grip);
        Grid.SetRow(_screenSlot, 1);
        layout.Children.Add(_screenSlot);
        Content = layout;

        _coolDown = new DispatcherTimer(DispatcherPriority.Background) { Interval = KeepWarm };
        _coolDown.Tick += (_, _) =>
        {
            _coolDown.Stop();
            if (State == MirrorState.Hidden) StopScrcpy();
        };

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        // No frame line from DWM; the window's own region gives it the phone's corners.
        int noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(_hwnd, 34, ref noBorder, sizeof(int));
    }

    private static Border StripButton(string glyph, out TextBlock text, double rightMargin)
    {
        text = new TextBlock
        {
            Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 8,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC8, 0xCC, 0xD4)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var button = new Border
        {
            Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, rightMargin, 0),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromRgb(0x2A, 0x2C, 0x31)), Cursor = Cursors.Hand, Opacity = 0,
            Child = text
        };
        button.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return button;
    }

    public enum MirrorState { Hidden, Docked, Free }

    public MirrorState State { get; private set; } = MirrorState.Hidden;
    public bool IsOpen => State != MirrorState.Hidden;
    public string? Serial => _scrcpy?.Serial ?? _startingSerial;

    /// <summary>Raised when the mirror is fully pulled back in or scrcpy ended.</summary>
    public event Action? MirrorClosed;

    /// <summary>The phone's screen size in pixels, for the body's proportions.</summary>
    public void SetPhoneSize(int width, int height)
    {
        if (width > 0 && height > 0) _aspect = (double)Math.Min(width, height) / Math.Max(width, height);
    }

    /// <summary>Starts scrcpy ahead of a likely tap, so opening is instant; it stops again if nobody opens it.</summary>
    public void Prewarm(string serial)
    {
        if (State != MirrorState.Hidden) return;
        _ = EnsureScrcpyAsync(serial);
        _coolDown.Stop();
        _coolDown.Start();
    }

    public async void Open(string serial)
    {
        try { await OpenAsync(serial); }
        catch (Exception ex)
        {
            // Never let the mirror take the notch down with it.
            RuntimeLog.Warn("PHONE", $"Mirror failed: {ex.GetType().Name}: {ex.Message}");
            Shutdown();
            MirrorClosed?.Invoke();
        }
    }

    private async Task OpenAsync(string serial)
    {
        if (State != MirrorState.Hidden || _anchor() is not { } anchor) return;
        _coolDown.Stop();
        State = MirrorState.Docked;
        // At full height it reaches behind the notch, so it must not be topmost (the notch stays above it).
        Topmost = !_fullscreen;
        var target = DockedRect(anchor);
        var start = TuckedRect(anchor);
        if (_frame == null) PlaceNow(start);
        SetScreenVisible(_child != null);
        Show();
        // The body grows right away; the picture joins as soon as scrcpy is up (at once when it was warm).
        var ready = EnsureScrcpyAsync(serial);
        Spring(CurrentRect(), target, Motion.Pop, done: () =>
        {
            if (ready.IsCompletedSuccessfully && ready.Result) RevealScreen();
        });
        bool ok = await ready;
        if (State == MirrorState.Hidden) return;
        if (!ok)
        {
            PullIn();
            return;
        }
        RevealScreen();
    }

    /// <summary>Full height (top of the screen to the bottom, under the notch) or back to hanging from the notch.</summary>
    public void ToggleFullscreen()
    {
        if (State == MirrorState.Hidden || _anchor() is not { } anchor) return;
        _fullscreen = !_fullscreen;
        SetFullscreenGlyph();
        State = MirrorState.Docked;
        Topmost = !_fullscreen;
        Spring(CurrentRect(), DockedRect(anchor), Motion.Glide, done: RevealScreen);
    }

    private void SetFullscreenGlyph()
    {
        string glyph = _fullscreen ? "\uE73F" : "\uE740";
        if (!IsVisible || !_motionAllowed() || AnimationConfig.ReduceMotion)
        {
            Motion.Stop(_fullGlyph, OpacityProperty, 1);
            _fullGlyph.Text = glyph;
            return;
        }
        Motion.To(_fullGlyph, OpacityProperty, 0, Motion.Exit, done: () =>
        {
            _fullGlyph.Text = glyph;
            Motion.To(_fullGlyph, OpacityProperty, 1, Motion.Tile);
        });
    }

    /// <summary>Pulls the mirror back into the notch; scrcpy stays warm for a little while.</summary>
    public void PullIn()
    {
        if (State == MirrorState.Hidden) return;
        State = MirrorState.Hidden;
        var anchor = _anchor();
        if (anchor == null || !IsVisible)
        {
            Hide();
            MirrorClosed?.Invoke();
            return;
        }
        Topmost = true;
        Spring(CurrentRect(), TuckedRect(anchor.Value), Motion.PopBack, done: () =>
        {
            Hide();
            _coolDown.Stop();
            _coolDown.Start();
            MirrorClosed?.Invoke();
        });
    }

    /// <summary>Stops everything: on phone disconnect and when the notch quits.</summary>
    public void Shutdown()
    {
        StopFrame();
        _springDone = null;
        State = MirrorState.Hidden;
        Hide();
        StopScrcpy();
    }

    private Task<bool> EnsureScrcpyAsync(string serial)
    {
        if (_scrcpy is { HasExited: false } running && running.Serial == serial) return Task.FromResult(true);
        if (_ensuring != null && _startingSerial == serial) return _ensuring;
        return _ensuring = StartScrcpyAsync(serial);
    }

    private async Task<bool> StartScrcpyAsync(string serial)
    {
        StopScrcpy();
        var anchor = _anchor();
        double scale = anchor?.Scale ?? 1;
        var size = ScreenSize(anchor);
        var cancel = _startCancel = new CancellationTokenSource();
        _startingSerial = serial;
        ScrcpyProcess? started;
        try
        {
            started = await ScrcpyProcess.StartAsync(serial, (int)(size.Width * scale), (int)(size.Height * scale), cancel.Token);
        }
        finally
        {
            _ensuring = null;
            _startingSerial = null;
        }
        if (started == null) return false;
        if (cancel.IsCancellationRequested)
        {
            started.Dispose();
            return false;
        }
        // The adopted picture joins the live resize as soon as it is ready.
        var child = await ScrcpyChild.AdoptAsync(started.Window, _hwnd);
        if (child == null || cancel.IsCancellationRequested)
        {
            started.Dispose();
            return false;
        }
        _scrcpy = started;
        _child = child;
        started.Exited += () => Dispatcher.BeginInvoke(() =>
        {
            if (_scrcpy != started) return;
            RuntimeLog.Info("PHONE", "scrcpy ended");
            Shutdown();
            MirrorClosed?.Invoke();
        });
        return true;
    }

    private void StopScrcpy()
    {
        _startCancel?.Cancel();
        _startCancel = null;
        _child?.Show(false);
        _child = null;
        _scrcpy?.Dispose();
        _scrcpy = null;
    }

    private void RevealScreen()
    {
        if (State == MirrorState.Hidden || !IsVisible) return;
        SetScreenVisible(true);
        // Settling an obscured resize must not steal focus from the fullscreen app.
        if (_motionAllowed()) Activate();
    }

    private void SetScreenVisible(bool visible)
    {
        if (_child == null) return;
        if (visible) PlaceChild();
        _child.Show(visible);
    }

    /// <summary>Lays scrcpy's window over the screen slot, in this window's device pixels.</summary>
    private void PlaceChild()
    {
        if (_child == null || _screenSlot.ActualWidth <= 0 || Content is not UIElement root) return;
        var source = PresentationSource.FromVisual(this);
        double scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        Point at = _screenSlot.TranslatePoint(new Point(0, 0), root);
        _child.Place((int)Math.Round(at.X * scale), (int)Math.Round(at.Y * scale),
            (int)Math.Round(_screenSlot.ActualWidth * scale), (int)Math.Round(_screenSlot.ActualHeight * scale));
    }

    private void FadeButtons(double to)
    {
        foreach (var button in new[] { _close, _full })
        {
            if (button.IsVisible && _motionAllowed()) Motion.To(button, OpacityProperty, to, Motion.Exit);
            else Motion.Stop(button, OpacityProperty, to);
        }
    }

    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (State == MirrorState.Hidden || _frame != null || _fullscreen) return;
        e.Handled = true;
        try { DragMove(); }
        catch (InvalidOperationException) { return; }
        if (_anchor() is not { } anchor) return;
        var now = CurrentRect();
        double centerX = now.X + now.Width / 2.0;
        double distance = Math.Sqrt(Math.Pow(centerX - anchor.CenterX, 2) + Math.Pow(now.Y - anchor.Bottom, 2));
        if (distance <= DockDistance * anchor.Scale)
        {
            State = MirrorState.Docked;
            Topmost = true;
            Spring(now, DockedRect(anchor), Motion.Pop, done: null);
        }
        else
        {
            State = MirrorState.Free;
            Topmost = false;
        }
    }

    private Size ScreenSize(NotchAnchor? anchor)
    {
        double scale = anchor?.Scale ?? 1;
        double workHeight = (anchor?.WorkArea.Height ?? 1040) / scale;
        double height = Math.Round(workHeight * ScreenShare);
        return new Size(Math.Round(height * _aspect), height);
    }

    private Int32Rect DockedRect(NotchAnchor anchor)
    {
        if (_fullscreen)
        {
            // Top of the screen down to the taskbar (it would hide the phone's own buttons); the strip and the phone's
            // status bar sit behind and beside the notch.
            int fullHeight = anchor.WorkArea.Y + anchor.WorkArea.Height - anchor.Monitor.Y;
            int screenHeight = fullHeight - (int)Math.Round(GripHeight * anchor.Scale);
            int fullWidth = (int)Math.Round(screenHeight * _aspect);
            return new Int32Rect(anchor.CenterX - fullWidth / 2, anchor.Monitor.Y, fullWidth, fullHeight);
        }
        var screen = ScreenSize(anchor);
        int width = (int)Math.Round(screen.Width * anchor.Scale);
        int height = (int)Math.Round((screen.Height + GripHeight) * anchor.Scale);
        // Tucked a few pixels under the notch so the two read as one shape.
        int top = anchor.Bottom - (int)Math.Round(2 * anchor.Scale);
        return new Int32Rect(anchor.CenterX - width / 2, top, width, height);
    }

    private static Int32Rect TuckedRect(NotchAnchor anchor)
    {
        int width = Math.Max(40, (int)(anchor.Width * 0.8));
        int height = (int)Math.Round(6 * anchor.Scale);
        return new Int32Rect(anchor.CenterX - width / 2, anchor.Bottom - height, width, height);
    }

    private Int32Rect CurrentRect()
    {
        GetWindowRect(_hwnd, out var r);
        return new Int32Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    private void PlaceNow(Int32Rect rect)
    {
        int width = Math.Max(1, rect.Width), height = Math.Max(1, rect.Height);
        SetWindowPos(_hwnd, IntPtr.Zero, rect.X, rect.Y, width, height, SWP_NOZORDER | SWP_NOACTIVATE);
        // The phone's round corners on the whole window (scrcpy's picture inside is clipped by it too).
        int radius = (int)Math.Min(Math.Round(width * CornerShare), height / 2.0);
        IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2);
        if (SetWindowRgn(_hwnd, region, true) == 0) DeleteObject(region);
    }

    /// <summary>
    /// Moves the native rectangle with the same spring as the notch, retaining speed on a reversal.
    /// </summary>
    private void Spring(Int32Rect from, Int32Rect to, SpringMotion spring, Action? done, Vector anchorShift = default)
    {
        double[] value = [from.X + from.Width / 2.0, from.Y, from.Width, from.Height];
        double[] goal = [to.X + to.Width / 2.0, to.Y, to.Width, to.Height];
        double[] velocity = new double[4];
        if (_rectangleSprings is { } previous && _frame != null)
        {
            for (int i = 0; i < 4; i++)
            {
                value[i] = RectangleValue(previous[i], _rectangleFrameTime);
                velocity[i] = previous[i].VelocityAt(_rectangleFrameTime);
            }
            value[0] += anchorShift.X;
            value[1] += anchorShift.Y;
        }
        StopFrame();
        _springTarget = to;
        _rectangleMotion = spring;
        _springDone = done;
        if (AnimationConfig.ReduceMotion || !IsVisible || !_motionAllowed())
        {
            PlaceNow(to);
            CompleteSpring();
            return;
        }
        // The same closed-form springs as the notch preserve speed when a resize reverses.
        _rectangleSprings = new SpringDoubleAnimation[4];
        for (int i = 0; i < 4; i++) _rectangleSprings[i] = spring.Animate(value[i], goal[i], velocity[i]);
        _rectangleFrameTime = 0;
        _springClock.Restart();
        double last = -1;
        _frame = (_, _) =>
        {
            if (!IsVisible || !_motionAllowed() || AnimationConfig.ReduceMotion) { RefreshMotionVisibility(); return; }
            double now = _springClock.Elapsed.TotalSeconds;
            if (now - last < 1.0 / AnimationConfig.TargetFps) return;
            last = now;
            _rectangleFrameTime = now;
            bool settled = true;
            for (int i = 0; i < 4; i++)
            {
                value[i] = RectangleValue(_rectangleSprings[i], now);
                if (now < _rectangleSprings[i].Duration.TimeSpan.TotalSeconds) settled = false;
            }
            if (settled)
            {
                StopFrame();
                PlaceNow(to);
                CompleteSpring();
                return;
            }
            int width = Math.Max(1, (int)Math.Round(value[2])), height = Math.Max(1, (int)Math.Round(value[3]));
            PlaceNow(new Int32Rect((int)Math.Round(value[0] - width / 2.0), (int)Math.Round(value[1]), width, height));
        };
        CompositionTarget.Rendering += _frame;
    }

    private static double RectangleValue(SpringDoubleAnimation spring, double time) =>
        time >= spring.Duration.TimeSpan.TotalSeconds ? spring.To : spring.ValueAt(time);

    internal void RefreshNotchAnchor()
    {
        if (!IsVisible || State == MirrorState.Free || _anchor() is not { } anchor) return;
        // A tuck still owns the window until it reaches the notch and hides it.
        if (State == MirrorState.Hidden && _frame == null) return;
        var target = State == MirrorState.Hidden ? TuckedRect(anchor) : DockedRect(anchor);
        if (target == _springTarget) return;
        if (_frame == null)
        {
            PlaceNow(_springTarget = target);
            return;
        }

        // The attachment moves with the notch edge. The rectangle keeps its velocity relative to that edge.
        var shift = new Vector(target.X + target.Width / 2.0 - (_springTarget.X + _springTarget.Width / 2.0),
            target.Y - _springTarget.Y);
        var current = CurrentRect();
        var shifted = new Int32Rect(current.X + (int)Math.Round(shift.X), current.Y + (int)shift.Y,
            current.Width, current.Height);
        if (shifted != current) PlaceNow(shifted);
        Spring(shifted, target, _rectangleMotion, _springDone, shift);
    }

    public void RefreshMotionVisibility()
    {
        bool animate = IsVisible && _motionAllowed() && !AnimationConfig.ReduceMotion;
        if (!animate)
        {
            Motion.Stop(_fullGlyph, OpacityProperty, 1);
            _fullGlyph.Text = _fullscreen ? "\uE73F" : "\uE740";
            Motion.Stop(_close, OpacityProperty, _close.IsMouseOver ? 1 : 0);
            Motion.Stop(_full, OpacityProperty, _full.IsMouseOver ? 1 : 0);
        }
        if (_frame == null || animate) return;
        StopFrame();
        PlaceNow(_springTarget);
        CompleteSpring();
    }

    private void CompleteSpring()
    {
        var done = _springDone;
        _springDone = null;
        done?.Invoke();
    }

    private void StopFrame()
    {
        if (_frame != null) CompositionTarget.Rendering -= _frame;
        _frame = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        Shutdown();
        base.OnClosed(e);
    }

    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
