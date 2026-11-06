using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using VNotch.Services.ControlCenter;
using Windows.Devices.Radios;

namespace VNotch.Controls;

/// <summary>Which part of the control center an instance shows.</summary>
public enum ControlCenterPage
{
    /// <summary>Wired and Wi-Fi network, Bluetooth and Wallpaper Engine.</summary>
    System,
    /// <summary>Claude Code sessions, approvals, questions and plan limits.</summary>
    Claude,
    /// <summary>The user's Raspberry Pi dashboard: Home Assistant, school, to-dos.</summary>
    Pi,
    /// <summary>A Lenovo Legion's power modes, key locks and temperatures.</summary>
    Legion
}

/// <summary>
/// The quick settings that live in Windows' notification area, in the notch: wired and Wi-Fi network,
/// Bluetooth devices and Wallpaper Engine on one tab, Claude Code sessions on another. Built in code with
/// the same rows, headers and pickers as <see cref="AudioMixerView"/> so the tabs look alike.
/// </summary>
public partial class ControlCenterView : UserControl, IDisposable
{
    private const double ColName = 196;
    private const double ColAction = 206;
    private const double RowHeight = 34;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan UsageViewMinAge = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan UsageHookMinAge = TimeSpan.FromMinutes(5);

    private static readonly Brush Track = Frozen("#4D4D4D");
    private static readonly Brush Muted = Frozen("#8C8C8C");
    private static readonly Brush ComboBg = Frozen("#33000000");
    private static readonly Brush ComboBorder = Frozen("#26FFFFFF");
    private static readonly Brush ComboHover = Frozen("#1FFFFFFF");
    private static readonly Brush HeaderText = Frozen("#8A8A8A");
    private static readonly Brush MenuBg = Frozen("#F21C1C1C");
    private static readonly Brush KnobBrush = Frozen("#FFFFFF");
    private static readonly Brush OnGreenText = Frozen("#0B0B0B");
    private static readonly Brush WaitingYellow = Frozen("#FFD60A");
    private static readonly Brush WorkingOrange = Frozen("#D97757");
    private static readonly Brush CardBg = Frozen("#0FFFFFFF");
    private static readonly Brush CardHover = Frozen("#1CFFFFFF");

    private static readonly FontFamily SegoeSymbolFont = new("pack://application:,,,/Fonts/#Segoe MDL2 Assets, Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol, Segoe UI");
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");

    private readonly WallpaperEngineControl _wallpaper = new();
    private readonly SystemStatsSampler _stats = new();
    private readonly ProcessLoadSampler _processes = new();
    private bool _gpuSampling;
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly Dictionary<string, bool> _expanded = new(StringComparer.Ordinal);
    private readonly List<(TextBlock Label, DateTime StartedUtc)> _elapsedLabels = new();
    private readonly List<WifiNetwork> _wifiScratch = new();
    // Picks on a question card, by request id, kept while other sessions redraw the list.
    private readonly Dictionary<string, Dictionary<string, List<string>>> _questionPicks = new(StringComparer.Ordinal);
    // Session and request cards on the page, so only new ones animate in on a rebuild.
    private readonly HashSet<string> _shownCards = new(StringComparer.Ordinal);
    private readonly List<(string Key, FrameworkElement Element)> _cardScratch = new();
    // The session a message is being typed for, and what is typed so far (kept across rebuilds).
    private string? _composingSessionId;
    private string _composeDraft = "";
    private TextBox? _composerBox;
    private bool _rebuildAfterComposing;
    // Rings around busy sessions' dots; they ripple only while the tab is on screen.
    private readonly List<System.Windows.Shapes.Ellipse> _ripples = new();
    private const int RippleFps = 30;

    private ClaudeSessionTracker? _claude;
    private ClaudeHookServer? _hookServer;
    private ClaudeUsageClient? _usage;
    private Func<bool> _isLocalOnly = () => true;
    private Func<bool> _usesGlass = () => false;

    private Radio? _wifiRadio;
    private Radio? _bluetoothRadio;
    private bool _radiosLookedUp;
    private bool _refreshing;
    private bool _disposed;
    private Snapshot? _snapshot;
    private bool _claudeInstalled;
    private bool _revealRequestFirst;
    private FrameworkElement? _firstRequestCard;

    private string _networkKey = "";
    private string _bluetoothKey = "";
    private string _wallpaperKey = "";
    private string _audioKey = "";
    private string _notificationKey = "";
    // The output in use before the last switch, so the swap button goes back to it.
    private string? _previousOutputId;
    private string _claudeKey = "";

    private readonly StackPanel _statsSection = new();
    private readonly StackPanel _networkSection = new();
    private readonly StackPanel _bluetoothSection = new();
    private readonly StackPanel _audioSection = new();
    private readonly StackPanel _notificationSection = new();
    private readonly StackPanel _wallpaperSection = new();
    private readonly StackPanel _claudeSection = new();

    private FontFamily? _fontCached;
    private FontFamily ControlFont => _fontCached ??= (FontFamily)FindResource("ControlSystemFont");

    public ControlCenterView()
    {
        InitializeComponent();
        ControlRoot.Children.Add(_statsSection);
        ControlRoot.Children.Add(_laptopSection);
        ControlRoot.Children.Add(_networkSection);
        ControlRoot.Children.Add(_bluetoothSection);
        ControlRoot.Children.Add(_audioSection);
        ControlRoot.Children.Add(_notificationSection);
        ControlRoot.Children.Add(_wallpaperSection);
        ControlRoot.Children.Add(_claudeSection);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _refreshTimer.Tick += (_, _) => Refresh();
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateElapsedLabels();

        IsVisibleChanged += OnIsVisibleChanged;
        Loc.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>Set in XAML; fixed once the view is built.</summary>
    public ControlCenterPage Page { get; set; }

    /// <summary>Raised when a Claude Code request waits for an answer, so the shell can open this tab.</summary>
    public event Action? AttentionRequested;

    /// <summary>Raised after the Claude page was rebuilt, so the shell can fit the notch to it.</summary>
    public event Action? ContentHeightChanged;

    /// <summary>Raised when a session's window was brought to the front from its card.</summary>
    public event Action? SessionOpened;

    /// <summary>Raised with the device id when an audio output is picked; the mixer makes it the default.</summary>
    public event Action<string>? OutputDeviceSelected;

    /// <summary>True while a message is typed here: the notch then has to take keyboard focus.</summary>
    public event Action<bool>? KeyboardInputRequested;

    /// <summary>For the <see cref="ControlCenterPage.System"/> page.</summary>
    internal void Attach(Func<bool> usesGlass)
    {
        _usesGlass = usesGlass;
        RefreshOnceShellIsReady();
    }

    /// <summary>
    /// Reads once up front so the first open shows rows instead of filling in, but only after the window
    /// finished starting: the callbacks read its settings, which are not loaded yet while it attaches us.
    /// </summary>
    private void RefreshOnceShellIsReady() => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));

    /// <summary>For the <see cref="ControlCenterPage.Claude"/> page.</summary>
    internal void AttachClaude(ClaudeSessionTracker tracker, ClaudeHookServer hookServer, ClaudeUsageClient usage,
        Func<bool> isLocalOnly, Func<bool> usesGlass)
    {
        _claude = tracker;
        _hookServer = hookServer;
        _usage = usage;
        _isLocalOnly = isLocalOnly;
        _usesGlass = usesGlass;
        tracker.Changed += OnClaudeChanged;
        tracker.PermissionRequested += OnPermissionRequested;
        tracker.UsageMayHaveChanged += OnUsageMayHaveChanged;
        _claudeInstalled = ClaudeCodeSetup.IsInstalled(ClaudeHookServer.HookUrl);
        RefreshOnceShellIsReady();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer.Stop();
        _clockTimer.Stop();
        _piTimer?.Stop();
        SetGpuSampling(false);
        _stats.Dispose();
        IsVisibleChanged -= OnIsVisibleChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        if (_claude != null)
        {
            _claude.Changed -= OnClaudeChanged;
            _claude.PermissionRequested -= OnPermissionRequested;
            _claude.UsageMayHaveChanged -= OnUsageMayHaveChanged;
        }
        GC.SuppressFinalize(this);
    }

    #region Lifecycle

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (IsVisible)
        {
            Refresh();
            if (Page == ControlCenterPage.Claude)
            {
                RefreshUsage(UsageViewMinAge);
                _clockTimer.Start();
                StartRipples();
            }
            else if (Page == ControlCenterPage.Pi)
            {
                OnPiVisibleChanged(true);
            }
            else if (Page == ControlCenterPage.Legion)
            {
                OnLegionVisibleChanged(true);
            }
            else
            {
                _refreshTimer.Start();
                SetGpuSampling(true);
            }
        }
        else
        {
            _refreshTimer.Stop();
            _clockTimer.Stop();
            StopRipples();
            SetGpuSampling(false);
            if (Page == ControlCenterPage.System)
            {
                _stats.Reset();
                _processes.Reset();
                _topMode = TopMode.Off;
            }
            CloseMenu();
            if (_composingSessionId != null) StopComposing();
            if (Page == ControlCenterPage.Pi) OnPiVisibleChanged(false);
            if (Page == ControlCenterPage.Legion) OnLegionVisibleChanged(false);
        }
    }

    private void OnLanguageChanged() => Dispatcher.BeginInvoke(() =>
    {
        _networkKey = _bluetoothKey = _audioKey = _notificationKey = _wallpaperKey = _claudeKey = _laptopKey = "";
        _statsTiles = null;
        _piKey = _legionKey = "";
        if (_snapshot != null) ApplySnapshot(_snapshot);
        BuildClaudeSection();
        BuildPiPage();
        BuildLegionPage();
    });

    private void OnClaudeChanged() => Dispatcher.BeginInvoke(DispatcherPriority.Background, BuildClaudeSection);

    private void OnPermissionRequested(ClaudePermissionRequest request) => Dispatcher.BeginInvoke(() =>
    {
        // Below a few sessions the card can sit out of view: show it first.
        _revealRequestFirst = true;
        if (IsVisible) Dispatcher.BeginInvoke(DispatcherPriority.Background, ScrollToRequest);
        AttentionRequested?.Invoke();
    });

    private void ScrollToRequest()
    {
        _revealRequestFirst = false;
        if (_firstRequestCard == null) return;
        ControlRoot.UpdateLayout();
        try
        {
            double y = _firstRequestCard.TransformToAncestor(ControlRoot).Transform(new Point(0, 0)).Y;
            // Keep the session row above the card in view.
            ControlScrollViewer.ScrollToVerticalOffset(Math.Max(0, y - RowHeight - 16));
        }
        catch (InvalidOperationException)
        {
            // Not laid out yet; the list simply stays where it is.
        }
    }

    private void OnUsageMayHaveChanged() => Dispatcher.BeginInvoke(() => RefreshUsage(UsageHookMinAge));

    /// <summary>Reads everything again off the UI thread and patches the sections that changed.</summary>
    public async void Refresh()
    {
        if (_disposed) return;
        if (Page == ControlCenterPage.Claude)
        {
            // Hook events redraw this page; nothing to poll.
            BuildClaudeSection();
            return;
        }
        if (Page == ControlCenterPage.Pi)
        {
            RefreshPi();
            return;
        }
        if (Page == ControlCenterPage.Legion)
        {
            RefreshLegion();
            return;
        }
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            if (!_radiosLookedUp)
            {
                _radiosLookedUp = true;
                _wifiRadio = await RadioSwitch.FindAsync(RadioKind.WiFi);
                _bluetoothRadio = await RadioSwitch.FindAsync(RadioKind.Bluetooth);
            }

            bool? wifiOn = _wifiRadio == null ? null : _wifiRadio.State == RadioState.On;
            bool? bluetoothOn = _bluetoothRadio == null ? null : _bluetoothRadio.State == RadioState.On;
            bool withStats = IsVisible;
            var top = _topMode;
            var snapshot = await Task.Run(() => ReadSnapshot(wifiOn, bluetoothOn, rescanWifi: false, withStats, top));
            if (_disposed) return;
            ApplySnapshot(snapshot);
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("CONTROL-CENTER", $"Refresh failed: {ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async void RefreshUsage(TimeSpan minAge)
    {
        if (_usage == null || _isLocalOnly() || !ClaudeUsageClient.HasCredentials) return;
        try
        {
            if (await _usage.RefreshAsync(minAge) != null) BuildClaudeSection();
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("CONTROL-CENTER", $"Usage refresh failed: {ex.Message}");
        }
    }

    private void RefreshSoon(int delayMs)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Refresh();
        };
        timer.Start();
    }

    #endregion

    #region Snapshot

    private sealed class Snapshot
    {
        public bool? WifiOn;
        public WifiListStatus WifiStatus;
        public List<WifiNetwork> Networks = new();
        public List<WiredAdapter> Wired = new();
        public bool? BluetoothOn;
        public List<BluetoothDevice> Devices = new();
        public WallpaperEngineState Wallpaper;
        public bool WallpaperMuted;
        public List<AudioDeviceInfo> Outputs = new();
        public bool? MicMuted;
        public string? MicName;
        public SystemStats? Stats;
        public List<ProcessLoad>? Top;
        public LaptopStatus? Laptop;
    }

    private Snapshot ReadSnapshot(bool? wifiOn, bool? bluetoothOn, bool rescanWifi, bool withStats, TopMode top)
    {
        var s = new Snapshot { WifiOn = wifiOn, BluetoothOn = bluetoothOn };
        if (withStats) s.Stats = _stats.Sample();
        if (withStats && top != TopMode.Off) s.Top = _processes.Top(TopProcessCount, byMemory: top == TopMode.Memory);
        s.Wired = NetworkControl.GetWiredAdapters();
        if (wifiOn != false)
        {
            lock (_wifiScratch)
            {
                s.WifiStatus = NetworkControl.GetNetworks(_wifiScratch, rescanWifi);
                s.Networks = new List<WifiNetwork>(_wifiScratch);
            }
        }
        if (bluetoothOn != false) s.Devices = BluetoothControl.Paired();
        s.Wallpaper = _wallpaper.GetState();
        s.WallpaperMuted = _wallpaper.IsMuted;
        s.Outputs = AudioMixerService.EnumerateOutputDevices();
        s.MicMuted = MicrophoneControl.IsMuted();
        foreach (var input in AudioMixerService.EnumerateInputDevices())
        {
            if (input.IsDefault) s.MicName = input.FriendlyName;
        }
        if (withStats) s.Laptop = LaptopControl.Read();
        return s;
    }

    private void ApplySnapshot(Snapshot s)
    {
        _snapshot = s;
        if (_statsTiles == null) BuildStatsSection();
        if (s.Stats is { } stats) ApplyStats(stats);
        UpdateTopList(s.Top);
        if (s.Stats != null) BuildLaptopSectionIfChanged(s.Laptop);

        string networkKey = NetworkKey(s);
        if (networkKey != _networkKey)
        {
            _networkKey = networkKey;
            BuildNetworkSection(s);
        }

        string bluetoothKey = BluetoothKey(s);
        if (bluetoothKey != _bluetoothKey)
        {
            _bluetoothKey = bluetoothKey;
            BuildBluetoothSection(s);
        }

        var audioKey = new StringBuilder().Append(IsExpanded("audio")).Append('|').Append(s.MicMuted).Append(s.MicName).Append('|');
        foreach (var d in s.Outputs) audioKey.Append(d.Id).Append(d.IsDefault).Append(';');
        if (audioKey.ToString() != _audioKey)
        {
            _audioKey = audioKey.ToString();
            BuildAudioSection(s);
        }

        BuildNotificationSectionIfChanged();

        string wallpaperKey = $"{s.Wallpaper}|{s.WallpaperMuted}|{IsExpanded("wallpaper")}";
        if (wallpaperKey != _wallpaperKey)
        {
            _wallpaperKey = wallpaperKey;
            BuildWallpaperSection(s);
        }
    }

    private string NetworkKey(Snapshot s)
    {
        var sb = new StringBuilder();
        sb.Append(IsExpanded("network")).Append('|').Append(s.WifiOn).Append('|').Append(s.WifiStatus).Append('|');
        foreach (var w in s.Wired) sb.Append(w.Name).Append(w.IsUp).Append(';');
        foreach (var n in s.Networks)
            if (n.IsConnected) sb.Append(n.Ssid);
        return sb.ToString();
    }

    private string BluetoothKey(Snapshot s)
    {
        var sb = new StringBuilder();
        sb.Append(IsExpanded("bluetooth")).Append('|').Append(s.BluetoothOn).Append('|');
        foreach (var d in s.Devices) sb.Append(d.Address).Append(d.IsConnected).Append(d.Battery).Append(d.CanConnect).Append(';');
        return sb.ToString();
    }

    #endregion

    #region System monitor

    private sealed class StatTile
    {
        public required TextBlock Value;
        public TextBlock? Detail;
        public ScaleTransform? Fill;
        public Border? FillBar;
        public Brush Tint = Blue;
        public RollingNumber? Roll;
    }

    private sealed class StatTiles
    {
        public required StatTile Cpu, Gpu, Ram, Ping, Net;
    }

    private StatTiles? _statsTiles;
    private static readonly Brush StatWarm = Frozen("#FFD60A");

    /// <summary>Five tiles built once; every refresh only changes their numbers and slides their bars.</summary>
    private void BuildStatsSection()
    {
        _statsSection.Children.Clear();
        _statsSection.Children.Add(BuildSectionHeader("system", Loc.Get("control.system"), "", null, topMargin: 0));
        _statsTiles = null;
        if (!IsExpanded("system"))
        {
            // Collapsed: a placeholder so the section is not rebuilt on every refresh.
            _statsTiles = new StatTiles { Cpu = Dummy(), Gpu = Dummy(), Ram = Dummy(), Ping = Dummy(), Net = Dummy() };
            return;
        }

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2), Tag = TileGridTag };
        for (int i = 0; i < 9; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(8) });
        _statsTiles = new StatTiles
        {
            Cpu = AddStatTile(grid, 0, "CPU", withBar: true, () => ToggleTop(TopMode.Cpu), Blue),
            Gpu = AddStatTile(grid, 2, "GPU", withBar: true, () => Launch("taskmgr.exe"), Purple),
            Ram = AddStatTile(grid, 4, "RAM", withBar: true, () => ToggleTop(TopMode.Memory), Orange),
            Ping = AddStatTile(grid, 6, "Ping", withBar: false, null, Mint),
            Net = AddStatTile(grid, 8, Loc.Get("control.network"), withBar: false, () => Launch("ms-settings:network-status"), Teal)
        };
        _statsSection.Children.Add(grid);
        _topList.Children.Clear();
        _statsSection.Children.Add(_topList);

        static StatTile Dummy() => new() { Value = new TextBlock() };
    }

    private StatTile AddStatTile(Grid grid, int column, string label, bool withBar, Action? onClick, Brush tint)
    {
        var stack = new StackPanel();
        stack.Children.Add(MakeLabel(label, 11, tint));
        var value = MakeLabel("–", 15, VNotch.Services.UiPalette.PrimaryBrush);
        value.Margin = new Thickness(0, 3, 0, 0);
        stack.Children.Add(value);
        var tile = new StatTile { Value = value, Tint = tint, Roll = new RollingNumber(v => value.Text = $"{Math.Round(v)} %") };
        if (withBar)
        {
            var fill = new ScaleTransform(0, 1);
            var bar = new Border { Background = new SolidColorBrush(((SolidColorBrush)tint).Color), CornerRadius = new CornerRadius(1.5), RenderTransform = fill, RenderTransformOrigin = new Point(0, 0.5) };
            stack.Children.Add(new Grid
            {
                Height = 3,
                Margin = new Thickness(0, 7, 0, 1),
                Children = { new Border { Background = Track, CornerRadius = new CornerRadius(1.5) }, bar }
            });
            tile.Fill = fill;
            tile.FillBar = bar;
        }
        else
        {
            var detail = MakeLabel("", 10.5, Muted);
            detail.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(detail);
            tile.Detail = detail;
        }
        var card = Card();
        card.Padding = new Thickness(12, 9, 12, 9);
        card.Child = stack;
        if (onClick != null)
        {
            // CPU, GPU and RAM lead to Task Manager, the network tile to its settings.
            card.Cursor = Cursors.Hand;
            Pressable(card, () => CardBg, CardHover);
            card.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };
        }
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
        return tile;
    }

    private void ApplyStats(SystemStats stats)
    {
        if (_statsTiles is not { } tiles || !IsExpanded("system")) return;
        SetLoad(tiles.Cpu, stats.CpuPercent);
        SetLoad(tiles.Gpu, stats.GpuPercent);
        SetLoad(tiles.Ram, stats.RamPercent);
        tiles.Ram.Value.ToolTip = $"{stats.RamUsedGb:0.0} / {stats.RamTotalGb:0.0} GB";

        tiles.Ping.Value.Text = stats.PingMs is { } ping ? $"{ping} ms" : "–";
        tiles.Ping.Value.Foreground = stats.PingMs switch
        {
            null => Muted,
            < 50 => Mint,
            < 100 => StatWarm,
            _ => WorkingOrange
        };
        tiles.Ping.Detail!.Text = "1.1.1.1";

        tiles.Net.Value.Text = "↓ " + FormatRate(stats.DownBytesPerSecond);
        tiles.Net.Detail!.Text = "↑ " + FormatRate(stats.UpBytesPerSecond);
    }

    private static void SetLoad(StatTile tile, double? percent)
    {
        if (percent is { } p && tile.Roll != null) tile.Roll.To(p);
        else tile.Value.Text = "–";
        if (tile.Fill == null || tile.FillBar == null) return;
        double target = Math.Clamp((percent ?? 0) / 100, 0, 1);
        var hot = percent >= 90 ? Red : percent >= 75 ? StatWarm : tile.Tint;
        if (tile.FillBar.Background is SolidColorBrush { IsFrozen: false } barFill && hot is SolidColorBrush hotFill)
            WeatherMotion.Tint(barFill, hotFill.Color, 300);
        if (AnimationConfig.ReduceMotion)
        {
            tile.Fill.ScaleX = target;
            return;
        }
        var slide = new DoubleAnimation(target, new Duration(TimeSpan.FromMilliseconds(700)))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(slide, RippleFps);
        tile.Fill.BeginAnimation(ScaleTransform.ScaleXProperty, slide, HandoffBehavior.SnapshotAndReplace);
    }

    private enum TopMode
    {
        Off,
        Cpu,
        Memory
    }

    private const int TopProcessCount = 5;
    private static readonly TimeSpan EndConfirmWindow = TimeSpan.FromSeconds(3);
    private static readonly Brush EndConfirmBg = Frozen("#33FF453A");
    private readonly StackPanel _topList = new() { Margin = new Thickness(0, 8, 0, 0) };
    private TopMode _topMode;
    // The app whose End button was pressed once; a second press within the window ends it.
    private string? _endArmed;
    private DateTime _endArmedUntil;
    private bool _topListShown;
    private List<ProcessLoad>? _lastTop;

    /// <summary>CPU and RAM tiles open the apps that use the most of it; the same tile closes the list again.</summary>
    private void ToggleTop(TopMode mode)
    {
        _topMode = _topMode == mode ? TopMode.Off : mode;
        _endArmed = null;
        if (_topMode == TopMode.Off)
        {
            _processes.Reset();
            UpdateTopList(null);
            return;
        }
        RefreshSoon(0);
    }

    private void UpdateTopList(List<ProcessLoad>? top)
    {
        if (_topMode == TopMode.Off || top == null)
        {
            if (_topMode == TopMode.Off)
            {
                _topList.Children.Clear();
                _topListShown = false;
            }
            return;
        }

        _lastTop = top;
        _topList.Children.Clear();
        var card = Card();
        card.Padding = new Thickness(14, 8, 10, 8);
        var rows = new StackPanel();
        rows.Children.Add(TopRow(Loc.Get(_topMode == TopMode.Cpu ? "control.top.byCpu" : "control.top.byMemory"),
            "CPU", "RAM", header: true, load: null));
        foreach (var load in top) rows.Children.Add(TopRow(load.Title, $"{load.CpuPercent:0.0} %", FormatBytes(load.MemoryBytes), header: false, load));
        card.Child = rows;
        _topList.Children.Add(card);
        // Falls in when it opens; later refreshes only change the numbers.
        if (!_topListShown && IsVisible && !AnimationConfig.ReduceMotion) RevealElement(card, TimeSpan.Zero);
        _topListShown = true;
    }

    private FrameworkElement TopRow(string name, string cpu, string memory, bool header, ProcessLoad? load)
    {
        var grid = new Grid { Height = header ? 22 : 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        Brush emphasis = header ? Muted : VNotch.Services.UiPalette.PrimaryBrush;
        var title = MakeLabel(name, header ? 11 : 12.5, emphasis);
        if (load is { Instances: > 1 } many) title.ToolTip = $"{many.Name} × {many.Instances}";
        grid.Children.Add(title);
        var cpuLabel = MakeLabel(cpu, header ? 11 : 12, header || _topMode != TopMode.Cpu ? Muted : emphasis);
        cpuLabel.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(cpuLabel, 1);
        grid.Children.Add(cpuLabel);
        var memoryLabel = MakeLabel(memory, header ? 11 : 12, header || _topMode != TopMode.Memory ? Muted : emphasis);
        memoryLabel.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(memoryLabel, 2);
        grid.Children.Add(memoryLabel);

        if (load is { CanEnd: true } app)
        {
            bool armed = _endArmed == app.Name && DateTime.UtcNow < _endArmedUntil;
            var end = TextButton(Loc.Get(armed ? "control.top.confirm" : "control.top.end"), accent: false, () => EndApp(app.Name));
            end.Height = 24;
            end.MinWidth = 76;
            end.Margin = new Thickness(0);
            if (armed)
            {
                end.Background = EndConfirmBg;
                ((TextBlock)end.Child).Foreground = WorkingOrange;
            }
            Grid.SetColumn(end, 3);
            grid.Children.Add(end);
        }
        return grid;
    }

    /// <summary>The first press arms the button ("Sure?"), the second within a few seconds ends the app.</summary>
    private async void EndApp(string name)
    {
        if (_endArmed != name || DateTime.UtcNow >= _endArmedUntil)
        {
            _endArmed = name;
            _endArmedUntil = DateTime.UtcNow + EndConfirmWindow;
            UpdateTopList(_lastTop);
            return;
        }
        _endArmed = null;
        await Task.Run(() => ProcessLoadSampler.End(name));
        RefreshSoon(200);
    }

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        _ => $"{bytes / (double)(1L << 20):0} MB"
    };

    internal static string FormatRate(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{bytesPerSecond / (1024 * 1024):0.0} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:0} KB/s",
        _ => $"{bytesPerSecond:0} B/s"
    };

    /// <summary>The GPU sampler is shared and counts its users: one start per open tab, one stop when it closes.</summary>
    private void SetGpuSampling(bool on)
    {
        if (on == _gpuSampling || Page != ControlCenterPage.System) return;
        _gpuSampling = on;
        try
        {
            if (on) GpuMonitorService.Instance.Start();
            else GpuMonitorService.Instance.Stop();
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    #endregion

    #region Network

    private void BuildNetworkSection(Snapshot s)
    {
        _accent = Blue;
        CloseMenu();
        _networkSection.Children.Clear();
        _networkSection.Children.Add(BuildSectionHeader("network", Loc.Get("control.network"), Loc.Get("control.status"), null, topMargin: 14));
        if (!IsExpanded("network")) return;

        // Wired: what the PC uses whenever the cable is in, Wi-Fi or not.
        WiredAdapter? wired = null;
        foreach (var adapter in s.Wired)
        {
            if (wired == null || (adapter.IsUp && !wired.IsUp)) wired = adapter;
        }
        string wiredStatus = wired == null ? Loc.Get("control.noAdapter")
            : wired.IsUp ? Loc.Get("control.connected") : Loc.Get("control.notConnected");
        var wiredRow = NewRow("\uE839", Loc.Get("control.wired"));
        AddStatus(wiredRow, wiredStatus, wired?.IsUp == true ? Blue : Muted);
        if (wired != null) AddAction(wiredRow, InfoChip("\uE839", wired.Description));
        _networkSection.Children.Add(wiredRow);

        var wifiRow = NewRow("\uE701", Loc.Get("control.wifi"));
        if (s.WifiOn is bool on && _wifiRadio != null)
        {
            var radio = _wifiRadio;
            AddStatus(wifiRow, on ? Loc.Get("control.on") : Loc.Get("control.off"), Muted,
                Toggle(on, value => SetRadio(radio, value)));
        }
        else
        {
            AddStatus(wifiRow, Loc.Get("control.noAdapter"), Muted);
        }

        WifiNetwork? current = null;
        foreach (var n in s.Networks)
            if (n.IsConnected) { current = n; break; }

        FrameworkElement picker = s.WifiOn == false
            ? Combo("\uE701", Loc.Get("control.wifiOff"), interactive: false, null)
            : s.WifiStatus == WifiListStatus.LocationBlocked
                ? Combo("\uE707", Loc.Get("control.wifiLocation"), interactive: true, _ => NetworkControl.OpenLocationSettings())
                : s.WifiStatus is WifiListStatus.NoAdapter or WifiListStatus.Failed && s.WifiOn == null
                    ? Combo("\uE701", Loc.Get("control.noAdapter"), interactive: false, null)
                    : Combo("\uE701", current?.Ssid ?? Loc.Get("control.notConnected"), interactive: true, ShowWifiMenu);
        AddAction(wifiRow, picker);
        _networkSection.Children.Add(wifiRow);
    }

    private async void SetRadio(Radio radio, bool on)
    {
        await RadioSwitch.SetAsync(radio, on);
        RefreshSoon(600);
    }

    private async void ShowWifiMenu(Border anchor)
    {
        // Ask the adapter for a fresh scan; the list fills in a moment later.
        var networks = await Task.Run(() =>
        {
            var list = new List<WifiNetwork>();
            NetworkControl.GetNetworks(list, rescan: true);
            return list;
        });
        if (_disposed) return;

        var items = new List<MenuItemSpec>();
        foreach (var n in networks)
        {
            var network = n;
            string glyph = n.Signal >= 75 ? "\uE701" : n.Signal >= 50 ? "\uE874" : n.Signal >= 25 ? "\uE873" : "\uE872";
            items.Add(new MenuItemSpec(n.Ssid, n.IsConnected, n.Secured ? "\uE72E" : glyph, () => ConnectWifi(network)));
        }
        if (items.Count == 0) items.Add(new MenuItemSpec(Loc.Get("control.noNetworks"), false, null, null));
        foreach (var n in networks)
        {
            if (!n.IsConnected) continue;
            items.Add(new MenuItemSpec(Loc.Get("control.disconnect"), false, "\uE711", () =>
            {
                Task.Run(NetworkControl.Disconnect);
                RefreshSoon(1500);
            }));
            break;
        }
        items.Add(new MenuItemSpec(Loc.Get("control.moreNetworks"), false, "\uE712", NetworkControl.OpenNetworkFlyout));
        ShowMenu(anchor, items, Blue);
    }

    private void ConnectWifi(WifiNetwork network)
    {
        if (network.IsConnected) return;
        if (network.ProfileName is not { } profile)
        {
            // A new network needs its password: Windows' own flyout asks for it.
            NetworkControl.OpenNetworkFlyout();
            return;
        }
        Task.Run(() => NetworkControl.Connect(profile));
        RefreshSoon(2500);
        RefreshSoon(6000);
    }

    #endregion

    #region Notifications

    private Func<NotificationPrefs>? _notificationPrefs;
    private Action<bool>? _setNotificationsEnabled;
    private Action<string, bool>? _setNotificationAppMuted;

    /// <summary>The shell owns the watcher and the settings; this tab shows and flips them.</summary>
    internal void AttachNotifications(Func<NotificationPrefs> prefs, Action<bool> setEnabled, Action<string, bool> setMuted)
    {
        _notificationPrefs = prefs;
        _setNotificationsEnabled = setEnabled;
        _setNotificationAppMuted = setMuted;
        _notificationKey = "";
    }

    /// <summary>A new app sent something: show it in the list if the tab is open.</summary>
    internal void NotificationAppsChanged()
    {
        if (IsVisible) BuildNotificationSectionIfChanged();
    }

    private void BuildNotificationSectionIfChanged()
    {
        if (_notificationPrefs == null) return;
        var prefs = _notificationPrefs();
        var key = new StringBuilder().Append(IsExpanded("notifications")).Append(prefs.Allowed).Append(prefs.Enabled).Append('|');
        foreach (var app in prefs.Apps) key.Append(app.Key).Append(app.Muted).Append(';');
        if (key.ToString() == _notificationKey) return;
        _notificationKey = key.ToString();
        BuildNotificationSection(prefs);
    }

    private void BuildNotificationSection(NotificationPrefs prefs)
    {
        _accent = Indigo;
        _notificationSection.Children.Clear();
        _notificationSection.Children.Add(BuildSectionHeader("notifications", Loc.Get("control.notifications"), Loc.Get("control.status"), null, topMargin: 14));
        if (!IsExpanded("notifications")) return;

        var main = NewRow("\uEA8F", Loc.Get("control.notifications.inNotch"));
        if (prefs.Allowed)
        {
            AddStatus(main, prefs.Enabled ? Loc.Get("control.on") : Loc.Get("control.off"), Muted,
                Toggle(prefs.Enabled, on =>
                {
                    _setNotificationsEnabled?.Invoke(on);
                    _notificationKey = "";
                    BuildNotificationSectionIfChanged();
                }));
        }
        else
        {
            // Windows' privacy switch for notification access is off: only Windows can turn it on.
            AddStatus(main, Loc.Get("control.notifications.denied"), Muted);
            AddAction(main, TextButton(Loc.Get("control.notifications.openSettings"), accent: false, OpenNotificationPrivacySettings));
        }
        _notificationSection.Children.Add(main);
        if (!prefs.Allowed || !prefs.Enabled) return;

        if (prefs.Apps.Count == 0)
        {
            _notificationSection.Children.Add(NewRow("\uE946", Loc.Get("control.notifications.empty"), muted: true));
            return;
        }
        foreach (var app in prefs.Apps)
        {
            var row = NewRow("\uE737", app.Name);
            string key = app.Key;
            AddStatus(row, app.Muted ? Loc.Get("control.notifications.muted") : Loc.Get("control.notifications.shown"), Muted,
                Toggle(!app.Muted, on =>
                {
                    _setNotificationAppMuted?.Invoke(key, !on);
                    _notificationKey = "";
                    BuildNotificationSectionIfChanged();
                }));
            _notificationSection.Children.Add(row);
        }
    }

    private static void OpenNotificationPrivacySettings() => Launch("ms-settings:privacy-notifications");

    /// <summary>Opens a program or a settings page the way the Start menu would.</summary>
    private static void Launch(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            RuntimeLog.Warn("CONTROL-CENTER", $"{target} did not open: {ex.Message}");
        }
    }

    #endregion

    #region Audio output

    private void BuildAudioSection(Snapshot s)
    {
        _accent = Orange; // the microphone switch: orange like the system's mic dot
        CloseMenu();
        _audioSection.Children.Clear();
        _audioSection.Children.Add(BuildSectionHeader("audio", Loc.Get("control.audio"), Loc.Get("control.status"), null, topMargin: 14));
        if (!IsExpanded("audio")) return;

        AudioDeviceInfo? current = null;
        foreach (var device in s.Outputs)
        {
            if (device.IsDefault)
            {
                current = device;
                break;
            }
        }
        var row = NewRow("\uE7F5", Loc.Get("control.audioOutput"));
        if (s.Outputs.Count > 1 && SwapTarget(s.Outputs, current) is { } other)
        {
            // One click between the two outputs in use, e.g. headset and speakers.
            var swap = IconButton("\uE8AB", other.FriendlyName, () => SelectOutput(other, current));
            AddStatus(row, other.FriendlyName, Muted, swap);
        }
        else
        {
            AddStatus(row, s.Outputs.Count == 0 ? Loc.Get("control.noAdapter") : "", Muted);
        }
        var outputs = s.Outputs;
        AddAction(row, Combo("\uE7F5", current?.FriendlyName ?? Loc.Get("control.noAdapter"), interactive: outputs.Count > 0,
            anchor => ShowOutputMenu(anchor, outputs, current)));
        _audioSection.Children.Add(row);

        // The default microphone, muted for every app at once, e.g. before a voice call goes live.
        var mic = NewRow(s.MicMuted == true ? "\uEC54" : "\uE720", Loc.Get("control.microphone"));
        if (s.MicMuted is bool muted)
        {
            AddStatus(mic, muted ? Loc.Get("control.microphone.muted") : Loc.Get("control.on"), muted ? WorkingOrange : Muted,
                Toggle(!muted, on => SetMicrophoneMuted(!on)));
            if (s.MicName is { Length: > 0 } micName) AddAction(mic, InfoChip("\uE720", micName));
        }
        else
        {
            AddStatus(mic, Loc.Get("control.noAdapter"), Muted);
        }
        _audioSection.Children.Add(mic);
    }

    private async void SetMicrophoneMuted(bool mute)
    {
        await Task.Run(() => MicrophoneControl.SetMuted(mute));
        RefreshSoon(300);
    }

    /// <summary>The output to swap to: the one used before this one, else the next in the list.</summary>
    private AudioDeviceInfo? SwapTarget(List<AudioDeviceInfo> outputs, AudioDeviceInfo? current)
    {
        AudioDeviceInfo? next = null;
        for (int i = 0; i < outputs.Count; i++)
        {
            var device = outputs[i];
            if (device == current) continue;
            if (device.Id == _previousOutputId) return device;
            next ??= device;
        }
        return next;
    }

    private void ShowOutputMenu(Border anchor, List<AudioDeviceInfo> outputs, AudioDeviceInfo? current)
    {
        var items = new List<MenuItemSpec>(outputs.Count);
        foreach (var device in outputs)
        {
            var target = device;
            items.Add(new MenuItemSpec(device.FriendlyName, device.IsDefault, "\uE7F5", () => SelectOutput(target, current)));
        }
        ShowMenu(anchor, items, MediaAccent);
    }

    private void SelectOutput(AudioDeviceInfo target, AudioDeviceInfo? current)
    {
        if (target.IsDefault) return;
        _previousOutputId = current?.Id;
        OutputDeviceSelected?.Invoke(target.Id);
        RefreshSoon(500);
    }

    #endregion

    #region Bluetooth

    private void BuildBluetoothSection(Snapshot s)
    {
        _accent = Blue;
        _bluetoothSection.Children.Clear();
        _bluetoothSection.Children.Add(BuildSectionHeader("bluetooth", "Bluetooth", Loc.Get("control.status"), null, topMargin: 14));
        if (!IsExpanded("bluetooth")) return;

        var radioRow = NewRow("\uE702", "Bluetooth");
        if (s.BluetoothOn is bool on && _bluetoothRadio != null)
        {
            var radio = _bluetoothRadio;
            AddStatus(radioRow, on ? Loc.Get("control.on") : Loc.Get("control.off"), Muted, Toggle(on, value => SetRadio(radio, value)));
        }
        else
        {
            AddStatus(radioRow, Loc.Get("control.noAdapter"), Muted);
        }
        _bluetoothSection.Children.Add(radioRow);
        if (s.BluetoothOn == false) return;

        if (s.Devices.Count == 0)
        {
            var empty = NewRow("\uE702", Loc.Get("control.noDevices"), muted: true);
            _bluetoothSection.Children.Add(empty);
            return;
        }

        // Connected ones first, then the ones the notch can connect.
        var devices = new List<BluetoothDevice>(s.Devices);
        devices.Sort((a, b) =>
        {
            if (a.IsConnected != b.IsConnected) return a.IsConnected ? -1 : 1;
            if (a.CanConnect != b.CanConnect) return a.CanConnect ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
        });

        foreach (var d in devices)
        {
            var device = d;
            var row = NewRow(d.Glyph, d.Name);
            string status = d.IsConnected ? Loc.Get("control.connected") : Loc.Get("control.notConnected");
            if (d.Battery >= 0) status += $" · {d.Battery} %";
            AddStatus(row, status, d.IsConnected ? Blue : Muted);
            if (d.CanConnect)
            {
                AddAction(row, TextButton(d.IsConnected ? Loc.Get("control.disconnect") : Loc.Get("control.connect"),
                    accent: !d.IsConnected, () => SetBluetoothConnected(device, !device.IsConnected)));
            }
            _bluetoothSection.Children.Add(row);
        }
    }

    private void SetBluetoothConnected(BluetoothDevice device, bool connect)
    {
        Task.Run(() => BluetoothControl.SetConnected(device, connect));
        // The device answers a moment later, if it's on and in reach.
        RefreshSoon(1500);
        RefreshSoon(5000);
    }

    #endregion

    #region Wallpaper Engine

    private void BuildWallpaperSection(Snapshot s)
    {
        _accent = Teal;
        _wallpaperSection.Children.Clear();
        _wallpaperSection.Children.Add(BuildSectionHeader("wallpaper", "Wallpaper Engine", Loc.Get("control.actions"), null, topMargin: 14));
        if (!IsExpanded("wallpaper")) return;

        var row = NewRow("\uE771", "Wallpaper Engine");
        string status = s.Wallpaper switch
        {
            WallpaperEngineState.Running => Loc.Get("control.wallpaper.running"),
            WallpaperEngineState.Paused => Loc.Get("control.wallpaper.paused"),
            WallpaperEngineState.Stopped => Loc.Get("control.wallpaper.stopped"),
            WallpaperEngineState.NotRunning => Loc.Get("control.wallpaper.notRunning"),
            _ => Loc.Get("control.wallpaper.notInstalled")
        };
        AddStatus(row, status, s.Wallpaper == WallpaperEngineState.Running ? Teal : Muted);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 6, 0) };
        switch (s.Wallpaper)
        {
            case WallpaperEngineState.Running:
            case WallpaperEngineState.Paused:
            case WallpaperEngineState.Stopped:
                bool playing = s.Wallpaper == WallpaperEngineState.Running;
                buttons.Children.Add(IconButton(playing ? "\uE769" : "\uE768",
                    playing ? Loc.Get("control.wallpaper.pause") : Loc.Get("control.wallpaper.play"),
                    () => WallpaperAction(playing ? _wallpaper.Pause : _wallpaper.Play)));
                buttons.Children.Add(IconButton(s.WallpaperMuted ? "\uE74F" : "\uE767",
                    s.WallpaperMuted ? Loc.Get("control.wallpaper.unmute") : Loc.Get("control.wallpaper.mute"),
                    () => WallpaperAction(_wallpaper.ToggleMute)));
                buttons.Children.Add(IconButton("\uE893", Loc.Get("control.wallpaper.next"), () => WallpaperAction(_wallpaper.NextWallpaper)));
                buttons.Children.Add(IconButton("\uE71A", Loc.Get("control.wallpaper.stop"), () => WallpaperAction(_wallpaper.Stop)));
                buttons.Children.Add(IconButton("\uE711", Loc.Get("control.wallpaper.quit"), QuitWallpaperEngine));
                break;
            case WallpaperEngineState.NotRunning:
                buttons.Children.Add(TextButton(Loc.Get("control.wallpaper.start"), accent: true, () => WallpaperAction(_wallpaper.Start)));
                break;
        }
        AddAction(row, buttons);
        _wallpaperSection.Children.Add(row);
    }

    private void WallpaperAction(Func<bool> action)
    {
        Task.Run(action);
        RefreshSoon(400);
    }

    private async void QuitWallpaperEngine()
    {
        await _wallpaper.QuitAsync();
        Refresh();
    }

    #endregion

    #region Claude Code

    private void BuildClaudeSection()
    {
        _accent = WorkingOrange;
        if (_disposed || _claude == null) return;
        // A rebuild would take the text box from under the typing; catch up once the message is sent.
        if (_composerBox?.IsKeyboardFocusWithin == true)
        {
            _rebuildAfterComposing = true;
            return;
        }
        var sessions = _claude.GetSessions();
        var pending = _claude.GetPending();
        var limits = _usage?.Latest;
        bool listening = _hookServer?.IsListening == true;

        string key = ClaudeKey(sessions, pending, limits, listening);
        if (key == _claudeKey) return;
        _claudeKey = key;

        _elapsedLabels.Clear();
        _firstRequestCard = null;
        ForgetAnsweredQuestions(pending);
        StopRipples();
        _ripples.Clear();
        _claudeSection.Children.Clear();
        _claudeSection.Children.Add(ClaudeHeader());
        if (!_claudeInstalled) _claudeSection.Children.Add(SetupCard(listening));
        if (LimitCards(limits) is { } limitCards) _claudeSection.Children.Add(limitCards);

        // Hooks in a project's own settings report here too, without our entry in the user settings.
        if (_claudeInstalled || sessions.Count > 0)
        {
            if (!listening) _claudeSection.Children.Add(HintCard("\uE7BA", Loc.Get("control.claude.portBusy", ClaudeHookServer.DefaultPort)));
            _claudeSection.Children.Add(Caption(Loc.Get("control.claude.sessions"), sessions.Count));
            if (sessions.Count == 0) _claudeSection.Children.Add(HintCard("\uE99A", Loc.Get("control.claude.noSessions")));
            foreach (var session in sessions) _claudeSection.Children.Add(SessionCard(session, pending));
        }
        RevealNewCards(_cardScratch);
        _cardScratch.Clear();
        if (IsVisible) StartRipples();
        ContentHeightChanged?.Invoke();
    }

    /// <summary>
    /// The height the page needs at <paramref name="width"/>, so the notch can fit it instead of leaving
    /// a gap under the last card.
    /// </summary>
    public double MeasureContentHeight(double width)
    {
        BuildClaudeSection();
        var padding = ControlScrollViewer.Padding;
        ControlRoot.Measure(new Size(Math.Max(0, width - padding.Left - padding.Right), double.PositiveInfinity));
        return Math.Ceiling(ControlRoot.DesiredSize.Height);
    }

    private Func<bool>? _soundsOn;
    private Action<bool>? _setSounds;

    /// <summary>The Claude page's speaker button reads and flips the sound setting the shell owns.</summary>
    internal void AttachSoundSwitch(Func<bool> soundsOn, Action<bool> setSounds)
    {
        _soundsOn = soundsOn;
        _setSounds = setSounds;
        _claudeKey = "";
    }

    private string ClaudeKey(List<ClaudeSession> sessions, List<ClaudePermissionRequest> pending, ClaudeLimits? limits, bool listening)
    {
        var sb = new StringBuilder();
        sb.Append(_claudeInstalled).Append(listening).Append(_soundsOn?.Invoke()).Append('|');
        if (limits != null)
        {
            sb.Append(limits.FiveHour?.UsedPercent).Append(limits.FiveHour?.ResetsAt?.ToUnixTimeSeconds() / 60)
              .Append(limits.Week?.UsedPercent).Append(limits.Week?.ResetsAt?.ToUnixTimeSeconds() / 60).Append('|');
        }
        foreach (var s in sessions)
            sb.Append(s.Id).Append(s.Status).Append(s.Title).Append(s.Activity).Append(s.Prompt).Append(s.Summary).Append(s.Cwd).Append(s.TurnStartedUtc?.Ticks).Append(';');
        foreach (var p in pending) sb.Append(p.Id).Append(';');
        return sb.ToString();
    }

    private void InstallClaudeHooks()
    {
        _claudeInstalled = ClaudeCodeSetup.Install(ClaudeHookServer.HookUrl);
        _claudeKey = "";
        BuildClaudeSection();
    }

    private void RemoveClaudeHooks()
    {
        if (ClaudeCodeSetup.Uninstall(ClaudeHookServer.HookUrl)) _claudeInstalled = false;
        _claudeKey = "";
        BuildClaudeSection();
    }

    private FrameworkElement ClaudeHeader()
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Whether sessions show in the closed notch at all (see ControlCenterView.ClaudeNotch.cs).
        if (ClaudeNotchButton() is { } eye)
        {
            Grid.SetColumn(eye, 1);
            grid.Children.Add(eye);
        }

        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new System.Windows.Shapes.Path
        {
            Data = ClaudeGlyph.Data,
            Fill = WorkingOrange,
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 9, 0)
        });
        title.Children.Add(MakeLabel("Claude Code", 15, VNotch.Services.UiPalette.PrimaryBrush));
        grid.Children.Add(title);

        if (_soundsOn != null)
        {
            // Sounds when a turn ends or a session waits: the speaker shows and flips the setting.
            bool on = _soundsOn();
            var sound = IconButton(on ? "\uE767" : "\uE74F", Loc.Get(on ? "control.claude.soundsOn" : "control.claude.soundsOff"), () =>
            {
                _setSounds?.Invoke(!_soundsOn());
                _claudeKey = "";
                BuildClaudeSection();
            });
            sound.Width = sound.Height = 24;
            sound.Margin = new Thickness(0, 0, 12, 0);
            sound.Opacity = on ? 1 : 0.6;
            Grid.SetColumn(sound, 2);
            grid.Children.Add(sound);
        }
        if (_claudeInstalled)
        {
            var remove = ColumnLabel(Loc.Get("control.claude.remove"));
            remove.FontSize = 11.5;
            MakeLink(remove, RemoveClaudeHooks);
            Grid.SetColumn(remove, 3);
            grid.Children.Add(remove);
        }
        return grid;
    }

    private FrameworkElement SetupCard(bool listening)
    {
        string hint = !ClaudeCodeSetup.IsClaudeCodePresent ? Loc.Get("control.claude.notFound")
            : !listening ? Loc.Get("control.claude.portBusy", ClaudeHookServer.DefaultPort)
            : Loc.Get("control.claude.setupHint");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = MakeLabel(hint, 12, Muted);
        text.TextWrapping = TextWrapping.Wrap;
        text.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(text);
        if (listening && ClaudeCodeSetup.IsClaudeCodePresent)
        {
            var setup = TextButton(Loc.Get("control.claude.setup"), accent: true, InstallClaudeHooks);
            setup.Margin = new Thickness(0);
            Grid.SetColumn(setup, 1);
            grid.Children.Add(setup);
        }
        var card = Card();
        card.Margin = new Thickness(0, 0, 0, 10);
        card.Child = grid;
        return card;
    }

    private Border HintCard(string glyph, string text)
    {
        var card = Card();
        card.Margin = new Thickness(0, 0, 0, 6);
        card.Child = NameCell(glyph, text, muted: true);
        return card;
    }

    private FrameworkElement Caption(string text, int count)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 8) };
        panel.Children.Add(ColumnLabel(text));
        if (count > 0)
        {
            var number = MakeLabel(count.ToString(System.Globalization.CultureInfo.CurrentCulture), 12, Muted);
            number.Margin = new Thickness(6, 0, 0, 0);
            panel.Children.Add(number);
        }
        return panel;
    }

    private static Border Card() => new()
    {
        Background = CardBg,
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(14, 11, 14, 11)
    };

    /// <summary>
    /// One session: its name, what it is doing and any request waiting on it. A click brings its terminal
    /// tab or app to the front.
    /// </summary>
    private FrameworkElement SessionCard(ClaudeSession session, List<ClaudePermissionRequest> pending)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Brush statusBrush = StatusBrush(session.Status);
        grid.Children.Add(StatusDot(statusBrush, session.Status is ClaudeSessionStatus.Working or ClaudeSessionStatus.Waiting));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var name = MakeLabel(session.Name, 13.5, VNotch.Services.UiPalette.PrimaryBrush);
        if (session.Cwd.Length > 0) name.ToolTip = session.Cwd;
        text.Children.Add(name);
        text.Children.Add(StatusLine(session, statusBrush));
        string? preview = session.Status == ClaudeSessionStatus.Done && session.Summary is { Length: > 0 } ? session.Summary : session.Prompt;
        if (preview is { Length: > 0 })
        {
            var line = MakeLabel(preview, 11, HeaderText);
            line.FontWeight = FontWeights.Medium;
            line.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(line);
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var side = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) };
        if (TimeLabel(session) is { } time) side.Children.Add(time);
        // Shown on hover, like the open arrow; a waiting session wants its request answered first.
        var hoverButtons = new List<Border>(3);
        if (session.Status != ClaudeSessionStatus.Waiting && _composingSessionId != session.Id)
        {
            string replyTo = session.Id;
            hoverButtons.Add(IconButton("\uE97A", Loc.Get("control.claude.reply"), () => StartComposing(replyTo)));
            hoverButtons.Add(IconButton("\uE77F", Loc.Get("control.claude.pasteClipboard"), () => AttachClipboard(replyTo)));
            hoverButtons.Add(IconButton("\uE722", Loc.Get("control.claude.screenshot"), () => ScreenshotRequested?.Invoke(replyTo)));
            for (int i = 0; i < hoverButtons.Count; i++)
            {
                hoverButtons[i].Width = hoverButtons[i].Height = 24;
                hoverButtons[i].Margin = new Thickness(i == 0 ? 10 : 5, -3, 0, -3);
                hoverButtons[i].Opacity = 0;
                side.Children.Add(hoverButtons[i]);
            }
        }
        var open = new TextBlock
        {
            Text = "\uE8A7",
            FontFamily = SegoeSymbolFont,
            FontSize = 12,
            Foreground = VNotch.Services.UiPalette.PrimaryBrush,
            Opacity = 0,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        side.Children.Add(open);
        Grid.SetColumn(side, 2);
        grid.Children.Add(side);

        StackPanel? requests = null;
        foreach (var request in pending)
        {
            if (request.SessionId != session.Id) continue;
            var requestCard = request.Questions != null ? QuestionCard(request, request.Questions)
                : request.Plan != null ? PlanCard(request, request.Plan)
                : PermissionCard(request);
            _firstRequestCard ??= requestCard;
            _cardScratch.Add(("request:" + request.Id, requestCard));
            requests ??= new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            requests.Children.Add(requestCard);
        }
        if (_composingSessionId == session.Id)
        {
            requests ??= new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            requests.Children.Add(Composer(session.Id));
        }
        if (requests != null)
        {
            Grid.SetRow(requests, 1);
            Grid.SetColumn(requests, 1);
            Grid.SetColumnSpan(requests, 2);
            grid.Children.Add(requests);
        }

        var card = Card();
        card.Margin = new Thickness(0, 0, 0, 6);
        card.Cursor = Cursors.Hand;
        card.Child = grid;
        _cardScratch.Add(("session:" + session.Id, card));
        System.Windows.Automation.AutomationProperties.SetName(card, session.Name);
        card.MouseEnter += (_, _) =>
        {
            card.Background = CardHover;
            open.Opacity = 1;
            foreach (var button in hoverButtons) button.Opacity = 1;
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = CardBg;
            open.Opacity = 0;
            foreach (var button in hoverButtons) button.Opacity = 0;
        };
        string sessionId = session.Id;
        string? title = session.Title;
        card.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            OpenSession(card, sessionId, title);
        };
        return card;
    }

    private void OpenSession(Border card, string sessionId, string? title)
    {
        if (ClaudeSessionWindow.TryActivate(sessionId, title))
        {
            SessionOpened?.Invoke();
            return;
        }
        // Its window is gone (or it never told us its process): a short shake says so.
        Shake(card);
    }

    /// <summary>Raised with a session id when its screenshot button is pressed; the shell runs the snip.</summary>
    public event Action<string>? ScreenshotRequested;

    // Goes along with the message being typed: clipboard text, a picture or files.
    private ClaudeAttachment? _composeAttachment;

    private void AttachClipboard(string sessionId)
    {
        if (ClaudeAttachments.FromClipboard() is not { } attachment) return;
        StartComposing(sessionId, attachment);
    }

    /// <summary>The snip the shell took for a session: opens its message box with the picture attached.</summary>
    internal void AttachScreenshot(string sessionId, ClaudeAttachment screenshot) => StartComposing(sessionId, screenshot);

    private void StartComposing(string sessionId, ClaudeAttachment? attachment = null)
    {
        if (_composingSessionId != sessionId)
        {
            _composeDraft = "";
            _composeAttachment = null;
        }
        if (attachment != null) _composeAttachment = attachment;
        _composingSessionId = sessionId;
        KeyboardInputRequested?.Invoke(true);
        _claudeKey = "";
        BuildClaudeSection();
    }

    /// <summary>Esc while typing a message: drop it and close the box, but keep the notch open.</summary>
    internal bool TryDismissComposer()
    {
        if (_composingSessionId == null) return false;
        _composeDraft = "";
        _composeAttachment = null;
        StopComposing();
        return true;
    }

    private void StopComposing()
    {
        _composingSessionId = null;
        _composerBox = null;
        KeyboardInputRequested?.Invoke(false);
        _rebuildAfterComposing = false;
        _claudeKey = "";
        BuildClaudeSection();
    }

    /// <summary>A one-line message box under the session: Enter sends it into the session, Esc closes it.</summary>
    private FrameworkElement Composer(string sessionId)
    {
        var box = new TextBox
        {
            Text = _composeDraft,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = VNotch.Services.UiPalette.PrimaryBrush,
            CaretBrush = VNotch.Services.UiPalette.PrimaryBrush,
            SelectionBrush = WorkingOrange,
            FontFamily = ControlFont,
            FontSize = 12.5,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            AcceptsReturn = false
        };
        var placeholder = MakeLabel(Loc.Get("control.claude.messagePlaceholder"), 12.5, Muted);
        placeholder.FontWeight = FontWeights.Normal;
        placeholder.IsHitTestVisible = false;
        placeholder.Margin = new Thickness(2, 0, 0, 0);
        placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        box.TextChanged += (_, _) =>
        {
            _composeDraft = box.Text;
            placeholder.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SendMessage(sessionId, box.Text);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                TryDismissComposer();
            }
        };
        box.LostKeyboardFocus += (_, _) =>
        {
            if (_rebuildAfterComposing && _composingSessionId != null) Dispatcher.BeginInvoke(DispatcherPriority.Background, BuildClaudeSection);
        };
        box.Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.CaretIndex = box.Text.Length;
        });
        _composerBox = box;

        var send = IconButton("\uE724", Loc.Get("control.claude.reply"), () => SendMessage(sessionId, box.Text));
        send.Width = send.Height = 26;
        send.Background = WorkingOrange;
        send.BorderBrush = WorkingOrange;
        ((TextBlock)send.Child).Foreground = OnGreenText;
        send.MouseEnter += (_, _) => send.Opacity = 0.85;
        send.MouseLeave += (_, _) => send.Opacity = 1;

        var field = new Grid();
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        field.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        field.Children.Add(placeholder);
        field.Children.Add(box);
        Grid.SetColumn(send, 1);
        field.Children.Add(send);

        FrameworkElement content = field;
        if (_composeAttachment is { } attachment)
        {
            var withChip = new StackPanel();
            withChip.Children.Add(AttachmentChip(attachment));
            withChip.Children.Add(field);
            content = withChip;
        }
        var shell = new Border
        {
            Background = ComboBg,
            BorderBrush = ComboBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 3, 3, 3),
            Margin = new Thickness(0, 0, 0, 2),
            Cursor = Cursors.IBeam,
            Child = content
        };
        // Typing here must neither open the session's window nor close the notch.
        shell.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            box.Focus();
        };
        return shell;
    }

    private async void SendMessage(string sessionId, string text)
    {
        // The prompt is one line here; a line break would submit early.
        string message = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
        var attachment = _composeAttachment;
        if (attachment != null) message = message.Length > 0 ? $"{message} {attachment.Mention}" : attachment.Mention;
        if (message.Length == 0) return;
        _composeDraft = "";
        _composeAttachment = null;
        StopComposing();
        if (await ClaudeSessionInput.SendAsync(sessionId, message)) return;
        RuntimeLog.Warn("CLAUDE", "Message not delivered: the session has no console to type into.");
        // Keep what was typed, so it can go to the terminal by hand.
        _composeDraft = text;
        StartComposing(sessionId, attachment);
        if (_composerBox?.Parent is FrameworkElement field && field.Parent is FrameworkElement shell) Shake(shell);
    }

    /// <summary>"12 lines from the clipboard", "Picture"…, with a cross that takes it off again.</summary>
    private FrameworkElement AttachmentChip(ClaudeAttachment attachment)
    {
        (string glyph, string label) = attachment.Kind switch
        {
            ClaudeAttachmentKind.Image => ("\uE722", Loc.Get("control.claude.attachedImage")),
            ClaudeAttachmentKind.Files => ("\uE8A5", Loc.Get("control.claude.attachedFiles", attachment.Count)),
            ClaudeAttachmentKind.Lines => ("\uE77F", Loc.Get("control.claude.attachedLines", attachment.Count)),
            _ => ("\uE77F", attachment.Mention)
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 3) };
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = SegoeSymbolFont,
            FontSize = 12,
            Foreground = WorkingOrange,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0)
        });
        var text = MakeLabel(label, 11.5, VNotch.Services.UiPalette.PrimaryBrush, maxWidth: 420);
        text.ToolTip = attachment.Mention;
        row.Children.Add(text);
        var remove = new TextBlock
        {
            Text = "\uE711",
            FontFamily = SegoeSymbolFont,
            FontSize = 10,
            Foreground = Muted,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 1, 0, 0)
        };
        remove.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _composeAttachment = null;
            _composerBox = null;
            _claudeKey = "";
            BuildClaudeSection();
        };
        row.Children.Add(remove);
        return row;
    }

    private static Brush StatusBrush(ClaudeSessionStatus status) => status switch
    {
        ClaudeSessionStatus.Waiting => WaitingYellow,
        ClaudeSessionStatus.Working => WorkingOrange,
        ClaudeSessionStatus.Done => VNotch.Services.UiPalette.PrimaryBrush,
        _ => Muted
    };

    /// <summary>
    /// A dot in the state's colour; while the session is busy or waiting a ring ripples out of it, as long as
    /// the tab is on screen.
    /// </summary>
    private FrameworkElement StatusDot(Brush brush, bool active)
    {
        var dot = new Grid
        {
            Width = 14,
            Height = 14,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0)
        };
        if (active)
        {
            var ring = new System.Windows.Shapes.Ellipse
            {
                Fill = brush,
                Opacity = 0.22,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
            _ripples.Add(ring);
            dot.Children.Add(ring);
        }
        dot.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Fill = brush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
        return dot;
    }

    /// <summary>"V-Notch · Thinking…": the folder (once the session has its own name) and what it does.</summary>
    private TextBlock StatusLine(ClaudeSession session, Brush statusBrush)
    {
        string status = session.Status switch
        {
            ClaudeSessionStatus.Waiting => Loc.Get("control.claude.waiting"),
            ClaudeSessionStatus.Working => session.Activity is { Length: > 0 } activity ? activity : Loc.Get("control.claude.thinking"),
            ClaudeSessionStatus.Done => Loc.Get("control.claude.done"),
            _ => Loc.Get("control.claude.idle")
        };
        var line = new TextBlock
        {
            FontFamily = ControlFont,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Muted,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 2, 0, 0)
        };
        if (session.Title is { Length: > 0 }) line.Inlines.Add(new System.Windows.Documents.Run(session.Project + "  ·  "));
        line.Inlines.Add(new System.Windows.Documents.Run(status)
        {
            Foreground = session.Status is ClaudeSessionStatus.Waiting or ClaudeSessionStatus.Done ? statusBrush : Muted
        });
        return line;
    }

    /// <summary>How long the turn runs (ticking) or took; null for an idle session.</summary>
    private TextBlock? TimeLabel(ClaudeSession session)
    {
        if (session.TurnStartedUtc is { } started && session.Status is ClaudeSessionStatus.Working or ClaudeSessionStatus.Waiting)
        {
            var elapsed = MakeLabel(FormatElapsed(DateTime.UtcNow - started), 12, Muted);
            _elapsedLabels.Add((elapsed, started));
            return elapsed;
        }
        if (session.Status == ClaudeSessionStatus.Done && session.FinishedUtc is { } finished && session.TurnStartedUtc is { } turnStart)
            return MakeLabel(FormatElapsed(finished - turnStart), 12, Muted);
        return null;
    }

    private void StartRipples()
    {
        if (AnimationConfig.ReduceMotion) return;
        var grow = new DoubleAnimation(0.6, 1.7, new Duration(TimeSpan.FromMilliseconds(1600)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fade = new DoubleAnimation(0.45, 0, new Duration(TimeSpan.FromMilliseconds(1600)))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Timeline.SetDesiredFrameRate(grow, RippleFps);
        Timeline.SetDesiredFrameRate(fade, RippleFps);
        foreach (var ring in _ripples)
        {
            var scale = (ScaleTransform)ring.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
            ring.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void StopRipples()
    {
        foreach (var ring in _ripples)
        {
            var scale = (ScaleTransform)ring.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            ring.BeginAnimation(OpacityProperty, null);
        }
    }

    private static void Shake(FrameworkElement element)
    {
        if (AnimationConfig.ReduceMotion) return;
        var shift = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = shift;
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(320) };
        double[] offsets = { -6, 5, -3, 2, 0 };
        for (int i = 0; i < offsets.Length; i++)
            shake.KeyFrames.Add(new EasingDoubleKeyFrame(offsets[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(64 * (i + 1)))));
        Timeline.SetDesiredFrameRate(shake, AnimationConfig.TargetFps);
        shift.BeginAnimation(TranslateTransform.XProperty, shake);
    }

    private static Border RequestCard()
    {
        var card = new Border
        {
            Background = ComboBg,
            BorderBrush = ComboBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 10, 9),
            Margin = new Thickness(0, 0, 0, 6),
            // Inside a session card, whose hand says "opens the terminal".
            Cursor = Cursors.Arrow
        };
        // A tap next to the buttons while reading must neither open the session nor close the notch.
        card.MouseLeftButtonDown += (_, e) => e.Handled = true;
        return card;
    }

    private FrameworkElement PermissionCard(ClaudePermissionRequest request)
    {
        var card = RequestCard();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        string? detail = request.Detail;
        // A file reads by its name; the folder goes under it, cut at the end if long.
        if (detail is { Length: > 0 } && IsFileTool(request.Tool) && System.IO.Path.GetFileName(detail) is { Length: > 0 } fileName)
        {
            var title = new TextBlock { FontFamily = ControlFont, FontSize = 12.5, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
            title.Inlines.Add(new System.Windows.Documents.Run(request.Tool + "  ") { Foreground = Muted });
            title.Inlines.Add(new System.Windows.Documents.Run(fileName) { Foreground = VNotch.Services.UiPalette.PrimaryBrush });
            text.Children.Add(title);
            detail = System.IO.Path.GetDirectoryName(detail);
        }
        else
        {
            text.Children.Add(MakeLabel(request.Tool, 12.5, VNotch.Services.UiPalette.PrimaryBrush));
        }
        if (detail is { Length: > 0 } && IsFileTool(request.Tool))
        {
            var folder = MakeLabel(detail, 11, Muted);
            folder.FontFamily = MonoFont;
            folder.FontWeight = FontWeights.Normal;
            folder.ToolTip = request.Detail;
            folder.Margin = new Thickness(0, 3, 0, 0);
            text.Children.Add(folder);
        }
        else if (detail is { Length: > 0 })
        {
            text.Children.Add(new TextBlock
            {
                Text = detail,
                FontFamily = MonoFont,
                FontSize = 11.5,
                Foreground = Muted,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 48,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 0, 0)
            });
        }
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        string id = request.Id;
        var deny = TextButton(Loc.Get("control.claude.deny"), accent: false, () => _claude?.Decide(id, ClaudeDecision.Deny));
        var always = TextButton(Loc.Get("control.claude.always"), accent: false, () => _claude?.Decide(id, ClaudeDecision.Always));
        if (request.AlwaysLabel is { Length: > 0 } rule) always.ToolTip = rule;
        var allow = TextButton(Loc.Get("control.claude.allow"), accent: true, () => _claude?.Decide(id, ClaudeDecision.Allow));
        deny.Margin = always.Margin = new Thickness(0, 0, 6, 0);
        buttons.Children.Add(deny);
        buttons.Children.Add(always);
        buttons.Children.Add(allow);
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);

        if (ChangePreview(request) is { } preview)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(preview, 1);
            Grid.SetColumnSpan(preview, 2);
            grid.Children.Add(preview);
        }

        card.Child = grid;
        return card;
    }

    private static bool IsFileTool(string tool) => tool is "Edit" or "MultiEdit" or "Write" or "Read" or "NotebookEdit";

    private const int ChangePreviewLines = 8;
    private static readonly Brush AddedLineBg = Frozen("#243FD15B");
    private static readonly Brush RemovedLineBg = Frozen("#26FF453A");
    private static readonly Brush AddedLineText = Frozen("#A6EBB3");
    private static readonly Brush RemovedLineText = Frozen("#FFA59E");
    private static readonly Brush PreviewBg = Frozen("#40000000");

    /// <summary>The lines an Edit or Write would change, red and green, so it can be judged before allowing.</summary>
    private FrameworkElement? ChangePreview(ClaudePermissionRequest request)
    {
        var lines = ClaudeChangePreview.Lines(request.Tool, request.Input);
        if (lines.Count == 0) return null;
        var stack = new StackPanel();
        int shown = Math.Min(lines.Count, ChangePreviewLines);
        for (int i = 0; i < shown; i++)
        {
            var line = lines[i];
            bool added = line.Sign == '+';
            stack.Children.Add(new Border
            {
                Background = added ? AddedLineBg : RemovedLineBg,
                Padding = new Thickness(8, 1, 8, 1),
                Child = new TextBlock
                {
                    Text = $"{(added ? '+' : '−')} {line.Text}",
                    FontFamily = MonoFont,
                    FontSize = 11,
                    Foreground = added ? AddedLineText : RemovedLineText,
                    TextTrimming = TextTrimming.CharacterEllipsis
                }
            });
        }
        if (lines.Count > shown)
        {
            var more = MakeLabel(Loc.Get("control.claude.moreLines", lines.Count - shown), 10.5, Muted);
            more.Margin = new Thickness(8, 4, 8, 2);
            stack.Children.Add(more);
        }
        return new Border
        {
            Background = PreviewBg,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0, 4, 0, 4),
            Margin = new Thickness(0, 9, 0, 1),
            ClipToBounds = true,
            Child = stack
        };
    }

    private const int PlanMaxLines = 120;
    private const double PlanMaxHeight = 200;

    /// <summary>
    /// Claude's plan, readable here: headings, bullets and code from its Markdown, scrolling inside the card
    /// when long. It goes ahead from here, with edits accepted if Claude Code offers that, or back to planning.
    /// </summary>
    private FrameworkElement PlanCard(ClaudePermissionRequest request, string plan)
    {
        var card = RequestCard();
        var stack = new StackPanel();
        var header = MakeLabel(Loc.Get("control.claude.plan"), 11, HeaderText);
        stack.Children.Add(header);

        var body = new StackPanel { Margin = new Thickness(0, 2, 8, 0) };
        bool code = false;
        int lines = 0;
        foreach (string raw in plan.Replace("\r\n", "\n").Split('\n'))
        {
            if (++lines > PlanMaxLines) break;
            string line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                code = !code;
                continue;
            }
            if (code)
            {
                body.Children.Add(new TextBlock { Text = line, FontFamily = MonoFont, FontSize = 11, Foreground = Muted, TextWrapping = TextWrapping.Wrap });
                continue;
            }
            string text = line.Trim();
            if (text.Length == 0)
            {
                body.Children.Add(new Border { Height = 5 });
                continue;
            }
            if (text.StartsWith('#'))
            {
                var heading = PlanText(text.TrimStart('#').Trim(), 13, VNotch.Services.UiPalette.PrimaryBrush, FontWeights.Bold);
                heading.Margin = new Thickness(0, 4, 0, 2);
                body.Children.Add(heading);
                continue;
            }
            bool bullet = text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal);
            int indent = raw.Length - raw.TrimStart().Length;
            var paragraph = PlanText(bullet ? "•  " + text[2..] : text, 12, VNotch.Services.UiPalette.PrimaryBrush, FontWeights.Normal);
            paragraph.Margin = new Thickness(Math.Min(indent, 8) * 4, 1, 0, 1);
            body.Children.Add(paragraph);
        }
        stack.Children.Add(new ScrollViewer
        {
            Content = body,
            MaxHeight = PlanMaxHeight,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 4, 0, 10)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        string id = request.Id;
        var keepPlanning = TextButton(Loc.Get("control.claude.keepPlanning"), accent: false, () => _claude?.Decide(id, ClaudeDecision.Deny));
        buttons.Children.Add(keepPlanning);
        // Claude Code offers "auto-accept edits" as the mode to switch to; the Always answer applies it.
        if (request.AlwaysLabel?.Contains("acceptEdits", StringComparison.OrdinalIgnoreCase) == true)
            buttons.Children.Add(TextButton(Loc.Get("control.claude.autoEdits"), accent: false, () => _claude?.Decide(id, ClaudeDecision.Always)));
        var approve = TextButton(Loc.Get("control.claude.approvePlan"), accent: true, () => _claude?.Decide(id, ClaudeDecision.Allow));
        approve.Margin = new Thickness(0);
        buttons.Children.Add(approve);
        stack.Children.Add(buttons);

        card.Child = stack;
        return card;
    }

    private TextBlock PlanText(string text, double size, Brush foreground, FontWeight weight) => new()
    {
        Text = text.Replace("**", "").Replace("`", ""),
        FontFamily = ControlFont,
        FontSize = size,
        FontWeight = weight,
        Foreground = foreground,
        TextWrapping = TextWrapping.Wrap
    };

    /// <summary>Claude's multiple-choice questions, answered with the option buttons.</summary>
    private FrameworkElement QuestionCard(ClaudePermissionRequest request, IReadOnlyList<ClaudeQuestion> questions)
    {
        if (!_questionPicks.TryGetValue(request.Id, out var picks))
            _questionPicks[request.Id] = picks = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        // A single single-choice question goes out on the first tap; anything else waits for Send.
        bool sendOnPick = questions.Count == 1 && !questions[0].MultiSelect;

        var card = RequestCard();
        var stack = new StackPanel();
        for (int i = 0; i < questions.Count; i++)
        {
            var question = questions[i];
            if (question.Header.Length > 0)
            {
                var header = MakeLabel(question.Header, 11, HeaderText);
                header.FontWeight = FontWeights.Bold;
                header.Margin = new Thickness(0, i == 0 ? 0 : 10, 0, 2);
                stack.Children.Add(header);
            }
            stack.Children.Add(new TextBlock
            {
                Text = question.Text,
                FontFamily = ControlFont,
                FontSize = 12.5,
                Foreground = VNotch.Services.UiPalette.PrimaryBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, question.Header.Length > 0 || i == 0 ? 0 : 10, 0, 8)
            });

            picks.TryGetValue(question.Text, out var chosen);
            var options = new WrapPanel();
            foreach (var option in question.Options)
            {
                string label = option.Label;
                var button = TextButton(label, accent: chosen?.Contains(label) == true,
                    () => Pick(request, question, label, sendOnPick));
                button.HorizontalAlignment = HorizontalAlignment.Left;
                button.Margin = new Thickness(0, 0, 6, 6);
                if (option.Description is { } description) button.ToolTip = description;
                options.Children.Add(button);
            }
            stack.Children.Add(options);
        }

        if (!sendOnPick)
        {
            var send = TextButton(Loc.Get("control.claude.submit"), accent: AllAnswered(questions, picks), () =>
            {
                if (AllAnswered(questions, picks)) SendAnswers(request, questions);
            });
            send.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(send);
        }

        card.Child = stack;
        return card;
    }

    private void Pick(ClaudePermissionRequest request, ClaudeQuestion question, string label, bool sendNow)
    {
        if (!_questionPicks.TryGetValue(request.Id, out var picks)) return;
        if (!picks.TryGetValue(question.Text, out var chosen)) picks[question.Text] = chosen = new List<string>();
        if (!question.MultiSelect) chosen.Clear();
        if (!chosen.Remove(label)) chosen.Add(label);

        if (sendNow)
        {
            SendAnswers(request, request.Questions!);
            return;
        }
        _claudeKey = "";
        BuildClaudeSection();
    }

    private void SendAnswers(ClaudePermissionRequest request, IReadOnlyList<ClaudeQuestion> questions)
    {
        if (!_questionPicks.Remove(request.Id, out var picks)) return;
        var answers = new Dictionary<string, string>(questions.Count, StringComparer.Ordinal);
        foreach (var question in questions)
        {
            if (!picks.TryGetValue(question.Text, out var chosen) || chosen.Count == 0) continue;
            // Several labels go back in the order Claude listed them.
            var labels = new List<string>(chosen.Count);
            foreach (var option in question.Options)
                if (chosen.Contains(option.Label)) labels.Add(option.Label);
            answers[question.Text] = string.Join(", ", labels);
        }
        _claude?.Answer(request.Id, answers);
    }

    private static bool AllAnswered(IReadOnlyList<ClaudeQuestion> questions, Dictionary<string, List<string>> picks)
    {
        foreach (var question in questions)
            if (!picks.TryGetValue(question.Text, out var chosen) || chosen.Count == 0) return false;
        return true;
    }

    private void ForgetAnsweredQuestions(List<ClaudePermissionRequest> pending)
    {
        if (_questionPicks.Count == 0) return;
        var gone = new List<string>();
        foreach (var id in _questionPicks.Keys)
        {
            bool open = false;
            foreach (var request in pending)
                if (request.Id == id) open = true;
            if (!open) gone.Add(id);
        }
        foreach (var id in gone) _questionPicks.Remove(id);
    }

    /// <summary>The 5-hour and weekly limits side by side; null before the first reading.</summary>
    private FrameworkElement? LimitCards(ClaudeLimits? limits)
    {
        if (limits == null || (limits.FiveHour == null && limits.Week == null)) return null;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (limits.FiveHour is { } fiveHour) grid.Children.Add(LimitCard(Loc.Get("control.claude.limit5h"), fiveHour));
        if (limits.Week is { } week)
        {
            var card = LimitCard(Loc.Get("control.claude.limitWeek"), week);
            Grid.SetColumn(card, 2);
            grid.Children.Add(card);
        }
        return grid;
    }

    private Border LimitCard(string label, ClaudeLimitWindow window)
    {
        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(MakeLabel(label, 12, VNotch.Services.UiPalette.PrimaryBrush));
        var percent = MakeLabel($"{Math.Round(window.UsedPercent)} %", 12.5,
            window.UsedPercent >= 90 ? WorkingOrange : VNotch.Services.UiPalette.PrimaryBrush);
        Grid.SetColumn(percent, 1);
        top.Children.Add(percent);

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(UsageBar(window.UsedPercent));
        if (window.ResetsAt is { } resets)
            stack.Children.Add(MakeLabel(Loc.Get("control.claude.resetsIn", FormatDuration(resets - DateTimeOffset.UtcNow)), 11, Muted));

        var card = Card();
        card.Child = stack;
        return card;
    }

    private static Grid UsageBar(double usedPercent)
    {
        var bar = new Grid { Height = 4, Margin = new Thickness(0, 8, 0, 7) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(usedPercent, 0.001), GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(100 - usedPercent, 0.001), GridUnitType.Star) });
        var trackBorder = new Border { Background = Track, CornerRadius = new CornerRadius(2) };
        Grid.SetColumnSpan(trackBorder, 2);
        bar.Children.Add(trackBorder);
        bar.Children.Add(new Border
        {
            Background = usedPercent >= 90 ? Red : WorkingOrange,
            CornerRadius = new CornerRadius(2)
        });
        return bar;
    }

    private void UpdateElapsedLabels()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var (label, started) in _elapsedLabels) label.Text = FormatElapsed(now - started);
    }

    internal static string FormatElapsed(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }

    internal static string FormatDuration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays} d {span.Hours} h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours} h {span.Minutes} min";
        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))} min";
    }

    #endregion

    #region Shared builders (same look as AudioMixerView)

    private bool IsExpanded(string section) => !_expanded.TryGetValue(section, out bool value) || value;

    private static Grid NewRowGrid()
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColName) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 120 });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ColAction) });
        return g;
    }

    private FrameworkElement BuildSectionHeader(string section, string title, string rightLabel, Action? onRightLabel, double topMargin)
    {
        bool expanded = IsExpanded(section);
        var grid = NewRowGrid();
        grid.Margin = new Thickness(0, topMargin, 0, 6);

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var rotate = new RotateTransform(expanded ? 0 : -90);
        titlePanel.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L8,0 L4,5 Z"),
            Fill = HeaderText,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 8, 0),
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = rotate
        });
        titlePanel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = VNotch.Services.UiPalette.PrimaryBrush,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            FontFamily = ControlFont,
            VerticalAlignment = VerticalAlignment.Center
        });
        titlePanel.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _expanded[section] = !expanded;
            var spin = new DoubleAnimation(expanded ? -90 : 0, new Duration(TimeSpan.FromMilliseconds(220)))
            {
                EasingFunction = KnobSpring
            };
            Timeline.SetDesiredFrameRate(spin, AnimationConfig.TargetFps);
            var panel = grid.Parent as Panel;
            if (expanded && panel != null && !AnimationConfig.ReduceMotion)
                for (int i = panel.Children.Count - 1, n = 0; i > 0; i--, n++)
                {
                    var row = panel.Children[i];
                    var lift = new TranslateTransform();
                    row.RenderTransform = lift;
                    row.BeginAnimation(OpacityProperty, WeatherMotion.To(row.Opacity, 0, 150, WeatherMotion.Out, n * 12));
                    lift.BeginAnimation(TranslateTransform.YProperty, WeatherMotion.To(0, -8, 170, WeatherMotion.Out, n * 12));
                }
            spin.Completed += (_, _) =>
            {
                RebuildSection(section);
                if (!expanded && panel != null && IsVisible) RevealChildren(panel, TimeSpan.Zero, 0, skip: 1);
            };
            rotate.BeginAnimation(RotateTransform.AngleProperty, spin);
        };
        Grid.SetColumn(titlePanel, 0);
        grid.Children.Add(titlePanel);

        if (expanded)
        {
            var label = ColumnLabel(rightLabel);
            if (onRightLabel != null) MakeLink(label, onRightLabel);
            Grid.SetColumn(label, 2);
            grid.Children.Add(label);
        }
        return grid;
    }

    private void MakeLink(TextBlock label, Action onClick)
    {
        label.Cursor = Cursors.Hand;
        label.MouseEnter += (_, _) => label.Foreground = VNotch.Services.UiPalette.PrimaryBrush;
        label.MouseLeave += (_, _) => label.Foreground = _usesGlass() ? VNotch.Services.UiPalette.PrimaryBrush : HeaderText;
        label.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
    }

    private void RebuildSection(string section)
    {
        switch (section)
        {
            case "network": _networkKey = ""; break;
            case "bluetooth": _bluetoothKey = ""; break;
            case "wallpaper": _wallpaperKey = ""; break;
            case "laptop": _laptopKey = ""; break;
            case "audio": _audioKey = ""; break;
            case "notifications": _notificationKey = ""; break;
            case "pi.home":
            case "pi.school":
            case "pi.todos": _piKey = ""; BuildPiPage(); return;
            case "legion.mode":
            case "control.system": ToggleLegionSection(section); return;
            case "system": _statsTiles = null; break;
            case "claude": _claudeKey = ""; BuildClaudeSection(); return;
        }
        if (_snapshot != null) ApplySnapshot(_snapshot);
    }

    private TextBlock ColumnLabel(string text) => new()
    {
        Text = text,
        // White on the liquid glass skin, like the mixer's column labels.
        Foreground = _usesGlass() ? VNotch.Services.UiPalette.PrimaryBrush : HeaderText,
        FontSize = 12,
        FontWeight = FontWeights.Bold,
        FontFamily = ControlFont,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

    /// <param name="muted">A hint ("no devices"): grey, and free to run into the next columns.</param>
    /// <param name="dim">A quiet entry (a task already handed in): grey, but trimmed to its column like any row.</param>
    private Grid NewRow(string glyph, string label, bool muted = false, bool dim = false)
    {
        var grid = NewRowGrid();
        grid.Margin = new Thickness(0, 4, 0, 4);
        grid.Height = RowHeight;
        grid.Children.Add(NameCell(glyph, label, muted, dim));
        return grid;
    }

    private FrameworkElement NameCell(string glyph, string label, bool muted, bool dim = false)
    {
        Brush color = muted || dim ? Muted : VNotch.Services.UiPalette.PrimaryBrush;
        var namePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        namePanel.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = SegoeSymbolFont,
            FontSize = 16,
            Foreground = color,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        // Muted rows are hints ("no devices"), so they may run into the next column.
        namePanel.Children.Add(MakeLabel(label, 13.5, color, maxWidth: muted ? double.PositiveInfinity : ColName - 30));
        Grid.SetColumn(namePanel, 0);
        if (muted) Grid.SetColumnSpan(namePanel, 3);
        return namePanel;
    }

    private void AddStatus(Grid row, string text, Brush foreground, FrameworkElement? toggle = null)
    {
        // A grid, not a stack, so long hints trim instead of running under the buttons.
        var panel = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (toggle != null)
        {
            toggle.Margin = new Thickness(0, 0, 10, 0);
            panel.Children.Add(toggle);
        }
        var label = MakeLabel(text, 12, foreground);
        label.ToolTip = text;
        Grid.SetColumn(label, 1);
        panel.Children.Add(label);
        Grid.SetColumn(panel, 1);
        row.Children.Add(panel);
    }

    private static void AddAction(Grid row, FrameworkElement action)
    {
        Grid.SetColumn(action, 2);
        row.Children.Add(action);
    }

    private TextBlock MakeLabel(string text, double size, Brush foreground, double maxWidth = double.PositiveInfinity) => new()
    {
        Text = text,
        Foreground = foreground,
        FontSize = size,
        FontWeight = FontWeights.Bold,
        FontFamily = ControlFont,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = maxWidth
    };

    private Border Combo(string glyph, string text, bool interactive, Action<Border>? onClick)
    {
        var border = new Border
        {
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Background = ComboBg,
            BorderBrush = ComboBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = ColAction,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = interactive ? Cursors.Hand : Cursors.Arrow
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = SegoeSymbolFont,
            FontSize = 13,
            Foreground = Muted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        });
        var label = MakeLabel(text, 12, interactive ? VNotch.Services.UiPalette.PrimaryBrush : Muted);
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        if (interactive)
        {
            var caret = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M0,0 L4,4 L8,0 M0,6 L4,2 L8,6"),
                Stroke = Muted,
                StrokeThickness = 1.3,
                Width = 8,
                Height = 8,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0)
            };
            Grid.SetColumn(caret, 2);
            grid.Children.Add(caret);

            border.MouseEnter += (_, _) => border.Background = ComboHover;
            border.MouseLeave += (_, _) => border.Background = ComboBg;
            border.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                onClick?.Invoke(border);
            };
        }
        border.Child = grid;
        return border;
    }

    /// <summary>A read-only chip in the picker's shape, e.g. the wired adapter's name.</summary>
    private Border InfoChip(string glyph, string text)
    {
        var chip = Combo(glyph, text, interactive: false, null);
        chip.ToolTip = text;
        return chip;
    }

    private Border TextButton(string text, bool accent, Action onClick)
    {
        var tint = _accent;
        var label = MakeLabel(text, 12, accent ? InkOn(tint) : VNotch.Services.UiPalette.PrimaryBrush);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var button = new Border
        {
            Height = 28,
            MinWidth = 64,
            CornerRadius = new CornerRadius(6),
            Background = accent ? tint : ComboBg,
            BorderBrush = accent ? tint : ComboBorder,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 0, 6, 0),
            Cursor = Cursors.Hand,
            Child = label
        };
        button.MouseEnter += (_, _) => button.Opacity = accent ? 0.85 : 1;
        button.MouseLeave += (_, _) => button.Opacity = 1;
        if (!accent)
        {
            button.MouseEnter += (_, _) => button.Background = ComboHover;
            button.MouseLeave += (_, _) => button.Background = ComboBg;
        }
        button.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        Squish(button);
        return button;
    }

    private static Border IconButton(string glyph, string tooltip, Action onClick)
    {
        var button = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(6),
            Background = ComboBg,
            BorderBrush = ComboBorder,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = SegoeSymbolFont,
                FontSize = 12,
                Foreground = VNotch.Services.UiPalette.PrimaryBrush,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(button, tooltip);
        button.MouseEnter += (_, _) => button.Background = ComboHover;
        button.MouseLeave += (_, _) => button.Background = ComboBg;
        button.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
        return button;
    }

    private sealed record MenuItemSpec(string Text, bool Selected, string? Glyph, Action? OnClick);

    private void CloseMenu()
    {
        ControlOverlay.Children.Clear();
        ControlOverlay.Visibility = Visibility.Collapsed;
    }

    private void ShowMenu(Border anchor, List<MenuItemSpec> items, Brush? accent = null)
    {
        var tick = accent ?? _accent;
        CloseMenu();
        var list = new StackPanel();
        var container = new Border
        {
            Background = MenuBg,
            BorderBrush = ComboBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(4),
            Width = anchor.ActualWidth > 0 ? anchor.ActualWidth : ColAction,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new ScrollViewer
            {
                MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = list
            },
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 4,
                Opacity = 0.5,
                Color = Colors.Black,
                Direction = 270,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            }
        };
        container.MouseLeftButtonDown += (_, e) => e.Handled = true;

        foreach (var spec in items)
        {
            var item = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 1, 0, 1),
                Background = spec.Selected ? ComboHover : Brushes.Transparent,
                Cursor = spec.OnClick != null ? Cursors.Hand : Cursors.Arrow
            };
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(MakeLabel(spec.Text, 12, spec.OnClick != null ? VNotch.Services.UiPalette.PrimaryBrush : Muted));
            string? trailing = spec.Selected ? "\uE73E" : spec.Glyph;
            if (trailing != null)
            {
                var mark = new TextBlock
                {
                    Text = trailing,
                    FontFamily = SegoeSymbolFont,
                    FontSize = 12,
                    Foreground = spec.Selected ? tick : Muted,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                Grid.SetColumn(mark, 1);
                g.Children.Add(mark);
            }
            item.Child = g;
            if (spec.OnClick is { } onClick)
            {
                bool selected = spec.Selected;
                item.MouseEnter += (_, _) => { if (!selected) item.Background = ComboHover; };
                item.MouseLeave += (_, _) => { if (!selected) item.Background = Brushes.Transparent; };
                item.MouseLeftButtonDown += (_, e) =>
                {
                    e.Handled = true;
                    CloseMenu();
                    onClick();
                };
            }
            list.Children.Add(item);
        }

        try
        {
            var pos = anchor.TransformToVisual(this).Transform(new Point(0, 0));
            container.Margin = new Thickness(pos.X, pos.Y + anchor.ActualHeight + 4, 0, 0);
        }
        catch (InvalidOperationException)
        {
            // Anchor left the visual tree during a refresh.
        }

        ControlOverlay.Children.Add(container);
        ControlOverlay.Visibility = Visibility.Visible;
        ControlOverlay.MouseLeftButtonDown -= ControlOverlay_OutsideClick;
        ControlOverlay.MouseLeftButtonDown += ControlOverlay_OutsideClick;

        if (AnimationConfig.ReduceMotion) return;
        // Unfolds from the button it belongs to: scales up from its top edge with a small overshoot,
        // and the entries drop in one after another.
        var grow = new ScaleTransform(0.9, 0.7);
        var slide = new TranslateTransform(0, -6);
        container.RenderTransformOrigin = new Point(0.5, 0);
        container.RenderTransform = new TransformGroup { Children = { grow, slide } };
        container.BeginAnimation(OpacityProperty, WeatherMotion.To(0, 1, 150, WeatherMotion.Out));
        grow.BeginAnimation(ScaleTransform.ScaleXProperty, WeatherMotion.To(0.9, 1, 420, WeatherMotion.OutBack));
        grow.BeginAnimation(ScaleTransform.ScaleYProperty, WeatherMotion.To(0.7, 1, 420, WeatherMotion.OutBack));
        slide.BeginAnimation(TranslateTransform.YProperty, WeatherMotion.To(-6, 0, 300, WeatherMotion.OutQuint));
        for (int i = 0; i < list.Children.Count; i++)
            WeatherMotion.Rise(list.Children[i], 40 + i * 28, dy: -6, ms: 320);
    }

    private void ControlOverlay_OutsideClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseMenu();
    }

    private static Brush Frozen(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }

    #endregion

    #region Scroll fades and entrance

    private void ControlScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Same edge fades as the mixer: only where there is more to scroll to.
        ControlFadeTopStop.Color = ControlScrollViewer.VerticalOffset > 0.5 ? Colors.Transparent : Colors.Black;
        ControlFadeBottomStop.Color = ControlScrollViewer.VerticalOffset < ControlScrollViewer.ScrollableHeight - 0.5 ? Colors.Transparent : Colors.Black;
    }

    private static readonly ExponentialEase RowRevealEase = CreateRowRevealEase();

    private static ExponentialEase CreateRowRevealEase()
    {
        var ease = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    /// <summary>Rows fall in one after another, like the mixer's when its tab opens.</summary>
    public void RevealRows(TimeSpan baseDelay)
    {
        if (_revealRequestFirst) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollToRequest);
        else ControlScrollViewer.ScrollToTop();
        if (AnimationConfig.ReduceMotion) return;
        int index = 0;
        foreach (UIElement child in ControlRoot.Children)
            if (child is Panel section && section.Visibility == Visibility.Visible)
                index = RevealChildren(section, baseDelay, index);
    }

    private static void RevealElement(FrameworkElement row, TimeSpan delay)
    {
        var shift = new TranslateTransform(0, 8);
        row.RenderTransform = shift;
        var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(280))) { BeginTime = delay, EasingFunction = RowRevealEase };
        var rise = new DoubleAnimation(8, 0, new Duration(TimeSpan.FromMilliseconds(280))) { BeginTime = delay, EasingFunction = RowRevealEase };
        Timeline.SetDesiredFrameRate(fade, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(rise, AnimationConfig.TargetFps);
        fade.Completed += (_, _) =>
        {
            row.BeginAnimation(OpacityProperty, null);
            row.Opacity = 1;
        };
        row.Opacity = 0;
        row.BeginAnimation(OpacityProperty, fade);
        shift.BeginAnimation(TranslateTransform.YProperty, rise);
    }

    /// <summary>
    /// While the tab is open, sessions and requests that were not on screen before fall in like the rows
    /// did when it opened; everything that stayed just stays.
    /// </summary>
    private void RevealNewCards(List<(string Key, FrameworkElement Element)> cards)
    {
        bool animate = IsVisible && !AnimationConfig.ReduceMotion;
        int index = 0;
        foreach (var (key, element) in cards)
        {
            if (animate && !_shownCards.Contains(key))
                RevealElement(element, TimeSpan.FromMilliseconds(60 + Math.Min(index++ * 30, 120)));
        }
        _shownCards.Clear();
        foreach (var (key, _) in cards) _shownCards.Add(key);
    }

    #endregion
}
