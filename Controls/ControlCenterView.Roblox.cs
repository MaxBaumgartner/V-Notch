using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

/// <summary>
/// The Roblox tab: every public game of the creator added up, as the Pi's own Roblox tab shows them. The face holds
/// who plays now with the games' icons and the last 24 hours, then visits, the record, playtime and the rating; the
/// drawer the rest at a glance and today's daily levels. Green like the Pi's page, a little brighter while somebody
/// plays. Only counts: the players' names stay on the Pi.
/// </summary>
public partial class ControlCenterView
{
    private static readonly TimeSpan RobloxRefreshInterval = TimeSpan.FromSeconds(30);
    private static readonly Color RobloxGreen = Color.FromRgb(63, 185, 107);
    private static readonly Brush RobloxInk = Frozen("#3FB96B");
    private static readonly Brush RobloxArea = Frozen("#383FB96B");
    private static readonly Brush RobloxQuiet = Frozen("#59FFFFFF");
    private const int RobloxIconsShown = 7;

    private PiDashboardClient? _robloxClient;
    private DispatcherTimer? _robloxTimer;
    private bool _robloxRefreshing, _robloxReachable = true;
    private RobloxBoard? _roblox;
    private readonly StackPanel _robloxSection = new();
    private Grid _robloxFace = null!;
    private TextBlock _robloxPlaying = null!, _robloxPlayingLabel = null!, _robloxSub = null!, _robloxUnavailable = null!,
        _robloxDayLabel = null!, _robloxDayPeak = null!;
    private Ellipse _robloxLive = null!;
    private StackPanel _robloxIcons = null!;
    private Canvas _robloxChart = null!;
    private Polyline _robloxLine = null!;
    private Polygon _robloxArea = null!;
    private readonly (TextBlock Label, TextBlock Value, TextBlock Sub)[] _robloxKpis = new (TextBlock, TextBlock, TextBlock)[4];
    private readonly Dictionary<string, ImageSource?> _robloxIconCache = new(StringComparer.Ordinal);
    private string _robloxIconKey = "", _robloxDrawerKey = "";

    private bool RobloxAvailable => !_isLocalOnly() && _robloxClient?.IsConfigured == true && _roblox != null;

    internal void AttachRoblox(PiDashboardClient client, Func<bool> isLocalOnly, Func<bool> usesGlass)
    {
        _robloxClient = client;
        _isLocalOnly = isLocalOnly;
        _usesGlass = usesGlass;
        MountCompactPage();
        _compactPage.Children.Add(_robloxSection);
        MountRobloxFace();
        _robloxTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = RobloxRefreshInterval };
        _robloxTimer.Tick += (_, _) => RefreshRoblox();
    }

    private void OnRobloxVisibleChanged(bool visible)
    {
        if (visible)
        {
            BuildRoblox();
            _robloxTimer?.Start();
        }
        else _robloxTimer?.Stop();
    }

    private async void RefreshRoblox()
    {
        if (_robloxClient == null || _robloxRefreshing || _disposed) return;
        if (_isLocalOnly() || !_robloxClient.IsConfigured)
        {
            BuildRoblox();
            return;
        }
        _robloxRefreshing = true;
        try
        {
            var board = await _robloxClient.GetRobloxAsync();
            if (_disposed) return;
            _robloxReachable = board != null;
            if (board != null) _roblox = board;
            BuildRoblox();
            await LoadRobloxIconsAsync();
        }
        catch (Exception ex)
        {
            // An async void: anything that escapes here would end the whole app, not just this refresh.
            RuntimeLog.Error("ROBLOX", ex, "Roblox refresh failed");
        }
        finally
        {
            _robloxRefreshing = false;
        }
    }

    private void MountRobloxFace()
    {
        _robloxLive = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        _robloxPlayingLabel = SchoolText("", 12);
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Children = { _robloxLive, _robloxPlayingLabel } };
        _robloxPlaying = SchoolText("", 52, CompactInk, FontWeights.DemiBold);
        _robloxPlaying.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        _robloxPlaying.LineHeight = 57;
        Typography.SetNumeralAlignment(_robloxPlaying, FontNumeralAlignment.Tabular);
        _robloxSub = SchoolText("", 14);
        _robloxIcons = new StackPanel { Orientation = Orientation.Horizontal, Height = 24, Margin = new Thickness(0, 9, 0, 0) };
        var copy = new StackPanel { Margin = new Thickness(0, 2, 14, 0), Children = { heading, _robloxPlaying, _robloxSub, _robloxIcons } };

        _robloxArea = new Polygon { Fill = RobloxArea };
        _robloxLine = new Polyline { Stroke = RobloxInk, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
        _robloxChart = new Canvas { Height = 58, ClipToBounds = true, Children = { _robloxArea, _robloxLine } };
        _robloxChart.SizeChanged += (_, _) => DrawRobloxChart();
        _robloxDayLabel = SchoolText("", 12);
        _robloxDayPeak = SchoolText("", 12);
        var chartCard = new Border
        {
            Width = 206, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0), Background = SchoolTile,
            BorderBrush = SchoolRim, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 8, 12, 10),
            Child = new StackPanel { Children = { SchoolColumns(_robloxDayLabel, _robloxDayPeak), new Border { Height = 8 }, _robloxChart } }
        };
        var hero = SchoolColumns(copy, chartCard);

        var kpis = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        for (int i = 0; i < 7; i++)
            kpis.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(8) });
        for (int i = 0; i < _robloxKpis.Length; i++)
        {
            var label = SchoolText("", 12);
            var value = SchoolText("", 20, CompactInk, FontWeights.SemiBold);
            Typography.SetNumeralAlignment(value, FontNumeralAlignment.Tabular);
            value.Margin = new Thickness(0, 2, 0, 0);
            var sub = SchoolText("", 12);
            foreach (var text in new[] { label, value, sub }) text.TextTrimming = TextTrimming.CharacterEllipsis;
            _robloxKpis[i] = (label, value, sub);
            var tile = new Border
            {
                Background = SchoolTile, BorderBrush = SchoolRim, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 9), Child = new StackPanel { Children = { label, value, sub } }
            };
            Grid.SetColumn(tile, i * 2);
            kpis.Children.Add(tile);
        }

        _robloxFace = new Grid { Margin = new Thickness(4, 0, 4, 3), Children = { new StackPanel { Children = { hero, kpis } } } };
        _robloxUnavailable = SchoolText("", 16);
        _robloxUnavailable.Margin = new Thickness(4, 6, 4, 5);
        _robloxSection.Children.Add(_robloxFace);
        _robloxSection.Children.Add(_robloxUnavailable);
        ControlScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        SetMoodColors(RobloxGreen, Lift(RobloxGreen, .2), Color.FromRgb(46, 139, 106));
    }

    private void BuildRoblox()
    {
        if (_disposed || _robloxClient == null) return;
        bool available = RobloxAvailable;
        _robloxFace.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        _robloxUnavailable.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        _robloxUnavailable.Text = _isLocalOnly() ? Loc.Get("pi.localOnly")
            : !_robloxClient.IsConfigured ? Loc.Get("pi.notConfigured")
            : _robloxReachable ? Loc.Get("roblox.loading") : Loc.Get("roblox.unavailable");
        if (!available)
        {
            SetCompactStatus("");
            if (_robloxDrawerKey.Length > 0) RemoveCompactDrawer();
            _robloxDrawerKey = "";
            ContentHeightChanged?.Invoke();
            return;
        }

        var board = _roblox!;
        bool playing = board.Playing > 0;
        _robloxLive.Fill = playing ? RobloxInk : RobloxQuiet;
        _robloxPlayingLabel.Text = Loc.Get("roblox.playingNow");
        _robloxPlaying.Text = RobloxBoard.Count(board.Playing);
        _robloxSub.Text = Loc.Get("roblox.inGames", board.PlayingGames, board.Games);
        _robloxDayLabel.Text = Loc.Get("roblox.day");
        _robloxDayPeak.Text = Loc.Get("roblox.peak", board.Last24Hours.Count > 0 ? board.Last24Hours.Max() : 0);
        DrawRobloxChart();
        PaintRobloxIcons();

        SetKpi(0, Loc.Get("roblox.visits"), RobloxBoard.Count(board.Visits), Loc.Get("roblox.today", RobloxBoard.Count(board.Today.Visits)));
        SetKpi(1, Loc.Get("roblox.record"), RobloxBoard.Count(board.Record), board.RecordAt is { } at ? RobloxBoard.ShortDate(at) : "");
        SetKpi(2, Loc.Get("roblox.playtime"), RobloxBoard.Playtime(board.Minutes), Loc.Get("roblox.today", RobloxBoard.Playtime(board.Today.Minutes)));
        SetKpi(3, Loc.Get("roblox.rating"), board.Rating is { } rating ? $"{rating} %" : "–", Loc.Get("roblox.favorites", board.Favorites));
        SetCompactStatus(Loc.Get("roblox.summary", board.Games, RobloxBoard.Count(board.Visits)));
        if (_backdrop != null) _backdrop.Intensity = playing ? .4 : .25;

        string key = string.Join("|", Loc.CurrentLanguage, board.Up, board.Down, board.Favorites, board.Today.Peak, board.DifferentPlayers,
            board.Robux, board.Profile.Length > 0, board.Daily is { } d ? $"{d.Teams}/{d.Players}/{string.Join(",", d.Games.Select(g => $"{g.Game}={g.Teams}"))}" : "");
        if (key != _robloxDrawerKey)
        {
            _robloxDrawerKey = key;
            AddDrawer(ComposeRobloxDrawer(board));
        }
        ContentHeightChanged?.Invoke();

        void SetKpi(int index, string label, string value, string sub)
        {
            var (labelText, valueText, subText) = _robloxKpis[index];
            labelText.Text = label;
            valueText.Text = value;
            subText.Text = sub;
            labelText.ToolTip = $"{label}: {value}";
        }
    }

    private StackPanel ComposeRobloxDrawer(RobloxBoard board)
    {
        var body = new StackPanel { Margin = new Thickness(4, 0, 4, 0) };
        body.Children.Add(Heading(Loc.Get("roblox.glance")));
        if (board.Rating is { } rating)
            body.Children.Add(Row(Loc.Get("roblox.rating"), $"{rating} %  ·  {Loc.Get("roblox.votes", board.Up, board.Down)}"));
        body.Children.Add(Row(Loc.Get("roblox.favoritesLabel"), RobloxBoard.Count(board.Favorites)));
        body.Children.Add(Row(Loc.Get("roblox.peakToday"), RobloxBoard.Count(board.Today.Peak)));
        if (board.DifferentPlayers is { } players) body.Children.Add(Row(Loc.Get("roblox.players"), RobloxBoard.Count(players)));
        if (board.Robux is { } robux) body.Children.Add(Row(Loc.Get("roblox.robux"), RobloxBoard.Count(robux)));
        if (board.Daily is { } daily)
        {
            var title = Heading(Loc.Get("roblox.daily"));
            title.Margin = new Thickness(0, 12, 0, 4);
            body.Children.Add(title);
            body.Children.Add(Row(Loc.Get("roblox.teams", daily.Teams, daily.Players), ""));
            foreach (var (game, teams) in daily.Games) body.Children.Add(Row(game, RobloxBoard.Count(teams)));
        }
        if (board.Profile.Length > 0)
        {
            string profile = board.Profile, label = Loc.Get("roblox.open");
            var icon = new TextBlock
            {
                Text = "", FontFamily = FluentFont, FontSize = 14, Foreground = CompactInk,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0)
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Children = { icon, SchoolText(label, 13, CompactInk) } };
            var open = SchoolPaintButton(new Border
            {
                Height = 30, CornerRadius = new CornerRadius(9), Padding = new Thickness(10, 0, 12, 0), Background = SchoolTile,
                BorderBrush = SchoolRim, BorderThickness = new Thickness(1), Child = content
            }, label, () => SafeLauncher.TryOpenUrl(profile), Frozen("#262628"));
            open.HorizontalAlignment = HorizontalAlignment.Left;
            open.Margin = new Thickness(0, 12, 0, 4);
            body.Children.Add(open);
        }
        return body;

        TextBlock Heading(string text)
        {
            var heading = SchoolText(text, 16, CompactInk, FontWeights.SemiBold);
            heading.Margin = new Thickness(0, 0, 0, 4);
            return heading;
        }

        Grid Row(string label, string value)
        {
            var name = SchoolText(label, 13, CompactInk);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            var amount = SchoolText(value, 13);
            Typography.SetNumeralAlignment(amount, FontNumeralAlignment.Tabular);
            amount.Margin = new Thickness(12, 0, 0, 0);
            var row = SchoolColumns(name, amount);
            row.Height = 26;
            return row;
        }
    }

    /// <summary>The players of the last 24 hours, the most at once in each ten minutes, as a line over a soft area.</summary>
    private void DrawRobloxChart()
    {
        var points = _roblox?.Last24Hours ?? [];
        double width = _robloxChart.ActualWidth, height = _robloxChart.ActualHeight > 0 ? _robloxChart.ActualHeight : _robloxChart.Height;
        if (points.Count < 2 || width <= 0)
        {
            _robloxLine.Points = [];
            _robloxArea.Points = [];
            return;
        }
        int top = Math.Max(1, points.Max());
        var line = new PointCollection(points.Count);
        for (int i = 0; i < points.Count; i++)
            line.Add(new Point(i * width / (points.Count - 1), height - 2 - (height - 6) * Math.Max(0, points[i]) / top));
        var area = new PointCollection(line) { new Point(width, height), new Point(0, height) };
        line.Freeze();
        area.Freeze();
        _robloxLine.Points = line;
        _robloxArea.Points = area;
    }

    /// <summary>The first games' icons overlapping like a stack, "+10" for the rest; a grey tile until one has loaded.</summary>
    private void PaintRobloxIcons()
    {
        if (_roblox is not { } board) return;
        var shown = board.Icons.Take(RobloxIconsShown).ToList();
        string key = string.Join("|", shown.Select(p => _robloxIconCache.TryGetValue(p, out var image) && image != null ? p : "")) + $"|{board.Games}";
        if (key == _robloxIconKey) return;
        _robloxIconKey = key;
        _robloxIcons.Children.Clear();
        for (int i = 0; i < shown.Count; i++)
        {
            var image = _robloxIconCache.GetValueOrDefault(shown[i]);
            _robloxIcons.Children.Add(new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(7), Margin = new Thickness(i == 0 ? 0 : -7, 0, 0, 0),
                BorderBrush = Frozen("#E6101012"), BorderThickness = new Thickness(1.5),
                Background = image != null ? new ImageBrush(image) { Stretch = Stretch.UniformToFill } : SchoolTile
            });
        }
        int more = Math.Max(board.Games, board.Icons.Count) - shown.Count;
        if (more > 0)
        {
            var rest = SchoolText($"+{more}", 12);
            rest.VerticalAlignment = VerticalAlignment.Center;
            rest.Margin = new Thickness(7, 0, 0, 0);
            _robloxIcons.Children.Add(rest);
        }
    }

    private async Task LoadRobloxIconsAsync()
    {
        if (_robloxClient == null || _roblox is not { } board) return;
        bool loaded = false;
        foreach (string path in board.Icons.Take(RobloxIconsShown))
        {
            if (_robloxIconCache.ContainsKey(path)) continue;
            byte[]? bytes = await _robloxClient.GetRobloxIconAsync(path);
            if (_disposed) return;
            _robloxIconCache[path] = bytes == null ? null : DecodeRobloxIcon(bytes);
            loaded = true;
        }
        if (loaded) PaintRobloxIcons();
    }

    private static ImageSource? DecodeRobloxIcon(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 48;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }
}
