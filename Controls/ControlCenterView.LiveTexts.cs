using System;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VNotch.Controls;

/// <summary>
/// Texts that say how long until something ("back in 2 h 13 min", "in 3 days"): the data behind them is only read
/// every few minutes, but the text counts on by itself in between, while the tab is on screen.
/// </summary>
public partial class ControlCenterView
{
    private static readonly TimeSpan LiveTextInterval = TimeSpan.FromSeconds(10);
    private readonly List<(TextBlock Text, Func<string?> Make)> _liveTexts = new();
    private DispatcherTimer? _liveTextTimer;

    /// <summary>Sets <paramref name="text"/> from <paramref name="make"/> now and again on every tick; null keeps the text.</summary>
    private TextBlock LiveText(TextBlock text, Func<string?> make)
    {
        if (make() is { } now) text.Text = now;
        _liveTexts.Add((text, make));
        if (IsVisible) StartLiveTexts();
        return text;
    }

    private void StartLiveTexts()
    {
        if (_liveTexts.Count == 0 || _disposed) return;
        if (_liveTextTimer == null)
        {
            _liveTextTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = LiveTextInterval };
            _liveTextTimer.Tick += (_, _) => TickLiveTexts();
        }
        TickLiveTexts();
        _liveTextTimer.Start();
    }

    private void StopLiveTexts() => _liveTextTimer?.Stop();

    private void TickLiveTexts()
    {
        for (int i = _liveTexts.Count - 1; i >= 0; i--)
        {
            var (text, make) = _liveTexts[i];
            // Rebuilt sections drop their old texts.
            if (!text.IsDescendantOf(this))
            {
                _liveTexts.RemoveAt(i);
                continue;
            }
            if (text.IsVisible && make() is { } now) SetDetailText(text, now);
        }
        if (_liveTexts.Count == 0) _liveTextTimer?.Stop();
    }
}
