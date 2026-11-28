using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
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
    private bool HasRunningDownloads => _downloads.Any(d => !d.IsFinished);

    private void InitializeDownloads()
    {
        _downloadTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = DownloadPollIdle };
        _downloadTimer.Tick += async (_, _) => await PollDownloadsAsync();
        BubbleDownloadArrow.IsVisibleChanged += (_, _) =>
        {
            if (!BubbleDownloadArrow.IsVisible) Motion.Stop(BubbleDownloadArrowShift, TranslateTransform.YProperty, 0);
        };
        _downloadTimer.Start();
    }

    private void DisposeDownloads()
    {
        _downloadTimer?.Stop();
        CloseDownloadPreview();
    }

    private async Task PollDownloadsAsync()
    {
        if (_downloadPolling || _cleanedUp || _downloadTimer == null) return;
        _downloadPolling = true;
        try
        {
            var found = await Task.Run(_downloadWatcher.Poll);
            if (_cleanedUp) return;
            // The fastest (or furthest along) first: it speaks for the others.
            found = found.OrderBy(d => d.IsFinished).ThenByDescending(d => d.Percent.HasValue).ThenByDescending(d => d.BytesPerSecond).ToList();
            bool had = _downloads.Count > 0, has = found.Count > 0;
            bool wasRunning = HasRunningDownloads;
            _downloads = found;
            if (_downloadPreviewPath is { } path && !found.Any(d => d.IsFinished && d.FilePath == path)) CloseDownloadPreview();
            _downloadTimer.Interval = has ? DownloadPollBusy : DownloadPollIdle;
            if (had != has)
            {
                ArrangeLiveActivities(_currentMediaInfo, applyCompact: true);
                return;
            }
            if (wasRunning != HasRunningDownloads) UpdateKeepAwake();
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
    internal static (string Title, string Subtitle, string Trailing) DownloadWords(IReadOnlyList<DownloadInfo> downloads) =>
        DownloadWords(downloads[0], downloads.Count - 1);

    private static (string Title, string Subtitle, string Trailing) DownloadWords(DownloadInfo lead, int extra = 0)
    {
        if (lead.IsFinished) return (lead.Name, "Download complete", "");
        var subtitle = lead.Source;
        if (lead.BytesPerSecond > 0) subtitle += " · " + ControlCenterView.FormatRate(lead.BytesPerSecond);
        if (extra > 0) subtitle += $" · +{extra}";
        string trailing = lead.Percent is { } percent ? $"{Math.Floor(percent):0} %" : "";
        string title = lead.Name == lead.Source ? Loc.Get("downloads.title") : lead.Name;
        return (title, subtitle, trailing);
    }

    /// <summary>The ring shows how far it is; without a percentage it stays a calm, half-lit full ring.</summary>
    private void ShowDownloadProgress(DownloadInfo download)
    {
        double? percent = download.Percent;
        BubbleDownloadArc.Data = TimerArcGeometry(percent is { } p ? p / 100 : 1);
        BubbleDownloadArc.Opacity = percent.HasValue ? 1 : 0.45;
        BubbleDownloadArrow.Visibility = download.IsFinished ? Visibility.Collapsed : Visibility.Visible;
        BubbleDownloadCheck.Visibility = download.IsFinished ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NudgeDownloadArrow()
    {
        if (AnimationConfig.ReduceMotion || !BubbleDownloadArrow.IsVisible) return;
        Motion.To(BubbleDownloadArrowShift, TranslateTransform.YProperty, 7, Motion.PressDown, done: () =>
        {
            if (BubbleDownloadArrow.IsVisible)
                Motion.To(BubbleDownloadArrowShift, TranslateTransform.YProperty, 0, Motion.Press);
        });
    }

    /// <summary>Steam's downloads page, or the folder a browser download lands in; fixed targets, never one from a web page.</summary>
    private static void OpenDownloadSource(DownloadInfo download)
    {
        try
        {
            if (download.Source == "Steam")
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("steam://open/downloads") { UseShellExecute = true });
            else if (download.FilePath is { } path && Path.GetDirectoryName(path) is { } folder)
                System.Diagnostics.Process.Start("explorer.exe", download.IsFinished && File.Exists(path) ? $"/select,\"{path}\"" : $"\"{folder}\"");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            RuntimeLog.Warn("DOWNLOADS", $"Could not open {download.Source}: {ex.Message}");
        }
    }

    #endregion
}
