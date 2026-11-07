using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch;

/// <summary>
/// Downloads as a live activity in the orb beside the closed notch: Steam and Riot updates with their percentage,
/// browser downloads and the other launchers with their speed (see <see cref="DownloadWatcher"/>). The ring fills,
/// the arrow nudges with every update; a tap shows name, source, speed and progress, a second tap opens where
/// the download lives (Steam's downloads page, the Downloads folder).
/// </summary>
public partial class MainWindow
{
    #region Downloads

    private static readonly TimeSpan DownloadPollBusy = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DownloadPollIdle = TimeSpan.FromSeconds(6);

    private readonly DownloadWatcher _downloadWatcher = new();
    private List<DownloadInfo> _downloads = new();
    private DispatcherTimer? _downloadTimer;
    private bool _downloadPolling;

    private void InitializeDownloads()
    {
        _downloadTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = DownloadPollIdle };
        _downloadTimer.Tick += async (_, _) => await PollDownloadsAsync();
        _downloadTimer.Start();
    }

    private void DisposeDownloads() => _downloadTimer?.Stop();

    private async Task PollDownloadsAsync()
    {
        if (_downloadPolling || _cleanedUp || _downloadTimer == null) return;
        _downloadPolling = true;
        try
        {
            var found = await Task.Run(_downloadWatcher.Poll);
            if (_cleanedUp) return;
            // The fastest (or furthest along) first: it speaks for the others.
            found = found.OrderByDescending(d => d.Percent.HasValue).ThenByDescending(d => d.BytesPerSecond).ToList();
            bool had = _downloads.Count > 0, has = found.Count > 0;
            _downloads = found;
            _downloadTimer.Interval = has ? DownloadPollBusy : DownloadPollIdle;
            if (had != has)
            {
                ArrangeLiveActivities(_currentMediaInfo, applyCompact: true);
                return;
            }
            if (has && _bubbleShown && _bubbleActivity == LiveActivity.Download)
            {
                UpdateBubbleContent();
                NudgeDownloadArrow();
            }
        }
        catch (Exception ex)
        {
            RuntimeLog.Warn("DOWNLOADS", $"Looking for downloads failed: {ex.Message}");
        }
        finally
        {
            _downloadPolling = false;
        }
    }

    /// <summary>Title, line below and the right-hand figure of the open orb.</summary>
    internal static (string Title, string Subtitle, string Trailing) DownloadWords(IReadOnlyList<DownloadInfo> downloads)
    {
        var lead = downloads[0];
        var subtitle = lead.Source;
        if (lead.BytesPerSecond > 0) subtitle += " · " + ControlCenterView.FormatRate(lead.BytesPerSecond);
        if (downloads.Count > 1) subtitle += $" · +{downloads.Count - 1}";
        string trailing = lead.Percent is { } percent ? $"{Math.Floor(percent):0} %" : "";
        string title = lead.Name == lead.Source ? Loc.Get("downloads.title") : lead.Name;
        return (title, subtitle, trailing);
    }

    /// <summary>The ring shows how far it is; without a percentage it stays a calm, half-lit full ring.</summary>
    private void ShowDownloadProgress(double? percent)
    {
        BubbleDownloadArc.Data = TimerArcGeometry(percent is { } p ? p / 100 : 1);
        BubbleDownloadArc.Opacity = percent.HasValue ? 1 : 0.45;
    }

    private void NudgeDownloadArrow()
    {
        if (AnimationConfig.ReduceMotion) return;
        var hop = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(520) };
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(160)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        hop.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(520)), new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut }));
        Fps(hop);
        BubbleDownloadArrowShift.BeginAnimation(TranslateTransform.YProperty, hop);
    }

    /// <summary>Steam's downloads page, or the folder a browser download lands in; fixed targets, never one from a web page.</summary>
    private static void OpenDownloadSource(DownloadInfo download)
    {
        try
        {
            if (download.Source == "Steam")
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("steam://open/downloads") { UseShellExecute = true });
            else if (download.Key.StartsWith("file:", StringComparison.Ordinal) && System.IO.Path.GetDirectoryName(download.Key["file:".Length..]) is { } folder)
                System.Diagnostics.Process.Start("explorer.exe", $"\"{folder}\"");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            RuntimeLog.Warn("DOWNLOADS", $"Could not open {download.Source}: {ex.Message}");
        }
    }

    #endregion
}
