using System.Windows.Media;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch;

/// <summary>
/// A fork's build tells its owner when the repository it follows has new commits: "David · 3 new commits" as a pill in
/// the closed notch, once per new upstream head, a tap opens the list on GitHub. Asked a few minutes after the start,
/// then hourly; never in local-only mode or with update checks off. Merging stays a job for the owner.
/// </summary>
public partial class MainWindow
{
    private static readonly TimeSpan UpstreamFirstCheck = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan UpstreamInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan UpstreamRetry = TimeSpan.FromMinutes(5);
    private static readonly ImageSource UpstreamPillIcon = CreateUpstreamPillIcon();
    private UpstreamWatch? _upstream;
    private DispatcherTimer? _upstreamTimer;
    private string? _upstreamShownHead;
    private bool _upstreamChecking;

    private void InitializeUpstreamWatch()
    {
        if (UpstreamWatch.FromAssembly(typeof(MainWindow).Assembly) is not { } source) return;
        _upstream = new UpstreamWatch(source);
        _upstreamTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = UpstreamFirstCheck };
        _upstreamTimer.Tick += async (_, _) => await CheckUpstreamAsync();
        // No log line here: the window is built before the log opens, so it would be lost. Each check logs instead.
        _upstreamTimer.Start();
    }

    private void DisposeUpstreamWatch()
    {
        _upstreamTimer?.Stop();
        _upstream?.Dispose();
        _upstream = null;
    }

    private async Task CheckUpstreamAsync()
    {
        if (_upstreamTimer == null || _upstream == null || _upstreamChecking) return;
        _upstreamTimer.Interval = UpstreamInterval;
        if (_cleanedUp || _settings == null || _settings.EnableLocalOnlyMode || !_settings.AutoCheckUpdates) return;
        _upstreamChecking = true;
        try
        {
            var source = _upstream.Source;
            var ahead = await _upstream.CheckAsync();
            if (ahead != null)
                RuntimeLog.Log("UPSTREAM", $"{source.Repository}@{source.Branch} is {ahead.Count} commits past {source.Commit[..Math.Min(8, source.Commit.Length)]}");
            if (_cleanedUp || ahead is not { Count: > 0 } || ahead.Head == _upstreamShownHead) return;
            string status = Loc.Get(ahead.Count == 1 ? "upstream.newCommit" : "upstream.newCommits", ahead.Count);
            string url = ahead.CompareUrl;
            bool shown = ShowNotchPill(_upstream.Source.Name, status, UpstreamPillIcon, isClaude: false,
                tap: () => url.Length > 0 && SafeLauncher.TryOpenUrl(url), duration: TimeSpan.FromSeconds(8));
            if (shown)
            {
                _upstreamShownHead = ahead.Head;
                RuntimeLog.Log("UPSTREAM", $"{ahead.Count} new upstream commits shown");
            }
            // The notch was busy (a game, another pill): try again soon instead of in an hour.
            else _upstreamTimer.Interval = UpstreamRetry;
        }
        finally { _upstreamChecking = false; }
    }

    /// <summary>A commit on a line, like a git graph's.</summary>
    private static ImageSource CreateUpstreamPillIcon()
    {
        var pen = new Pen(Brushes.White, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        var drawing = new DrawingImage(new GeometryDrawing(null, pen, Geometry.Parse("M1,12 H7.5 M16.5,12 H23 M12,7.5 A4.5,4.5 0 1 1 11.99,7.5 Z")));
        drawing.Freeze();
        return drawing;
    }
}
