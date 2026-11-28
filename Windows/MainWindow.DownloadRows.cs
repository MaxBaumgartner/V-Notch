using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using VNotch.Controls;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch;

public partial class MainWindow
{
    private const double DownloadRowHeight = 34;
    private const int VisibleDownloadRows = 4;
    private readonly List<(string Key, TextBlock Title, TextBlock Subtitle, TextBlock Trailing)> _downloadRows = new();
    private readonly object _downloadPreviewOwner = new();
    private string? _downloadPreviewPath;

    private double BubbleDetailHeight() => BubbleDownloadList.Visibility == Visibility.Visible
        ? Math.Max(BubbleOpenHeight, BubbleDownloadList.MaxHeight + 10)
        : BubbleOpenHeight;

    private void UpdateDownloadBubbleRows()
    {
        double oldHeight = BubbleDetailHeight();
        if (!_downloads.Any(d => d.IsFinished))
        {
            ClearDownloadBubbleRows();
        }
        else
        {
            var downloads = _downloads.OrderByDescending(d => d.IsFinished).ThenBy(d => d.Key, StringComparer.OrdinalIgnoreCase).ToList();
            if (!downloads.Select(d => d.Key).SequenceEqual(_downloadRows.Select(r => r.Key)))
            {
                BubbleDownloadRows.Children.Clear();
                _downloadRows.Clear();
                foreach (var download in downloads) BuildDownloadRow(download);
            }
            for (int i = 0; i < downloads.Count; i++)
            {
                var (title, subtitle, trailing) = DownloadWords(downloads[i]);
                var row = _downloadRows[i];
                row.Title.Text = title;
                row.Subtitle.Text = subtitle;
                row.Trailing.Text = trailing;
                row.Trailing.Visibility = trailing.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            BubbleSummaryWords.Visibility = BubbleTrailing.Visibility = Visibility.Collapsed;
            BubbleDownloadList.MaxHeight = Math.Min(downloads.Count, VisibleDownloadRows) * DownloadRowHeight;
            BubbleDownloadList.Visibility = Visibility.Visible;
            BubbleDetail.IsHitTestVisible = true;
        }

        double height = BubbleDetailHeight();
        if (height != oldHeight && BubbleDetail.IsVisible)
            Motion.To(ActivityBubble, HeightProperty, height, Motion.Glide);
    }

    private void ClearDownloadBubbleRows()
    {
        BubbleDownloadList.Visibility = Visibility.Collapsed;
        BubbleDownloadRows.Children.Clear();
        _downloadRows.Clear();
        BubbleSummaryWords.Visibility = BubbleTrailing.Visibility = Visibility.Visible;
        BubbleDetail.IsHitTestVisible = false;
    }

    private void BuildDownloadRow(DownloadInfo download)
    {
        var font = (FontFamily)FindResource("MainSystemFont");
        var title = new TextBlock { Foreground = UiPalette.PrimaryBrush, FontFamily = font, FontSize = 12,
            FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var subtitle = new TextBlock { Foreground = BubbleMuted, FontFamily = font, FontSize = 10.5,
            TextTrimming = TextTrimming.CharacterEllipsis };
        var trailing = new TextBlock { Foreground = UiPalette.PrimaryBrush, FontFamily = font, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        var words = new StackPanel { Children = { title, subtitle } };
        DockPanel.SetDock(trailing, Dock.Right);
        var body = new Button
        {
            Content = new DockPanel { Children = { trailing, words } },
            Style = (Style)BubbleDownloadList.FindResource("DownloadRowButton")
        };
        AutomationProperties.SetName(body, download.Name);
        body.Click += (_, _) =>
        {
            CloseBubble();
            OpenDownloadSource(download);
        };
        var row = new Grid { Height = DownloadRowHeight, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(body);
        if (download.IsFinished && download.FilePath is { } path)
        {
            var eye = new QuickLookEye { Host = row, PreviewOwner = _downloadPreviewOwner, PreviewKey = path,
                VerticalAlignment = VerticalAlignment.Center };
            eye.Click += (_, e) =>
            {
                e.Handled = true;
                _downloadPreviewPath = path;
                QuickLookBridge.Shared.ToggleAsync(_downloadPreviewOwner, path, () =>
                    Task.FromResult<QuickLookBridge.PreviewFile?>(new(path, CanOpen: () => _downloadPreviewPath == path)))
                    .SafeFireAndForget("QUICKLOOK");
            };
            Grid.SetColumn(eye, 1);
            row.Children.Add(eye);
        }
        BubbleDownloadRows.Children.Add(row);
        _downloadRows.Add((download.Key, title, subtitle, trailing));
    }

    private void CloseDownloadPreview()
    {
        if (_downloadPreviewPath == null) return;
        _downloadPreviewPath = null;
        _ = QuickLookBridge.Shared.CloseAsync(_downloadPreviewOwner);
    }
}
