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
    /// <summary>The system monitor, a Legion's power modes and key locks, wired and Wi-Fi network and Bluetooth.</summary>
    System,
    /// <summary>Claude Code sessions, approvals, questions and plan limits.</summary>
    Claude,
    /// <summary>Home, from the user's Raspberry Pi dashboard: lights, heating, air conditioner, sockets, scenes, the Pi.</summary>
    Pi,
    /// <summary>School, from the same dashboard: the next deadline, the others, tests and to-dos.</summary>
    School,
    /// <summary>Roblox, from the same dashboard: all of the creator's games added up.</summary>
    Roblox
}

/// <summary>
/// The quick settings that live in Windows' notification area, in the notch: the system monitor, wired and
/// Wi-Fi network and Bluetooth devices on one tab, Claude Code sessions on another. Sound lives in the audio
/// tab and the notification switches in the settings window, so neither is repeated here. Built in code with
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
    private static readonly Brush CardBg = Frozen("#0FFFFFFF");
    private static readonly Brush CardHover = Frozen("#1CFFFFFF");

    private static readonly FontFamily SegoeSymbolFont = new("pack://application:,,,/Fonts/#Segoe MDL2 Assets, Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol, Segoe UI");
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas");

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
    // The session a message is being typed for, and what is typed so far (kept across rebuilds).
    private string? _composingSessionId;
    private string _composeDraft = "";
    private TextBox? _composerBox;
    private bool _rebuildAfterComposing;
    // Rings around busy sessions' dots; they ripple only while the tab is on screen.
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
    private string _claudeKey = "";

    private readonly StackPanel _statsSection = new();
    private readonly StackPanel _networkSection = new();
    private readonly StackPanel _bluetoothSection = new();
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
        ControlRoot.Children.Add(_displaysSection);
        _expanded["displays"] = false;
        ControlRoot.Children.Add(_claudeSection);
        Grid.SetIsSharedSizeScope(_claudeSection, true);
        ControlRoot.Children.Add(_appsSection);

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RefreshInterval };
        _refreshTimer.Tick += (_, _) => Refresh();
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateElapsedLabels();

        IsVisibleChanged += OnIsVisibleChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        AnimationConfig.ReduceMotionChanged += OnResetMotionChanged;
    }

    /// <summary>
    /// One quiet read while the tab is not on screen, shortly after start, so its first open shows it filled (and
    /// sized) instead of "loading".
    /// </summary>
    internal void Prefetch()
    {
        if (_disposed || IsVisible) return;
        switch (Page)
        {
            case ControlCenterPage.Pi or ControlCenterPage.School:
                RefreshPi();
                break;
            case ControlCenterPage.Roblox:
                RefreshRoblox();
                break;
            case ControlCenterPage.Claude:
                RefreshUsage(UsageViewMinAge);
                RefreshCodexUsage();
                BuildClaudeSection();
                break;
            default:
                Refresh();
                break;
        }
    }

    /// <summary>Set in XAML; fixed once the view is built.</summary>
    public ControlCenterPage Page { get; set; }

    /// <summary>Raised when a Claude Code request waits for an answer, so the shell can open this tab.</summary>
    public event Action? AttentionRequested;

    /// <summary>Raised after the Claude page was rebuilt, so the shell can fit the notch to it.</summary>
    public event Action? ContentHeightChanged;

    /// <summary>Raised when a session's window was brought to the front from its card.</summary>
    public event Action? SessionOpened;

    /// <summary>True while a message is typed here: the notch then has to take keyboard focus.</summary>
    public event Action<bool>? KeyboardInputRequested;

    /// <summary>For the <see cref="ControlCenterPage.System"/> page.</summary>
    internal void Attach(Func<bool> usesGlass)
    {
        _usesGlass = usesGlass;
        if (LegionService.IsSupported) AttachLegion();
        BuildCompactSystem(new Snapshot());
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
        ControlScrollViewer.OpacityMask = null;
        tracker.Changed += OnClaudeChanged;
        tracker.PermissionRequested += OnPermissionRequested;
        tracker.UsageMayHaveChanged += OnUsageMayHaveChanged;
        _claudeInstalled = ClaudeCodeSetup.IsInstalled(ClaudeHookServer.HookUrl);
        // Quiet until the first build says otherwise, so the backdrop never starts in the system tab's blue.
        ApplyClaudeMood(ClaudeQuiet);
        RefreshOnceShellIsReady();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var panel in new[] { _homeLightSection, _piSchoolSection, _legionModeSection })
            foreach (UIElement child in panel.Children)
                if (child is SkeletonPlaceholder skeleton) skeleton.Dispose();
        _refreshTimer.Stop();
        _clockTimer.Stop();
        _displayTimer?.Stop();
        _expireDisplayChanges = true;
        _displayChanges?.Tick();
        _displayBrightness?.Dispose();
        if (_displayApi is IDisposable displayApi) displayApi.Dispose();
        _piTimer?.Stop();
        _robloxTimer?.Stop();
        _schoolCountdownTimer?.Stop();
        StopDrawerWheel();
        if (_drawerHeight != null) Motion.Stop(_drawerHeight, WeatherTween.ValueProperty);
        StopLiveTexts();
        DetachOutput();
        CoverColorsFile.Changed -= OnCoverColorsChanged;
        _legionTimer?.Stop();
        _legionLoops.StopAll();
        UpdateHomeRendering();
        _targetSend?.Stop();
        _wifiLoops.StopAll();
        _bluetoothLoops.StopAll();
        _backdrop?.SetDrifting(false);
        StopTokenClock();
        StopCodexClock();
        DisposeCodexAccounts();
        StopClaudeFaceMotion();
        HideAccountNotice();
        StopResetFeedback();
        SetGpuSampling(false);
        StopPeaks();
        _endDisarm?.Stop();
        _stats.Dispose();
        IsVisibleChanged -= OnIsVisibleChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        AnimationConfig.ReduceMotionChanged -= OnResetMotionChanged;
        if (_claude != null)
        {
            _claude.Changed -= OnClaudeChanged;
            _claude.PermissionRequested -= OnPermissionRequested;
            _claude.UsageMayHaveChanged -= OnUsageMayHaveChanged;
        }
        GC.SuppressFinalize(this);
    }

    #region Lifecycle

    private TabLedger? _seen;

    /// <summary>What this tab last showed; faces ask it which values changed while the tab was away.</summary>
    internal TabLedger Seen => _seen ??= new TabLedger(() => IsVisible);

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed) return;
        if (IsVisible)
        {
            Seen.Shown();
            if (Page == ControlCenterPage.System)
            {
                ForgetStaleHistory();
                if (IsExpanded("displays")) BuildDisplaysSection();
                if (_compactSystemReady) RefreshCompactDisplays();
            }
            Refresh();
            StartLiveTexts();
            if (Page == ControlCenterPage.Claude)
            {
                RefreshUsage(UsageViewMinAge);
                _clockTimer.Start();
                StartTokenClock();
                StartCodexClock();
                if (_resetStates.Count > 0) StartResetFeedback();
            }
            else if (Page is ControlCenterPage.Pi or ControlCenterPage.School)
            {
                OnPiVisibleChanged(true);
            }
            else if (Page == ControlCenterPage.Roblox)
            {
                OnRobloxVisibleChanged(true);
            }
            else
            {
                _refreshTimer.Start();
                SetGpuSampling(true);
                OnLegionVisibleChanged(true);
                DrawSparksIn();
            }
            if (_backdrop != null) _backdrop.Suppressed = Page != ControlCenterPage.Claude && _usesGlass();
        }
        else
        {
            Seen.Hidden();
            EndSchoolPreview();
            _refreshTimer.Stop();
            _clockTimer.Stop();
            StopLiveTexts();
            if (_outputOpen) SetOutputOpen(false);
            StopTokenClock();
            StopCodexClock();
            StopResetFeedback();
            StopClaudeFaceMotion();
            HideAccountNotice();
            if (_accountsOpen) SetAccountsOpen(false, animate: false);
            SetGpuSampling(false);
            if (Page == ControlCenterPage.System)
            {
                _stats.Reset();
                _processes.Reset();
                _loadWarning.Pause();
                ++_compactDisplayRevision;
                _topMode = TopMode.Off;
                _wifiLoops.StopAll();
                _bluetoothLoops.StopAll();
                OnLegionVisibleChanged(false);
            }
            _backdrop?.SetDrifting(false);
            CloseMenu();
            if (_composingSessionId != null) StopComposing();
            // The next open shows what matters then, not the session picked last time.
            _focusedSessionId = null;
            if (Page is ControlCenterPage.Pi or ControlCenterPage.School) OnPiVisibleChanged(false);
            if (Page == ControlCenterPage.Roblox) OnRobloxVisibleChanged(false);
        }
    }

    private void OnLanguageChanged() => Dispatcher.BeginInvoke(() =>
    {
        _networkKey = _bluetoothKey = _claudeKey = _laptopKey = "";
        _statsTiles = null;
        _piKey = _legionKey = _homeKey = "";
        if (_snapshot != null) ApplySnapshot(_snapshot);
        BuildClaudeSection();
        BuildPiPage();
        BuildLegionPage();
        _robloxDrawerKey = "";
        BuildRoblox();
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
        if (Page is ControlCenterPage.Pi or ControlCenterPage.School)
        {
            RefreshPi();
            return;
        }
        if (Page == ControlCenterPage.Roblox)
        {
            RefreshRoblox();
            return;
        }
        if (_refreshing)
        {
            // A read is under way: one more follows it, so a list that was just asked for comes at once.
            _refreshAgain = true;
            return;
        }
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
            if (_refreshAgain && !_disposed)
            {
                _refreshAgain = false;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Refresh));
            }
        }
    }

    private bool _refreshAgain;

    private async void RefreshUsage(TimeSpan minAge)
    {
        if (_usage == null || _isLocalOnly()) return;
        try
        {
            if (await Task.Run(() => _usage.RefreshAsync(minAge)) != null && !_disposed)
            {
                BuildClaudeSection();
                AllowanceDataChanged?.Invoke();
            }
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
        public SystemStats? Stats;
        public List<ProcessLoad>? Top;
        public LaptopStatus? Laptop;
        public int? GpuTemp;
        public List<OutputDevice>? Outputs;
        public List<AudioSessionInfo>? Apps;
        public WaveXlrState? WaveXlr;
        public bool? MicMuted;
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
        if (withStats)
        {
            s.Laptop = LaptopControl.Read();
            s.GpuTemp = GpuTemperature.Read();
        }
        if (Page == ControlCenterPage.System && withStats) ReadSound(s);
        return s;
    }

    private void ApplySnapshot(Snapshot s)
    {
        _snapshot = s;
        if (Page == ControlCenterPage.System)
        {
            BuildCompactSystem(s);
            return;
        }
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
        if (Page == ControlCenterPage.System)
        {
            if (_displaysSection.Children.Count == 0) BuildDisplaysSection();
            ApplySound(s);
        }
    }

    private string NetworkKey(Snapshot s)
    {
        var sb = new StringBuilder();
        sb.Append(IsExpanded("network")).Append('|').Append(s.WifiOn).Append('|').Append(s.WifiStatus).Append('|');
        foreach (var w in s.Wired) sb.Append(w.Name).Append(w.IsUp).Append(';');
        foreach (var n in s.Networks)
            if (n.IsConnected) sb.Append(n.Ssid).Append(WifiBars(n.Signal));
        // When the search wave runs out, the arcs are drawn again without it.
        sb.Append('|').Append(IsWifiSearching);
        return sb.ToString();
    }

    private string BluetoothKey(Snapshot s)
    {
        var sb = new StringBuilder();
        sb.Append(IsExpanded("bluetooth")).Append('|').Append(s.BluetoothOn).Append('|');
        foreach (var d in s.Devices) sb.Append(d.Address).Append(d.IsConnected).Append(d.Battery).Append(d.CanConnect).Append(IsConnecting(d)).Append(';');
        return sb.ToString();
    }

    #endregion

    #region System monitor

    private sealed class StatTile
    {
        public required TextBlock Value;
        public TextBlock? Detail;
        public TextBlock? Temp;
        public ScaleTransform? Fill;
        public Grid? Bar;
        public TranslateTransform? Peak;
        public double PeakValue = double.NaN;
        public RollingNumber? Roll;
        public RollingNumber? TemperatureRoll;
        public double Last = double.NaN;
        public SolidColorBrush? ValueInk;
        public Spark? Spark;
        public PingDot? Ping;
        public FrameworkElement? Down, Up;
        public FrameworkElement? Heading;
    }

    private sealed class StatTiles
    {
        // Ping and network made way for the sound output (AddStatTile still knows how to draw them).
        public required StatTile Cpu, Gpu, Ram;
    }

    private StatTiles? _statsTiles;
    // How fast a peak mark sinks back, in points per refresh (every three seconds).
    private const double PeakFall = 6;
    private const double JumpToFlash = 25;
    private (int Cpu, int Gpu)? _temps;

    /// <summary>Five tiles built once; every refresh only changes their numbers and slides their bars.</summary>
    private void BuildStatsSection()
    {
        if (_compactSystemReady) { BuildCompactSystem(_snapshot ?? new Snapshot()); return; }
        _statsSection.Children.Clear();
        var header = BuildSectionHeader("system", Loc.Get("control.system"), _keepAwakeLabel?.Invoke() ?? "",
            _cycleKeepAwake == null ? null : () => _cycleKeepAwake(), topMargin: 0);
        _keepAwakeText = header is Panel headerPanel ? headerPanel.Children.OfType<TextBlock>().LastOrDefault(t => Grid.GetColumn(t) == 2) : null;
        _statsSection.Children.Add(header);
        AddPhoneRow(_statsSection);
        _statsTiles = null;
        if (!IsExpanded("system"))
        {
            // Collapsed: a placeholder so the section is not rebuilt on every refresh.
            _statsTiles = new StatTiles { Cpu = Dummy(), Gpu = Dummy(), Ram = Dummy() };
            _outputTile = null;
            return;
        }

        var grid = new Grid { Margin = new Thickness(0, 0, 0, 2), Tag = TileGridTag };
        for (int i = 0; i < 9; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(8) });
        _statsTiles = new StatTiles
        {
            Cpu = AddStatTile(grid, 0, "CPU", "cpu", () => ToggleTop(TopMode.Cpu)),
            Gpu = AddStatTile(grid, 2, "GPU", "gpu", () => Launch("taskmgr.exe")),
            Ram = AddStatTile(grid, 4, "RAM", "ram", () => ToggleTop(TopMode.Memory))
        };
        _outputTile = AddOutputTile(grid, 6, 3);
        _statsSection.Children.Add(grid);
        _outputSection.Children.Clear();
        _statsSection.Children.Add(_outputSection);
        _outputSection.Children.Add(_outputDrawer);
        if (_outputOpen) BuildOutputRow(animate: false);
        _topList.Children.Clear();
        _topRows.Clear();
        _topListShown = false;
        _statsSection.Children.Add(_topList);
        if (_temps is { } temps) ShowTemps(temps.Cpu, temps.Gpu);

        static StatTile Dummy() => new() { Value = new TextBlock() };
    }

    /// <param name="metric">"cpu", "gpu" or "ram": a load tile with a bar and a curve; null for ping and network.</param>
    private StatTile AddStatTile(Grid grid, int column, string label, string? metric, Action? onClick)
    {
        bool isNet = column == 8, isPing = column == 6;
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(MakeLabel(label, 11, _moodInk));
        var temp = MakeLabel("", 11, Muted);
        temp.Visibility = Visibility.Collapsed;
        head.Children.Add(temp);

        var valueInk = new SolidColorBrush(((SolidColorBrush)VNotch.Services.UiPalette.PrimaryBrush).Color);
        var value = MakeLabel("–", isNet ? 13.5 : 15, valueInk);
        value.Margin = new Thickness(0, 3, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(head);
        var tile = new StatTile { Value = value, Temp = temp, ValueInk = valueInk, Heading = head };
        tile.TemperatureRoll = new RollingNumber(v => temp.Text = $" · {Math.Round(v)}°", temp);

        if (isNet)
        {
            // ↓ and ↑ are their own glyphs so they can hop with the traffic.
            tile.Down = Arrow("↓", 13.5, VNotch.Services.UiPalette.PrimaryBrush);
            value.Margin = new Thickness(0);
            stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0), Children = { tile.Down, value } });
        }
        else stack.Children.Add(value);

        if (metric != null)
        {
            tile.Roll = new RollingNumber(v =>
            {
                if (double.IsFinite(tile.Last)) SetRollingDetailText(value, $"{Math.Round(v)} %");
            }, value);
            tile.Roll.To(0, 0);
            var fill = new ScaleTransform(0, 1);
            var peak = new TranslateTransform();
            var bar = new Grid
            {
                Height = 3,
                Margin = new Thickness(0, 7, 0, 1),
                Children =
                {
                    new Border { Background = Track, CornerRadius = new CornerRadius(1.5) },
                    new Border { Background = MoodBar, CornerRadius = new CornerRadius(1.5), RenderTransform = fill, RenderTransformOrigin = new Point(0, 0.5) },
                    // Where the load peaked lately; it sinks back slowly, like a level meter.
                    new Border
                    {
                        Width = 2, Height = 7, Margin = new Thickness(0, -2, 0, -2), CornerRadius = new CornerRadius(1),
                        Background = PeakBrush, HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = peak, Opacity = 0
                    }
                }
            };
            bar.SizeChanged += (_, _) => PlacePeak(tile, animate: false);
            stack.Children.Add(bar);
            tile.Fill = fill;
            tile.Bar = bar;
            tile.Peak = peak;
            bar.IsVisibleChanged += (_, _) =>
            {
                if (bar.IsVisible) return;
                Motion.Stop(fill, ScaleTransform.ScaleXProperty, double.IsFinite(tile.Last) ? Math.Clamp(tile.Last / 100, 0, 1) : 0);
                Motion.Stop(peak, TranslateTransform.XProperty);
            };
            tile.Spark = NewSpark(metric);
        }
        else
        {
            var detail = MakeLabel("", 10.5, Muted);
            if (isNet)
            {
                tile.Up = Arrow("↑", 10.5, Muted);
                stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0), Children = { tile.Up, detail } });
            }
            else
            {
                detail.Margin = new Thickness(0, 2, 0, 0);
                stack.Children.Add(detail);
            }
            tile.Detail = detail;
        }
        if (isPing)
        {
            tile.Ping = NewPingDot();
            head.Children.Add(tile.Ping.Host);
        }

        var body = new Grid();
        if (tile.Spark != null) body.Children.Add(tile.Spark.Host);
        body.Children.Add(stack);
        var card = Card();
        card.Padding = new Thickness(12, 9, 12, 9);
        card.Child = body;
        if (onClick != null)
        {
            // CPU and RAM list the busiest apps, GPU opens Task Manager, the network tile its settings.
            card.Cursor = Cursors.Hand;
            Pressable(card, () => CardBg, CardHover, grow: true);
            DetailHover(card, head, y: -1.5);
            card.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                onClick();
            };
        }
        Grid.SetColumn(card, column);
        grid.Children.Add(card);
        return tile;

        TextBlock Arrow(string glyph, double size, Brush ink)
        {
            var arrow = MakeLabel(glyph, size, ink);
            arrow.Margin = new Thickness(0, 0, 4, 0);
            arrow.RenderTransform = new TranslateTransform();
            return arrow;
        }
    }

    private static readonly Brush PeakBrush = Frozen("#C0FFFFFF");

    private void ApplyStats(SystemStats stats)
    {
        RecordSample(stats);
        // On a Legion the power mode colours the tab; elsewhere the load does.
        if (_modeTile == null || _legion == null) SetMood(MoodLoad(stats));
        if (_statsTiles is not { } tiles || !IsExpanded("system")) return;
        SetLoad(tiles.Cpu, stats.CpuPercent);
        SetLoad(tiles.Gpu, stats.GpuPercent);
        SetLoad(tiles.Ram, stats.RamPercent);
        tiles.Ram.Value.ToolTip = $"{stats.RamUsedGb:0.0} / {stats.RamTotalGb:0.0} GB";
    }

    private void SetLoad(StatTile tile, double? percent)
    {
        if (percent is { } p && tile.Roll != null)
        {
            if (!double.IsNaN(tile.Last) && Math.Abs(p - tile.Last) >= JumpToFlash && tile.ValueInk != null) Flash(tile.Value, tile.ValueInk);
            tile.Last = p;
            tile.Roll.To(p);
        }
        else
        {
            tile.Last = double.NaN;
            tile.Roll?.To(0, 0);
            SetDetailText(tile.Value, "–");
        }
        if (tile.Spark != null) DrawSpark(tile.Spark);
        if (tile.Fill == null) return;
        double target = Math.Clamp((percent ?? 0) / 100, 0, 1);
        if (percent is { } level) tile.PeakValue = double.IsNaN(tile.PeakValue) ? level : Math.Max(level, tile.PeakValue - PeakFall);
        PlacePeak(tile, animate: IsVisible);
        if (!IsVisible) Motion.Stop(tile.Fill, ScaleTransform.ScaleXProperty, target);
        else Motion.To(tile.Fill, ScaleTransform.ScaleXProperty, target, Motion.Glide, fps: RippleFps);
    }

    private static void PlacePeak(StatTile tile, bool animate)
    {
        if (tile.Peak == null || tile.Bar == null || double.IsNaN(tile.PeakValue)) return;
        double width = tile.Bar.ActualWidth;
        if (width <= 0) return;
        double x = Math.Clamp(tile.PeakValue / 100 * width - 1, 0, width - 2);
        if (tile.Bar.Children[^1] is UIElement mark && mark.Opacity == 0) mark.Opacity = 1;
        if (animate) WeatherMotion.Glide(tile.Peak, TranslateTransform.XProperty, x, 900);
        else
        {
            tile.Peak.BeginAnimation(TranslateTransform.XProperty, null);
            tile.Peak.X = x;
        }
    }

    /// <summary>A Legion reports its temperatures: they sit next to the CPU and GPU labels.</summary>
    private void ShowTemps(int cpu, int gpu)
    {
        _temps = (cpu, gpu);
        if (_statsTiles is not { } tiles) return;
        Set(tiles.Cpu, cpu);
        Set(tiles.Gpu, gpu);

        static void Set(StatTile tile, int celsius)
        {
            var label = tile.Temp;
            if (label == null) return;
            bool entering = label.Visibility != Visibility.Visible && celsius > 0;
            if (celsius <= 0)
            {
                if (!label.IsVisible || AnimationConfig.ReduceMotion) label.Visibility = Visibility.Collapsed;
                else Motion.To(label, OpacityProperty, 0, Motion.Exit, done: () =>
                {
                    if (label.Opacity <= .01) label.Visibility = Visibility.Collapsed;
                });
                return;
            }
            label.Visibility = Visibility.Visible;
            tile.TemperatureRoll?.To(celsius);
            if (entering) DetailArrive(label, 0);
            else if (Motion.IsRunning(label, OpacityProperty)) Motion.To(label, OpacityProperty, 1, Motion.Tile);
            var color = ((SolidColorBrush)(celsius >= 90 ? HotTemp : Muted)).Color;
            if (label.Foreground is not SolidColorBrush { IsFrozen: false } ink)
                label.Foreground = ink = new SolidColorBrush(((SolidColorBrush)label.Foreground).Color);
            if (label.IsVisible) WeatherMotion.Tint(ink, color);
            else { ink.BeginAnimation(SolidColorBrush.ColorProperty, null); ink.Color = color; }
        }
    }

    private static readonly Brush HotTemp = Frozen("#D98A84");

    /// <summary>Every opening draws the curves in again, the way the weather tab draws its charts.</summary>
    private void DrawSparksIn()
    {
        if (_statsTiles is not { } tiles) return;
        int i = 0;
        foreach (var tile in new[] { tiles.Cpu, tiles.Gpu, tiles.Ram })
        {
            if (tile.Heading != null) DetailArrive(tile.Heading, i);
            DetailArrive(tile.Value, i + 1);
            tile.Roll?.Reveal();
            if (tile.Fill != null && double.IsFinite(tile.Last) && !Motion.IsRunning(tile.Fill, ScaleTransform.ScaleXProperty))
                Motion.To(tile.Fill, ScaleTransform.ScaleXProperty, Math.Clamp(tile.Last / 100, 0, 1), Motion.Glide,
                    from: 0, delay: Motion.Stagger(i + 2), fps: RippleFps);
            if (tile.Spark != null) DrawSparkIn(tile.Spark, Motion.Stagger(i + 2).TotalMilliseconds);
            i++;
        }
    }

    private enum TopMode
    {
        Off,
        Cpu,
        Memory
    }

    private const int TopProcessCount = 5;
    private const double TopRowHeight = 30;
    private static readonly TimeSpan EndConfirmWindow = TimeSpan.FromSeconds(3);
    private static readonly Brush EndConfirmBg = Frozen("#33FF453A");
    private readonly StackPanel _topList = new() { Margin = new Thickness(0, 8, 0, 0) };
    // One row per app, kept across refreshes so a new order glides instead of jumping.
    private readonly Dictionary<string, TopRowParts> _topRows = new(StringComparer.OrdinalIgnoreCase);
    private Canvas? _topCanvas;
    private TextBlock? _topHeader;
    private TopMode _topMode;
    // The app whose End button was pressed once; a second press within the window ends it.
    private string? _endArmed;
    private DateTime _endArmedUntil;
    private bool _topListShown;
    private List<ProcessLoad>? _lastTop;

    private sealed class TopRowParts
    {
        public required Grid Row;
        public required TextBlock Title, Cpu, Memory;
        public required TranslateTransform Slot;
        public required RollingNumber CpuRoll, MemoryRoll;
        public int Index = -1;
    }

    /// <summary>CPU and RAM tiles open the apps that use the most of it; the same tile closes the list again.</summary>
    private void ToggleTop(TopMode mode)
    {
        _topMode = _topMode == mode ? TopMode.Off : mode;
        _endArmed = null;
        // Another list (or none): its rows arrive fresh instead of gliding from the other order.
        _topList.Children.Clear();
        _topRows.Clear();
        _topListShown = false;
        if (_topMode == TopMode.Off)
        {
            _processes.Reset();
            return;
        }
        RefreshSoon(0);
    }

    private void UpdateTopList(List<ProcessLoad>? top)
    {
        if (_topMode == TopMode.Off || top == null)
        {
            if (_topMode == TopMode.Off && _topListShown)
            {
                _topList.Children.Clear();
                _topRows.Clear();
                _topListShown = false;
            }
            return;
        }

        _lastTop = top;
        if (!_topListShown || _topCanvas == null || _topHeader == null)
        {
            _topList.Children.Clear();
            _topRows.Clear();
            _topCanvas = new Canvas { Height = TopRowHeight * top.Count };
            var header = TopRow("", "CPU", "RAM", header: true, out _topHeader, out _, out _);
            var canvas = _topCanvas;
            canvas.SizeChanged += (_, e) =>
            {
                foreach (var parts in _topRows.Values) parts.Row.Width = e.NewSize.Width;
            };
            var card = Card();
            card.Padding = new Thickness(14, 8, 10, 8);
            card.Child = new StackPanel { Children = { header, canvas } };
            _topList.Children.Add(card);
            // Falls in when it opens; later refreshes only change the numbers and the order.
            if (IsVisible && !AnimationConfig.ReduceMotion) RevealElement(card, TimeSpan.Zero);
            _topListShown = true;
        }
        _topHeader.Text = Loc.Get(_topMode == TopMode.Cpu ? "control.top.byCpu" : "control.top.byMemory");
        if (IsVisible) Motion.To(_topCanvas, HeightProperty, TopRowHeight * top.Count, Motion.Glide);
        else Motion.Stop(_topCanvas, HeightProperty, TopRowHeight * top.Count);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < top.Count; i++)
        {
            var load = top[i];
            seen.Add(load.Name);
            if (!_topRows.TryGetValue(load.Name, out var parts))
            {
                var row = TopRow(load.Title, "", "", header: false, out var title, out var cpu, out var memory);
                var slot = new TranslateTransform(0, i * TopRowHeight);
                row.RenderTransform = slot;
                if (_topCanvas.ActualWidth > 0) row.Width = _topCanvas.ActualWidth;
                _topCanvas.Children.Add(row);
                parts = new TopRowParts { Row = row, Title = title, Cpu = cpu, Memory = memory, Slot = slot, Index = i,
                    CpuRoll = new RollingNumber(v => cpu.Text = $"{v:0.0} %", cpu),
                    MemoryRoll = new RollingNumber(v => memory.Text = FormatBytes((long)Math.Max(0, v)), memory) };
                _topRows[load.Name] = parts;
                if (IsVisible && !AnimationConfig.ReduceMotion)
                    PopIn(row, Motion.Stagger(i).TotalMilliseconds);
            }
            SetDetailText(parts.Title, load.Title);
            parts.Title.ToolTip = load.Instances > 1 ? $"{load.Name} × {load.Instances}" : null;
            parts.CpuRoll.To(load.CpuPercent);
            parts.MemoryRoll.To(load.MemoryBytes);
            parts.Cpu.Foreground = _topMode == TopMode.Cpu ? VNotch.Services.UiPalette.PrimaryBrush : Muted;
            parts.Memory.Foreground = _topMode == TopMode.Memory ? VNotch.Services.UiPalette.PrimaryBrush : Muted;
            SetEndButton(parts, load);
            if (parts.Index == i) continue;
            // A new place in the order: glide there with a little overshoot.
            double y = i * TopRowHeight;
            if (IsVisible && !AnimationConfig.ReduceMotion)
                Motion.To(parts.Slot, TranslateTransform.YProperty, y, Motion.Glide);
            else
            {
                parts.Slot.BeginAnimation(TranslateTransform.YProperty, null);
                parts.Slot.Y = y;
            }
            parts.Index = i;
        }
        // Apps that dropped out of the list fade away.
        foreach (var name in new List<string>(_topRows.Keys))
        {
            if (seen.Contains(name)) continue;
            var gone = _topRows[name];
            _topRows.Remove(name);
            var host = _topCanvas;
            if (!IsVisible || AnimationConfig.ReduceMotion)
            {
                host.Children.Remove(gone.Row);
                continue;
            }
            ExitElement(gone.Row, () => host.Children.Remove(gone.Row));
        }
    }

    private Grid TopRow(string name, string cpu, string memory, bool header,
        out TextBlock title, out TextBlock cpuLabel, out TextBlock memoryLabel)
    {
        var grid = new Grid { Height = header ? 22 : TopRowHeight };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        title = MakeLabel(name, header ? 11 : 12.5, header ? Muted : VNotch.Services.UiPalette.PrimaryBrush);
        grid.Children.Add(title);
        cpuLabel = MakeLabel(cpu, header ? 11 : 12, Muted);
        cpuLabel.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(cpuLabel, 1);
        grid.Children.Add(cpuLabel);
        memoryLabel = MakeLabel(memory, header ? 11 : 12, Muted);
        memoryLabel.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(memoryLabel, 2);
        grid.Children.Add(memoryLabel);
        return grid;
    }

    /// <summary>The End button in its current state: "End", or "Sure?" in red once armed.</summary>
    private void SetEndButton(TopRowParts parts, ProcessLoad load)
    {
        for (int i = parts.Row.Children.Count - 1; i >= 0; i--)
            if (Grid.GetColumn(parts.Row.Children[i]) == 3) parts.Row.Children.RemoveAt(i);
        if (!load.CanEnd) return;
        bool armed = _endArmed == load.Name && DateTime.UtcNow < _endArmedUntil;
        string name = load.Name;
        var end = TextButton(Loc.Get(armed ? "control.top.confirm" : "control.top.end"), accent: false, () => EndApp(name));
        end.Height = 24;
        end.MinWidth = 76;
        end.Margin = new Thickness(0);
        if (armed)
        {
            end.Background = EndConfirmBg;
            ((TextBlock)end.Child).Foreground = HotTemp;
        }
        Grid.SetColumn(end, 3);
        parts.Row.Children.Add(end);
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
        _accent = _mood;
        CloseMenu();
        _wifiLoops.StopAll();
        _wifiIcon = null;
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
        AddStatus(wiredRow, wiredStatus, wired?.IsUp == true ? _moodSoft : Muted);
        if (wired != null) AddAction(wiredRow, InfoChip("\uE839", wired.Description));
        _networkSection.Children.Add(wiredRow);

        WifiNetwork? current = null;
        foreach (var n in s.Networks)
            if (n.IsConnected) { current = n; break; }

        // The arcs light up to the signal and pop in from the dot outwards.
        var wifiRow = NewRow("", Loc.Get("control.wifi"));
        var icon = NewWifiIcon(current == null ? 0 : WifiBars(current.Signal), connected: current != null && s.WifiOn != false);
        SetRowIcon(wifiRow, icon.Host);
        _wifiIcon = icon;
        if (IsVisible) ArcsArrive(icon, 120);
        if (current == null && IsWifiSearching) ArcsSearch(icon);
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
        ShowMenu(anchor, items, _mood);
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
        // A wave runs through the arcs until Windows has connected (or gives up).
        _wifiSearchUntil = DateTime.UtcNow + WifiSearchTime;
        if (_wifiIcon != null && IsVisible) ArcsSearch(_wifiIcon);
        RefreshSoon(2500);
        RefreshSoon(6000);
    }

    #endregion

    #region Launching

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

    #region Bluetooth

    private void BuildBluetoothSection(Snapshot s)
    {
        _accent = _mood;
        _bluetoothLoops.StopAll();
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

        int ringIndex = 0;
        foreach (var d in devices)
        {
            var device = d;
            var row = NewRow(d.Glyph, d.Name);
            string status = d.IsConnected ? Loc.Get("control.connected") : Loc.Get("control.notConnected");
            if (d.Battery >= 0) status += $" · {d.Battery} %";
            AddStatus(row, status, d.IsConnected ? _moodSoft : Muted);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            // The battery as a ring that fills up, next to the button.
            if (d.IsConnected && d.Battery >= 0) actions.Children.Add(BatteryRing(d.Battery, 200 + ringIndex++ * 80));
            if (d.CanConnect)
            {
                var button = TextButton(d.IsConnected ? Loc.Get("control.disconnect") : Loc.Get("control.connect"),
                    accent: false, () => SetBluetoothConnected(device, !device.IsConnected));
                // Asked to connect: the button breathes in the tab's colour until the device is there.
                if (IsConnecting(d)) Breathe(button);
                actions.Children.Add(button);
            }
            if (actions.Children.Count > 0) AddAction(row, actions);
            _bluetoothSection.Children.Add(row);
        }
    }

    private void Breathe(Border button)
    {
        var rest = ((SolidColorBrush)ComboBorder).Color;
        var edge = new SolidColorBrush(rest);
        button.BorderBrush = edge;
        _bluetoothLoops.Run(edge, SolidColorBrush.ColorProperty, new ColorAnimation(rest, _mood.Color, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = WeatherMotion.Sine
        });
    }

    private bool IsConnecting(BluetoothDevice device)
    {
        if (!_bluetoothConnecting.TryGetValue(device.Address, out var until)) return false;
        if (!device.IsConnected && DateTime.UtcNow < until) return true;
        _bluetoothConnecting.Remove(device.Address);
        return false;
    }

    private void SetBluetoothConnected(BluetoothDevice device, bool connect)
    {
        Task.Run(() => BluetoothControl.SetConnected(device, connect));
        if (connect && _snapshot != null)
        {
            _bluetoothConnecting[device.Address] = DateTime.UtcNow + BluetoothConnectTime;
            _bluetoothKey = BluetoothKey(_snapshot);
            BuildBluetoothSection(_snapshot);
        }
        // The device answers a moment later, if it's on and in reach.
        RefreshSoon(1500);
        RefreshSoon(5000);
    }

    #endregion

    #region Claude Code

    /// <summary>
    /// The height the page needs at <paramref name="width"/>, so the notch can fit it instead of leaving
    /// a gap under the last card.
    /// </summary>
    public double MeasureContentHeight(double width)
    {
        if (Page == ControlCenterPage.Claude) BuildClaudeSection();
        var padding = ControlScrollViewer.Padding;
        var bleed = ControlRoot.Margin;
        ControlRoot.Measure(new Size(Math.Max(0, width - padding.Left - padding.Right + bleed.Left + bleed.Right), double.PositiveInfinity));
        double height = Math.Max(0, ControlRoot.DesiredSize.Height - bleed.Top - bleed.Bottom);
        return IsLayoutFollowing ? height : Math.Ceiling(height);
    }

    private Func<bool>? _soundsOn;
    private Action<bool>? _setSounds;

    /// <summary>The Claude page's speaker button reads and flips the sound setting the shell owns.</summary>
    private Func<string>? _keepAwakeLabel;
    private Action? _cycleKeepAwake;
    private Func<KeepAwakeMode>? _keepAwakeMode;
    private Action<KeepAwakeMode>? _setKeepAwakeMode;
    private TextBlock? _keepAwakeText;

    /// <summary>The "System" header's right side: keep-awake mode or the reason it holds the PC awake; a tap steps the mode.</summary>
    internal void AttachKeepAwake(Func<string> label, Action cycle, Func<KeepAwakeMode>? mode = null, Action<KeepAwakeMode>? setMode = null)
    {
        _keepAwakeLabel = label;
        _cycleKeepAwake = cycle;
        _keepAwakeMode = mode;
        _setKeepAwakeMode = setMode;
        RefreshKeepAwake();
    }

    /// <summary>New words in the header label, swapped with a short fade.</summary>
    internal void RefreshKeepAwake()
    {
        if (_compactSystemReady) { BuildCompactSystem(_snapshot ?? new Snapshot()); }
        if (_keepAwakeText is not { } label || _keepAwakeLabel == null) return;
        string text = _keepAwakeLabel();
        if (label.Text == text) return;
        if (AnimationConfig.ReduceMotion || !label.IsVisible)
        {
            label.Text = text;
            return;
        }
        var fadeOut = WeatherMotion.To(label.Opacity, 0, 70, WeatherMotion.Out);
        fadeOut.Completed += (_, _) =>
        {
            label.Text = text;
            label.BeginAnimation(OpacityProperty, WeatherMotion.To(0, 1, 110, WeatherMotion.Out));
        };
        label.BeginAnimation(OpacityProperty, fadeOut);
    }

    internal void AttachSoundSwitch(Func<bool> soundsOn, Action<bool> setSounds)
    {
        _soundsOn = soundsOn;
        _setSounds = setSounds;
        _claudeKey = "";
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

    private static Border Card() => new()
    {
        Background = CardBg,
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(14, 11, 14, 11)
    };

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
            SelectionBrush = _mood,
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
        send.Background = _mood;
        send.BorderBrush = _mood;
        ((TextBlock)send.Child).Foreground = InkOn(_mood);
        send.MouseEnter += (_, _) => WeatherMotion.Glide(send, OpacityProperty, 0.85);
        send.MouseLeave += (_, _) => WeatherMotion.Glide(send, OpacityProperty, 1);

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
            Foreground = _moodInk,
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
        Squish(remove);
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

    private static void Shake(FrameworkElement element)
    {
        if (AnimationConfig.ReduceMotion) return;
        // The Claude tab's cards move with a scale and a shift already; shake that shift.
        var shift = element.RenderTransform as TranslateTransform ?? LiftParts(element).Shift;
        Motion.To(shift, TranslateTransform.XProperty, -6, Motion.PressDown,
            done: () => Motion.To(shift, TranslateTransform.XProperty, 0, Motion.Press));
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
        Grid.SetColumnSpan(text, 2);
        grid.Children.Add(text);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (ChangePreview(request) is { } preview)
        {
            Grid.SetRow(preview, 1);
            Grid.SetColumnSpan(preview, 2);
            grid.Children.Add(preview);
        }

        // Big buttons under the request, the answer you most likely want first.
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 1) };
        string id = request.Id;
        var allow = TextButton(Loc.Get("control.claude.allow"), accent: true, () => Settle(id, () => _claude?.Decide(id, ClaudeDecision.Allow)));
        var always = TextButton(Loc.Get("control.claude.always"), accent: false, () => Settle(id, () => _claude?.Decide(id, ClaudeDecision.Always)));
        if (request.AlwaysLabel is { Length: > 0 } rule) always.ToolTip = rule;
        var deny = TextButton(Loc.Get("control.claude.deny"), accent: false, () => Settle(id, () => _claude?.Decide(id, ClaudeDecision.Deny)));
        foreach (var button in new[] { allow, always, deny })
        {
            button.Height = 30;
            button.Margin = new Thickness(0, 0, 6, 0);
            buttons.Children.Add(button);
        }
        allow.Padding = new Thickness(18, 0, 18, 0);
        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 2);
        grid.Children.Add(buttons);

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
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 4, 0, 10)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        string id = request.Id;
        var keepPlanning = TextButton(Loc.Get("control.claude.keepPlanning"), accent: false, () => Settle(id, () => _claude?.Decide(id, ClaudeDecision.Deny)));
        buttons.Children.Add(keepPlanning);
        // Claude Code offers "auto-accept edits" as the mode to switch to; the Always answer applies it.
        if (request.AlwaysLabel?.Contains("acceptEdits", StringComparison.OrdinalIgnoreCase) == true)
            buttons.Children.Add(TextButton(Loc.Get("control.claude.autoEdits"), accent: false, () => Settle(id, () => _claude?.Decide(id, ClaudeDecision.Always))));
        var approve = TextButton(Loc.Get("control.claude.approvePlan"), accent: true, () => Settle(id, () => _claude?.Decide(id, ClaudeDecision.Allow)));
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
        Settle(request.Id, () => _claude?.Answer(request.Id, answers));
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

    /// <summary>
    /// A running clock just ticks: the swap animation other values use would blank it out every second.
    /// The digits are tabular, so the width holds still.
    /// </summary>
    private void UpdateElapsedLabels()
    {
        DateTime now = DateTime.UtcNow;
        foreach (var (label, started) in _elapsedLabels) label.Text = FormatElapsed(now - started);
        UpdateAccountElapsed();
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
        bool builtExpanded = expanded;
        var grid = NewRowGrid();
        grid.Margin = new Thickness(0, topMargin, 0, 6);

        var titlePanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var rotate = new RotateTransform(expanded ? 0 : -90);
        titlePanel.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M0,0 L8,0 L4,5 Z"),
            // On the system, home and school tabs the arrows wear the tab's colour too.
            Fill = Page != ControlCenterPage.Claude ? _moodInk : HeaderText,
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
        Squish(titlePanel);
        titlePanel.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            bool open = !IsExpanded(section);
            _expanded[section] = open;
            if (grid.Parent is not Panel panel) return;
            double currentHeight = panel.ActualHeight;
            // A section initially built closed has no rows yet. Build once; reversals retain them.
            if (open && !builtExpanded)
            {
                RebuildSection(section);
                builtExpanded = true;
                if (panel.Children.Count > 0 && panel.Children[0] != grid)
                {
                    panel.Children.RemoveAt(0);
                    panel.Children.Insert(0, grid);
                }
                panel.UpdateLayout();
            }
            foreach (UIElement child in grid.Children)
                if (Grid.GetColumn(child) == 2)
                {
                    double from = child.Visibility == Visibility.Visible ? child.Opacity : 0;
                    if (open) child.Visibility = Visibility.Visible;
                    var fade = WeatherMotion.To(from, open ? 1 : 0, 160, WeatherMotion.Out);
                    fade.Completed += (_, _) => { if (!IsExpanded(section)) child.Visibility = Visibility.Collapsed; };
                    if (AnimationConfig.ReduceMotion) child.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                    else child.BeginAnimation(OpacityProperty, fade);
                }
            foreach (UIElement child in panel.Children)
                if (child is FrameworkElement row && row != panel.Children[0])
                {
                    if (open) row.Visibility = Visibility.Visible;
                    else ExitElement(row);
                }
            double target = 0;
            for (int i = 0; i < panel.Children.Count; i++)
                if ((open || i == 0) && panel.Children[i] is FrameworkElement row)
                {
                    row.Measure(new Size(Math.Max(panel.ActualWidth, 1), double.PositiveInfinity));
                    target += row.DesiredSize.Height;
                }
            panel.ClipToBounds = true;
            Motion.To(rotate, RotateTransform.AngleProperty, open ? 0 : -90, Motion.Press);
            Motion.To(panel, HeightProperty, target, Motion.Glide,
                from: double.IsNaN(panel.Height) ? currentHeight : null,
                done: () =>
                {
                    if (IsExpanded(section) != open) return;
                    if (!open)
                        for (int i = 1; i < panel.Children.Count; i++) panel.Children[i].Visibility = Visibility.Collapsed;
                    Motion.Stop(panel, HeightProperty, double.NaN);
                    panel.ClipToBounds = false;
                });
            if (open && IsVisible) RevealChildren(panel, TimeSpan.Zero, 0, skip: 1);
        };
        Grid.SetColumn(titlePanel, 0);
        grid.Children.Add(titlePanel);

        var right = ColumnLabel(rightLabel);
        right.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (onRightLabel != null) MakeLink(right, onRightLabel);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        return grid;
    }

    private void MakeLink(TextBlock label, Action onClick)
    {
        label.Cursor = Cursors.Hand;
        Squish(label);
        var ink = new SolidColorBrush(((SolidColorBrush)label.Foreground).Color);
        label.Foreground = ink;
        label.MouseEnter += (_, _) => WeatherMotion.Tint(ink, ((SolidColorBrush)VNotch.Services.UiPalette.PrimaryBrush).Color);
        label.MouseLeave += (_, _) => WeatherMotion.Tint(ink, ((SolidColorBrush)(_usesGlass() ? VNotch.Services.UiPalette.PrimaryBrush : HeaderText)).Color);
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
            case "laptop": _laptopKey = ""; break;
            case "displays": BuildDisplaysSection(); return;
            case "pi.school":
            case "pi.exams":
            case "pi.todos": _piKey = ""; BuildPiPage(); return;
            case "home.lights":
            case "home.climate":
            case "home.plugs":
            case "home.scenes":
            case "home.pi": _homeKey = ""; BuildPiPage(); return;
            case "legion.mode":
            case "legion.keys": ToggleLegionSection(section); return;
            case "system": _statsTiles = null; break;
            case "apps": _appsKey = ""; break;
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

    private TextBlock AddStatus(Grid row, string text, Brush foreground, FrameworkElement? toggle = null)
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
        return label;
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

            Pressable(border, () => ComboBg, ComboHover);
            DetailHover(border, grid.Children[0] as FrameworkElement ?? caret, x: 1, y: -1);
            DetailHover(border, caret, y: 1.5);
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
        Pressable(button, () => accent ? tint : ComboBg, ComboHover, grow: true);
        button.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            onClick();
        };
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
        Pressable(button, () => ComboBg, ComboHover, grow: true);
        DetailHover(button, (FrameworkElement)button.Child);
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
        if (!IsVisible || AnimationConfig.ReduceMotion)
        {
            ControlOverlay.Children.Clear();
            ControlOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        foreach (UIElement child in new List<UIElement>(System.Linq.Enumerable.Cast<UIElement>(ControlOverlay.Children)))
            if (child is FrameworkElement surface)
            {
                surface.IsHitTestVisible = false;
                ExitElement(surface, () =>
                {
                    ControlOverlay.Children.Remove(surface);
                    if (ControlOverlay.Children.Count == 0) ControlOverlay.Visibility = Visibility.Collapsed;
                });
            }
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
                var rest = item.Background;
                Pressable(item, () => rest, ComboHover, grow: true);
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
        Motion.To(grow, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, from: Motion.TileFromScale);
        Motion.To(grow, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, from: Motion.TileFromScale);
        Motion.To(slide, TranslateTransform.YProperty, 0, Motion.Tile, from: -6);
        for (int i = 0; i < list.Children.Count; i++)
            WeatherMotion.Rise(list.Children[i], Motion.Stagger(i).TotalMilliseconds, dy: -6, ms: 320);
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

    /// <summary>Rows fall in one after another, like the mixer's when its tab opens.</summary>
    public void RevealRows(TimeSpan baseDelay)
    {
        if (_revealRequestFirst) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ScrollToRequest);
        else ControlScrollViewer.ScrollToTop();
        if (Page == ControlCenterPage.Claude)
        {
            // The account face stays still when the tab opens.
            return;
        }
        if (Page is ControlCenterPage.Pi or ControlCenterPage.School or ControlCenterPage.Roblox or ControlCenterPage.System) return;
        if (AnimationConfig.ReduceMotion) return;
        int index = 0;
        foreach (UIElement child in ControlRoot.Children)
            if (child is Panel section && section.Visibility == Visibility.Visible)
                index = RevealChildren(section, baseDelay, index);
    }

    private static void RevealElement(FrameworkElement row, TimeSpan delay)
    {
        // Placeholders own their motion and keep their dimmed offline opacity.
        if (row is SkeletonPlaceholder) return;
        PopIn(row, delay.TotalMilliseconds);
    }

    #endregion
}
