using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Controllers;
using VNotch.Controls;
using VNotch.Models;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using VNotch.Services.ControlCenter;

namespace VNotch;

public partial class MainWindow
{
    #region Live activities: the notch and the orb beside it

    // Like the second island on an iPhone: as tall as the notch, flush with its top, a small gap apart.
    private double BubbleSize => Math.Max(26, _collapsedHeight);
    private double BubbleOpenHeight => BubbleSize + 10;
    private const double BubbleOpenWidth = 236;
    private const double BubbleGap = 10;
    private static readonly TimeSpan BubbleLinger = TimeSpan.FromMilliseconds(1400);
    private static readonly TimeSpan BubbleUntouched = TimeSpan.FromSeconds(5);
    private static readonly Brush BubbleMuted = FrozenBrush(0x9A, 0x9A, 0x9F);

    private LiveActivity _mainActivity;
    private LiveActivity _bubbleWanted;
    private LiveActivity _bubbleActivity;
    private bool _claudeHoldsNotch;
    private bool _bubbleShown;
    private bool _bubbleOpen;
    private bool _bubbleHooked;
    private int _bubbleToken;
    private DispatcherTimer? _bubbleCloseTimer;
    private bool _bubbleMusicSpinning;
    // The Claude sessions the orb stands for: all of them with music in the notch, the rest with Claude there.
    private List<ClaudeSession> _bubbleClaudeSessions = new();
    private bool _bubbleListMode;

    private bool IsCountdownLive => CountdownWidget?.Controller.IsRunning == true;

    /// <summary>Music may hold the notch unless Claude Code, waiting for you, has taken it.</summary>
    private bool WantsMusicCompact(MediaInfo? info) => MediaDisplayController.ShouldBeCompactMode(info) && !_claudeHoldsNotch;

    /// <summary>
    /// Ranks what is live (see <see cref="LiveActivities"/>): the first holds the notch, the second the orb.
    /// When Claude Code takes the notch from the music, or gives it back, the music's compact view follows now.
    /// </summary>
    private void ArrangeLiveActivities(MediaInfo? info, bool applyCompact)
    {
        if (_cleanedUp) return;
        bool music = MediaDisplayController.ShouldBeCompactMode(info);
        var live = _claudeLiveSessions;
        var (main, bubble) = LiveActivities.Arrange(music, _claudeLiveSession?.Status, IsCountdownLive, moreClaude: live.Count > 1,
            lesson: IsLessonLive, download: _downloads.Count > 0, charging: WantsChargingOrb, job: RunningScriptJob != null);
        // Plugging in or out (or a full battery) pushes in front of whatever has the orb, for a moment.
        if (_chargeMoment != ChargeMoment.None) bubble = LiveActivity.Charging;
        _mainActivity = main;
        _bubbleClaudeSessions = bubble != LiveActivity.Claude ? new()
            : main == LiveActivity.Claude ? live.Skip(1).ToList() : live.ToList();
        ClaudeLive.Visibility = main == LiveActivity.Claude ? Visibility.Visible : Visibility.Collapsed;

        bool holds = main == LiveActivity.Claude && music;
        if (holds != _claudeHoldsNotch)
        {
            _claudeHoldsNotch = holds;
            if (applyCompact && info != null && !IsCountdownCompletionVisualActive)
            {
                CancelPendingMusicCompactExit();
                ApplyMusicCompactMode(WantsMusicCompact(info), info);
            }
        }
        _bubbleWanted = bubble;
        // Claude, downloads and the timer all pass through here, so the keep-awake reason follows them.
        UpdateKeepAwake();
        UpdateActivityBubble();
        UpdateIdleShape();
    }

    /// <summary>The orb shows its activity while the notch rests closed with its own content on screen.</summary>
    private void UpdateActivityBubble()
    {
        HookActivityBubble();
        QueueScriptJobPill();
        bool surface = MusicCompactContent.IsVisible || CollapsedContent.IsVisible;
        LiveActivity want = !_cleanedUp && surface && !_isExpanded && !_isMusicExpanded && !_isAnimating
                && !_spotlightMorphOwnsNotchVisibility
            ? _bubbleWanted
            : LiveActivity.None;

        if (want == LiveActivity.None)
        {
            if (!_bubbleShown) return;
            _bubbleShown = false;
            RetractBubble();
            return;
        }
        if (!_bubbleShown)
        {
            _bubbleShown = true;
            if (ActivityBubble.IsVisible)
            {
                BudBubble();
                if (want != _bubbleActivity) SwapBubbleFace(want);
                else if (want == LiveActivity.Charging) ChargeFaceReady();
            }
            else
            {
                SetBubbleFace(want);
                BudBubble();
            }
            return;
        }
        if (want != _bubbleActivity) SwapBubbleFace(want);
        else UpdateBubbleContent();
    }

    private void HookActivityBubble()
    {
        if (_bubbleHooked) return;
        _bubbleHooked = true;
        ActivityBubble.IsVisibleChanged += (_, _) =>
        {
            ApplyBubbleMusicSpin();
            RefreshChargePulse();
            UpdateScriptJobDetailClock();
        };
        MusicCompactContent.IsVisibleChanged += (_, _) => UpdateActivityBubble();
        CollapsedContent.IsVisibleChanged += (_, _) => UpdateActivityBubble();
        NotchBorder.SizeChanged += (_, _) => PositionBubble();
        NotchContainer.SizeChanged += (_, _) => PositionBubble();
        if (CountdownWidget?.Controller is { } countdown)
        {
            void Rearrange() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => ArrangeLiveActivities(_currentMediaInfo, applyCompact: true));
            countdown.RunStarted += (_, _) => Rearrange();
            countdown.RunPaused += (_, _) => Rearrange();
            countdown.RunReset += (_, _) => Rearrange();
            countdown.Completed += (_, _) => Rearrange();
            countdown.Tick += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_bubbleShown && _bubbleActivity == LiveActivity.Countdown) UpdateBubbleContent();
            });
        }
    }

    /// <summary>The notch starts to open: the orb goes back in at once, before the notch grows over it.</summary>
    private void RetractBubbleForExpand()
    {
        if (!_bubbleShown) return;
        _bubbleShown = false;
        RetractBubble();
    }

    /// <summary>
    /// Spotlight takes over the notch and hides it at once. The orb and the charge cable sit outside the notch,
    /// so they leave at the same instant instead of sliding under a notch that is no longer there.
    /// </summary>
    private void HideBubbleForSpotlight()
    {
        // Also when it is already sliding back in on its own: that slide would still show for a moment.
        if (!_bubbleShown && ActivityBubble.Visibility != Visibility.Visible) return;
        _bubbleShown = false;
        RetractBubble(instant: true);
        ChargeCable.Visibility = Visibility.Collapsed;
    }

    /// <summary>Just right of the notch (and its ear), flush with its top.</summary>
    private void PositionBubble()
    {
        if (ActivityBubble.Visibility != Visibility.Visible) return;
        double ear = LeftEar.Visibility == Visibility.Visible ? LeftEar.Width - 1 : 0;
        double notchWidth = _isExpanded ? _collapsedWidth : NotchBorder.ActualWidth;
        BubbleAnchor.X = Math.Round(NotchContainer.ActualWidth / 2 + notchWidth / 2 + ear + BubbleGap);
        BubbleAnchor.Y = 0;
        if (_bubbleActivity == LiveActivity.Charging) PositionChargeCable();
    }

    private double BubbleOpenTargetWidth() =>
        Math.Max(BubbleSize, Math.Min(BubbleOpenWidth, NotchContainer.ActualWidth - BubbleAnchor.X - 6));

    private void ActivityBubble_SizeChanged(object sender, SizeChangedEventArgs e) =>
        // A perfect pill whatever size the spring has it at; the taller list keeps the pill's corners.
        ActivityBubble.CornerRadius = new CornerRadius(Math.Min(e.NewSize.Height / 2, BubbleOpenHeight / 2));

    #endregion

    #region Faces and words

    private double FaceSize(LiveActivity activity, bool open) => activity switch
    {
        LiveActivity.Music => open ? 30 : 24,
        LiveActivity.Countdown or LiveActivity.Lesson or LiveActivity.Download or LiveActivity.Job => open ? 26 : 21,
        // The charging ring fills the whole orb.
        LiveActivity.Charging => open ? BubbleOpenHeight : BubbleSize,
        _ => open ? 19 : 16
    };

    private Thickness FaceMargin(LiveActivity activity, bool open)
    {
        double height = open ? BubbleOpenHeight : BubbleSize;
        return new Thickness((height - FaceSize(activity, open)) / 2, 0, 0, 0);
    }

    private void SetBubbleFace(LiveActivity activity)
    {
        // This runs while hidden or between the outgoing and incoming face fades.
        if (_bubbleActivity == LiveActivity.Charging && activity != LiveActivity.Charging) LeaveChargeFace(fast: true);
        _bubbleActivity = activity;
        BubbleClaudeFace.Visibility = activity == LiveActivity.Claude ? Visibility.Visible : Visibility.Collapsed;
        BubbleMusicFace.Visibility = activity == LiveActivity.Music ? Visibility.Visible : Visibility.Collapsed;
        BubbleTimerFace.Visibility = activity == LiveActivity.Countdown ? Visibility.Visible : Visibility.Collapsed;
        BubbleLessonFace.Visibility = activity == LiveActivity.Lesson ? Visibility.Visible : Visibility.Collapsed;
        BubbleDownloadFace.Visibility = activity == LiveActivity.Download ? Visibility.Visible : Visibility.Collapsed;
        BubbleJobFace.Visibility = activity == LiveActivity.Job ? Visibility.Visible : Visibility.Collapsed;
        BubbleChargeFace.Visibility = activity == LiveActivity.Charging ? Visibility.Visible : Visibility.Collapsed;
        if (activity == LiveActivity.Charging)
        {
            PrepareChargeFace();
            ChargeAfter(Motion.Stagger(1), ChargeFaceReady);
        }
        Motion.Stop(BubbleIcon, WidthProperty);
        Motion.Stop(BubbleIcon, HeightProperty);
        double size = FaceSize(activity, _bubbleOpen);
        BubbleIcon.Width = BubbleIcon.Height = size;
        Motion.Stop(this, BubbleFaceInsetProperty, FaceMargin(activity, _bubbleOpen).Left);
        // A zero inset may already be the property's default, so its callback need not run.
        BubbleIcon.Margin = FaceMargin(activity, _bubbleOpen);
        UpdateBubbleContent();
        ApplyClaudeLiveMotion();
    }

    private void UpdateBubbleContent()
    {
        string title = "", subtitle = "", trailing = "";
        Brush subtitleBrush = BubbleMuted;
        switch (_bubbleActivity)
        {
            case LiveActivity.Claude when _bubbleClaudeSessions.Count > 0 && _bubbleClaudeSessions[0] is var session:
                title = session.Name;
                subtitle = session.Status == ClaudeSessionStatus.Working && session.Activity is { Length: > 0 } activity
                    ? activity
                    : Loc.Get(ClaudeLiveStatusKey(session));
                if (session.Status == ClaudeSessionStatus.Waiting) subtitleBrush = ClaudeLiveWaitingBrush;
                trailing = ControlCenterView.FormatElapsed(DateTime.UtcNow - (session.TurnStartedUtc ?? DateTime.UtcNow));
                // The orb's star takes the colour of the session it leads with (yellow: one waits for you).
                ClaudeLiveBadgeStar.Fill = ClaudeStarBrush(session);
                break;
            case LiveActivity.Music when _currentMediaInfo is { } media:
                title = media.CurrentTrack;
                subtitle = media.CurrentArtist;
                break;
            case LiveActivity.Countdown when CountdownWidget?.Controller is { } countdown:
                title = Loc.Get("nav.timer");
                trailing = FormatCountdown(countdown.Remaining);
                UpdateTimerArc(countdown.Duration.TotalSeconds > 0 ? countdown.Remaining.TotalSeconds / countdown.Duration.TotalSeconds : 0);
                break;
            case LiveActivity.Lesson when _lessonNow.Phase != LessonPhase.Off:
                (title, subtitle) = LessonWords(_lessonNow);
                trailing = Loc.Get("lesson.minutes", _lessonNow.MinutesLeft);
                BubbleLessonArc.Data = TimerArcGeometry(_lessonNow.Remaining);
                break;
            case LiveActivity.Download when _downloads.Count > 0:
                (title, subtitle, trailing) = DownloadWords(_downloads);
                ShowDownloadProgress(_downloads[0].Percent);
                break;
            case LiveActivity.Job when RunningScriptJob is { } job:
                (title, subtitle, trailing) = ScriptJobWords(job);
                break;
            case LiveActivity.Charging when _chargeBattery is { } battery:
                (title, subtitle, trailing) = ChargeWords(battery);
                break;
        }
        SetBubbleWords(BubbleTitle, title);
        SetBubbleWords(BubbleSubtitle, subtitle);
        BubbleSubtitle.Foreground = subtitleBrush;
        BubbleSubtitle.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BubbleTrailing.TextAlignment = _bubbleActivity == LiveActivity.Job ? TextAlignment.Right : TextAlignment.Left;
        SetBubbleWords(BubbleTrailing, trailing);
        UpdateScriptJobDetailClock();
        UpdateBubbleClaudeCount();
        if (_bubbleListMode) UpdateBubbleList(animate: false);
        ApplyBubbleMusicSpin();
    }

    private readonly Dictionary<TextBlock, string> _bubbleWords = new();

    private void SetBubbleWords(TextBlock text, string value)
    {
        if (_bubbleWords.TryGetValue(text, out var pending) && pending == value) return;
        _bubbleWords[text] = value;
        if (!text.IsVisible || !_bubbleOpen || AnimationConfig.ReduceMotion)
        {
            text.Text = value;
            Motion.Stop(text, OpacityProperty, 1);
            return;
        }
        Motion.To(text, OpacityProperty, 0, Motion.Exit, done: () =>
        {
            if (_bubbleWords[text] != value) return;
            text.Text = value;
            Motion.To(text, OpacityProperty, 1, Motion.Tile);
        });
    }

    internal static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        // Round up: a timer at 0:00.4 still shows 0:01, and 0:00 only when it is done.
        int seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        return seconds >= 3600
            ? $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}"
            : $"{seconds / 60}:{seconds % 60:00}";
    }

    /// <summary>The ring drains clockwise from the top as the timer runs down.</summary>
    private void UpdateTimerArc(double fraction)
    {
        BubbleTimerArc.Data = TimerArcGeometry(fraction);
    }

    internal static Geometry TimerArcGeometry(double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        const double cx = 50, cy = 50, r = 42;
        if (fraction <= 0.001) return Geometry.Empty;
        if (fraction >= 0.999)
        {
            var full = new EllipseGeometry(new Point(cx, cy), r, r);
            full.Freeze();
            return full;
        }
        double angle = fraction * 2 * Math.PI;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Sin(angle), cy - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    #endregion

    #region Motion

    /// <summary>
    /// Out from under the notch's edge: it slides out on a spring and, like a drop pulling free, stretches
    /// along the way and squashes as it lands. The face surfaces a beat later.
    /// </summary>
    private void BudBubble()
    {
        ++_bubbleToken;
        _bubbleCloseTimer?.Stop();
        bool entering = !ActivityBubble.IsVisible;
        if (entering) ResetBubbleShape();
        ActivityBubble.Visibility = Visibility.Visible;
        PositionBubble();
        UpdateBubbleContent();
        Motion.To(ActivityBubble, WidthProperty, BubbleSize, Motion.Pop);
        Motion.To(ActivityBubble, HeightProperty, BubbleSize, Motion.Pop);
        Motion.To(BubbleSlide, TranslateTransform.XProperty, 0, Motion.Pop, from: entering ? -(BubbleSize + BubbleGap) : null);
        Motion.To(BubbleScale, ScaleTransform.ScaleXProperty, 1, Motion.Pop, from: entering ? Motion.TileFromScale : null);
        Motion.To(BubbleScale, ScaleTransform.ScaleYProperty, 1, Motion.Pop, from: entering ? Motion.TileFromScale : null);
        Motion.To(BubbleIcon, OpacityProperty, 1, Motion.Tile, from: entering ? 0 : null, delay: entering ? Motion.Stagger(1) : null);
    }

    /// <summary>Back in under the notch, quicker than it came: a short pull, then gone.</summary>
    private void RetractBubble(bool instant = false)
    {
        int token = ++_bubbleToken;
        _bubbleCloseTimer?.Stop();
        if (_bubbleActivity == LiveActivity.Charging) LeaveChargeFace(fast: true);
        if (ActivityBubble.Visibility != Visibility.Visible) return;
        if (_bubbleOpen && !_bubbleListMode) HideBubbleDetail(fast: true);
        _bubbleOpen = false;
        UpdateScriptJobDetailClock();
        if (instant || AnimationConfig.ReduceMotion || !ActivityBubble.IsVisible) { FinishRetract(); return; }
        Motion.To(ActivityBubble, WidthProperty, BubbleSize, Motion.PopBack);
        Motion.To(ActivityBubble, HeightProperty, BubbleSize, Motion.PopBack);
        Motion.To(BubbleIcon, OpacityProperty, 0, Motion.Exit);
        Motion.To(BubbleScale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.PopBack);
        Motion.To(BubbleScale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.PopBack);
        Motion.To(BubbleSlide, TranslateTransform.XProperty, -(BubbleSize + BubbleGap), Motion.PopBack,
            done: () => { if (token == _bubbleToken) FinishRetract(); });
        void FinishRetract()
        {
            ActivityBubble.Visibility = Visibility.Collapsed;
            ResetBubbleShape();
            ApplyClaudeLiveMotion();
            ApplyBubbleMusicSpin();
        }
    }

    /// <summary>
    /// Another activity takes the orb: the old face sinks away, the orb gives a small bump, and the new face
    /// springs up in its place.
    /// </summary>
    private void SwapBubbleFace(LiveActivity next)
    {
        int token = ++_bubbleToken;
        if (_bubbleOpen) CloseBubble();
        if (_bubbleActivity == LiveActivity.Charging) LeaveChargeFace(fast: false);
        var scale = BubbleIconScale();
        Motion.To(scale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
        Motion.To(scale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
        Motion.To(BubbleIcon, OpacityProperty, 0, Motion.Exit, done: () =>
        {
            if (token != _bubbleToken) return;
            SetBubbleFace(next);
            Motion.To(BubbleIcon, OpacityProperty, 1, Motion.Tile);
            Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale);
            Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale);
            Motion.To(BubbleScale, ScaleTransform.ScaleXProperty, 1, Motion.Press, velocity: 1.3);
            Motion.To(BubbleScale, ScaleTransform.ScaleYProperty, 1, Motion.Press, velocity: -1.3);
        });
    }

    private static readonly DependencyProperty BubbleFaceInsetProperty = DependencyProperty.Register(
        "BubbleFaceInset", typeof(double), typeof(MainWindow), new PropertyMetadata(0d,
            (d, e) => ((MainWindow)d).BubbleIcon.Margin = new Thickness((double)e.NewValue, 0, 0, 0)));

    private void SpringBubbleFace(bool open, SpringMotion spring)
    {
        double size = FaceSize(_bubbleActivity, open);
        Motion.To(BubbleIcon, WidthProperty, size, spring);
        Motion.To(BubbleIcon, HeightProperty, size, spring);
        Motion.To(this, BubbleFaceInsetProperty, FaceMargin(_bubbleActivity, open).Left, spring);
    }

    private ScaleTransform BubbleIconScale()
    {
        if (BubbleIcon.RenderTransform is ScaleTransform existing) return existing;
        var scale = new ScaleTransform(1, 1);
        BubbleIcon.RenderTransformOrigin = new Point(0.5, 0.5);
        BubbleIcon.RenderTransform = scale;
        return scale;
    }

    /// <summary>
    /// From orb to pill on a soft spring, growing to the right; the words follow a beat behind, sliding in out
    /// of a blur.
    /// </summary>
    private void OpenBubble()
    {
        if (_bubbleOpen || !_bubbleShown) return;
        if (_bubbleActivity == LiveActivity.Claude && _bubbleClaudeSessions.Count > 1) { OpenBubbleList(); return; }
        _bubbleOpen = true;
        UpdateBubbleContent();
        ApplyClaudeLiveMotion();
        Motion.To(ActivityBubble, WidthProperty, BubbleOpenTargetWidth(), Motion.Pop);
        Motion.To(ActivityBubble, HeightProperty, BubbleOpenHeight, Motion.Pop);
        SpringBubbleFace(open: true, Motion.Pop);
        BubbleDetail.Margin = new Thickness(FaceMargin(_bubbleActivity, open: true).Left * 2 + FaceSize(_bubbleActivity, open: true), 0, 14, 0);
        bool entering = !BubbleDetail.IsVisible;
        BubbleDetail.Visibility = Visibility.Visible;
        Motion.To(BubbleDetail, OpacityProperty, 1, Motion.Tile, from: entering ? 0 : null, delay: Motion.Stagger(1));
        Motion.To(BubbleDetailSlide, TranslateTransform.XProperty, 0, Motion.Tile, from: entering ? -8 : null);
        Motion.To(BubbleDetailBlur, BlurEffect.RadiusProperty, 0, Motion.Tile, from: entering ? 7 : null);
        ScheduleBubbleClose(ActivityBubble.IsMouseOver ? null : BubbleUntouched);
    }

    /// <summary>The words go first, then the pill draws back into the orb without bouncing.</summary>
    private void CloseBubble()
    {
        if (!_bubbleOpen) return;
        if (_bubbleListMode) { CloseBubbleList(); return; }
        _bubbleOpen = false;
        _bubbleCloseTimer?.Stop();
        UpdateScriptJobDetailClock();
        HideBubbleDetail(fast: false);
        Motion.To(ActivityBubble, WidthProperty, BubbleSize, Motion.PopBack);
        Motion.To(ActivityBubble, HeightProperty, BubbleSize, Motion.PopBack);
        SpringBubbleFace(open: false, Motion.PopBack);
        ApplyClaudeLiveMotion();
    }

    private void HideBubbleDetail(bool fast)
    {
        int token = _bubbleToken;
        Motion.To(BubbleDetailSlide, TranslateTransform.XProperty, -8, Motion.Exit);
        Motion.To(BubbleDetailBlur, BlurEffect.RadiusProperty, 5, Motion.Exit);
        Motion.To(BubbleDetail, OpacityProperty, 0, Motion.Exit, done: () =>
        {
            if (token == _bubbleToken && !_bubbleOpen) BubbleDetail.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>Closed orb, nothing running on it: the state it buds out in.</summary>
    private void ResetBubbleShape()
    {
        _bubbleOpen = false;
        _bubbleListMode = false;
        Motion.Stop(BubbleList, OpacityProperty);
        ClearBubbleRows();
        BubbleList.Visibility = Visibility.Collapsed;
        Motion.Stop(ActivityBubble, WidthProperty, BubbleSize);
        Motion.Stop(ActivityBubble, HeightProperty, BubbleSize);
        Motion.Stop(BubbleSlide, TranslateTransform.XProperty, 0);
        Motion.Stop(BubbleScale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(BubbleScale, ScaleTransform.ScaleYProperty, 1);
        var iconScale = BubbleIconScale();
        Motion.Stop(iconScale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(iconScale, ScaleTransform.ScaleYProperty, 1);
        Motion.Stop(BubbleIcon, WidthProperty, FaceSize(_bubbleActivity, open: false));
        Motion.Stop(BubbleIcon, HeightProperty, FaceSize(_bubbleActivity, open: false));
        Motion.Stop(this, BubbleFaceInsetProperty, FaceMargin(_bubbleActivity, open: false).Left);
        Motion.Stop(BubbleIcon, OpacityProperty, 1);
        Motion.Stop(BubbleDetail, OpacityProperty, 0);
        Motion.Stop(BubbleDetailSlide, TranslateTransform.XProperty, 0);
        BubbleDetail.Visibility = Visibility.Collapsed;
        Motion.Stop(BubbleDetailBlur, BlurEffect.RadiusProperty, 0);
        Motion.Stop(BubbleCountBadge, OpacityProperty, 1);
        Motion.Stop(BubbleCountBadgeScale, ScaleTransform.ScaleXProperty, 1);
        Motion.Stop(BubbleCountBadgeScale, ScaleTransform.ScaleYProperty, 1);
        foreach (var (text, value) in _bubbleWords)
        {
            Motion.Stop(text, OpacityProperty, 1);
            text.Text = value;
        }
    }

    /// <summary>The cover turns like a record while the music plays, and only while the orb is on screen.</summary>
    private void ApplyBubbleMusicSpin()
    {
        bool spin = AmbientAnimationsAllowed && _bubbleShown && _bubbleActivity == LiveActivity.Music &&
                    ActivityBubble.IsVisible && _currentMediaInfo?.IsPlaying == true;
        if (spin == _bubbleMusicSpinning) return;
        _bubbleMusicSpinning = spin;
        if (!spin)
        {
            double angle = BubbleMusicSpin.Angle;
            BubbleMusicSpin.BeginAnimation(RotateTransform.AngleProperty, null);
            BubbleMusicSpin.Angle = angle;
            return;
        }
        double from = BubbleMusicSpin.Angle % 360;
        var turn = new DoubleAnimation(from, from + 360, new Duration(TimeSpan.FromSeconds(9))) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(turn, ClaudeLiveFps);
        BubbleMusicSpin.BeginAnimation(RotateTransform.AngleProperty, turn);
    }

    /// <summary>A press sinks the orb a little; letting go springs it back.</summary>
    private void PressBubble(bool down)
    {
        Motion.To(BubbleScale, ScaleTransform.ScaleXProperty, down ? 0.9 : 1, down ? Motion.PressDown : Motion.Press);
        Motion.To(BubbleScale, ScaleTransform.ScaleYProperty, down ? 0.9 : 1, down ? Motion.PressDown : Motion.Press);
    }

    private static DoubleAnimationUsingKeyFrames Keys(TimeSpan duration, params (double At, double Value)[] frames)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        foreach (var (at, value) in frames)
        {
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromPercent(at),
                new SineEase { EasingMode = EasingMode.EaseInOut }));
        }
        Fps(animation);
        return animation;
    }

    private static void Fps(Timeline timeline) => Timeline.SetDesiredFrameRate(timeline, AnimationConfig.TargetFps);

    #endregion

    #region Several Claude sessions

    private const double BubbleRowHeight = 32;
    private const int BubbleMaxRows = 5;
    private readonly List<(string Id, System.Windows.Shapes.Path Star, TextBlock Name, TextBlock Status, TextBlock Time)> _bubbleListRows = new();
    private int _bubbleShownCount;

    /// <summary>On the closed orb, how many sessions it stands for (from two on); pops when the number changes.</summary>
    private void UpdateBubbleClaudeCount()
    {
        int count = _bubbleActivity == LiveActivity.Claude ? _bubbleClaudeSessions.Count : 0;
        bool show = count > 1 && !_bubbleListMode;
        if (!show)
        {
            _bubbleShownCount = 0;
            if (BubbleCountBadge.Visibility != Visibility.Visible) return;
            if (!BubbleCountBadge.IsVisible)
            {
                Motion.Stop(BubbleCountBadge, OpacityProperty, 0);
                Motion.Stop(BubbleCountBadgeScale, ScaleTransform.ScaleXProperty, Motion.ExitToScale);
                Motion.Stop(BubbleCountBadgeScale, ScaleTransform.ScaleYProperty, Motion.ExitToScale);
                BubbleCountBadge.Visibility = Visibility.Collapsed;
                return;
            }
            Motion.To(BubbleCountBadgeScale, ScaleTransform.ScaleXProperty, Motion.ExitToScale, Motion.Exit);
            Motion.To(BubbleCountBadgeScale, ScaleTransform.ScaleYProperty, Motion.ExitToScale, Motion.Exit);
            Motion.To(BubbleCountBadge, OpacityProperty, 0, Motion.Exit, done: () =>
            {
                if (_bubbleShownCount == 0) BubbleCountBadge.Visibility = Visibility.Collapsed;
            });
            return;
        }
        bool entering = !BubbleCountBadge.IsVisible;
        BubbleCountBadge.Visibility = Visibility.Visible;
        CrossFadeText(BubbleCountText, count > 9 ? "9+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Motion.To(BubbleCountBadge, OpacityProperty, 1, Motion.Tile, from: entering ? 0 : null);
        if (count == _bubbleShownCount) return;
        _bubbleShownCount = count;
        if (!ActivityBubble.IsVisible)
        {
            Motion.Stop(BubbleCountBadgeScale, ScaleTransform.ScaleXProperty, 1);
            Motion.Stop(BubbleCountBadgeScale, ScaleTransform.ScaleYProperty, 1);
            return;
        }
        Motion.To(BubbleCountBadgeScale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, from: entering ? Motion.TileFromScale : null);
        Motion.To(BubbleCountBadgeScale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, from: entering ? Motion.TileFromScale : null);
    }

    private double BubbleListHeight(int rows) =>
        BubbleList.Margin.Top + BubbleList.Margin.Bottom + Math.Min(rows, BubbleMaxRows) * BubbleRowHeight;

    /// <summary>
    /// The orb opens into a list, one row per session: it grows on a soft spring while its own star fades,
    /// and the rows slide in one after another.
    /// </summary>
    private void OpenBubbleList()
    {
        _bubbleOpen = true;
        _bubbleListMode = true;
        UpdateBubbleClaudeCount();
        UpdateBubbleList(animate: true);
        ApplyClaudeLiveMotion();
        Motion.To(ActivityBubble, WidthProperty, BubbleOpenTargetWidth(), Motion.Pop);
        Motion.To(ActivityBubble, HeightProperty, BubbleListHeight(_bubbleClaudeSessions.Count), Motion.Pop);
        Motion.To(BubbleIcon, OpacityProperty, 0, Motion.Exit);
        ScheduleBubbleClose(ActivityBubble.IsMouseOver ? null : BubbleUntouched);
    }

    /// <summary>The rows fade first, then the list draws back into the orb and its star returns.</summary>
    private void CloseBubbleList()
    {
        _bubbleOpen = false;
        _bubbleCloseTimer?.Stop();
        int token = _bubbleToken;
        Motion.To(BubbleList, OpacityProperty, 0, Motion.Exit, done: () =>
        {
            if (token != _bubbleToken || _bubbleOpen) return;
            BubbleList.Visibility = Visibility.Collapsed;
            ClearBubbleRows();
        });
        Motion.To(ActivityBubble, WidthProperty, BubbleSize, Motion.PopBack);
        Motion.To(ActivityBubble, HeightProperty, BubbleSize, Motion.PopBack);
        Motion.To(BubbleIcon, OpacityProperty, 1, Motion.Tile);
        _bubbleListMode = false;
        UpdateBubbleClaudeCount();
        ApplyClaudeLiveMotion();
    }

    /// <summary>One row per session; rebuilt when the set changes, otherwise only the words and times move.</summary>
    private void UpdateBubbleList(bool animate)
    {
        var sessions = _bubbleClaudeSessions.Take(BubbleMaxRows).ToList();
        bool sameSet = sessions.Count == _bubbleListRows.Count &&
                       sessions.Select(s => s.Id).SequenceEqual(_bubbleListRows.Select(r => r.Id));
        if (!sameSet && !animate && BubbleList.IsVisible && !AnimationConfig.ReduceMotion)
        {
            // Keep the previous rows until they have left; the callback reads the latest sessions.
            Motion.To(BubbleList, OpacityProperty, 0, Motion.Exit, done: () =>
            {
                if (!_bubbleOpen || !_bubbleListMode) return;
                BubbleList.Visibility = Visibility.Collapsed;
                Motion.To(ActivityBubble, HeightProperty, BubbleListHeight(_bubbleClaudeSessions.Count), Motion.Glide);
                UpdateBubbleList(animate: true);
            });
            return;
        }
        if (!sameSet)
        {
            ClearBubbleRows();
            for (int i = 0; i < sessions.Count; i++)
            {
                var row = BuildBubbleRow(sessions[i]);
                BubbleList.Children.Add(row);
                if (animate && !AnimationConfig.ReduceMotion) SlideRowIn(row, i);
            }
            if (_bubbleOpen && !animate)
            {
                Motion.To(ActivityBubble, HeightProperty, BubbleListHeight(sessions.Count), Motion.Glide);
            }
        }
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            var row = _bubbleListRows[i];
            row.Star.Fill = ClaudeStarBrush(session);
            CrossFadeText(row.Name, session.Name);
            CrossFadeText(row.Status, session.Status == ClaudeSessionStatus.Working && session.Activity is { Length: > 0 } activity
                ? activity
                : Loc.Get(ClaudeLiveStatusKey(session)));
            row.Status.Foreground = session.Status == ClaudeSessionStatus.Waiting ? ClaudeLiveWaitingBrush : BubbleMuted;
            CrossFadeText(row.Time, ControlCenterView.FormatElapsed(DateTime.UtcNow - (session.TurnStartedUtc ?? DateTime.UtcNow)));
        }
        bool entering = !BubbleList.IsVisible;
        BubbleList.Visibility = Visibility.Visible;
        if (entering || BubbleList.Opacity < 1)
            Motion.To(BubbleList, OpacityProperty, 1, Motion.Tile, from: entering ? 0 : null);
    }

    private void ClearBubbleRows()
    {
        // Removed rows must not keep their staggered springs ticking behind the collapsed orb.
        foreach (FrameworkElement row in BubbleList.Children)
        {
            Motion.Stop(row, OpacityProperty);
            if (row.RenderTransform is TransformGroup { Children: [ScaleTransform scale, TranslateTransform slide] })
            {
                Motion.Stop(scale, ScaleTransform.ScaleXProperty);
                Motion.Stop(scale, ScaleTransform.ScaleYProperty);
                Motion.Stop(slide, TranslateTransform.XProperty);
            }
        }
        foreach (var row in _bubbleListRows)
            foreach (var text in new[] { row.Name, row.Status, row.Time }) Motion.Stop(text, OpacityProperty);
        BubbleList.Children.Clear();
        _bubbleListRows.Clear();
    }

    private FrameworkElement BuildBubbleRow(ClaudeSession session)
    {
        var star = new System.Windows.Shapes.Path { Data = ClaudeGlyph.Data, Stretch = Stretch.Uniform, Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center };
        var name = new TextBlock
        {
            Foreground = UiPalette.PrimaryBrush, FontSize = 12, FontWeight = FontWeights.SemiBold,
            FontFamily = (FontFamily)FindResource("MainSystemFont"), TextTrimming = TextTrimming.CharacterEllipsis
        };
        var status = new TextBlock
        {
            FontSize = 10.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 0),
            FontFamily = (FontFamily)FindResource("MainSystemFont"), TextTrimming = TextTrimming.CharacterEllipsis
        };
        var time = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF)), FontSize = 11, FontWeight = FontWeights.SemiBold,
            FontFamily = (FontFamily)FindResource("MainSystemFont"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0)
        };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
        words.Children.Add(name);
        words.Children.Add(status);

        var grid = new Grid { Height = BubbleRowHeight, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var starBox = new Border { Width = 22, Child = star };
        grid.Children.Add(starBox);
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);

        // A light wash under the pointer, a press, then straight to that session's terminal.
        var row = new Border { CornerRadius = new CornerRadius(10), Child = grid, RenderTransformOrigin = new Point(0.5, 0.5) };
        var press = new ScaleTransform(1, 1);
        var slide = new TranslateTransform();
        row.RenderTransform = new TransformGroup { Children = { press, slide } };
        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
        row.MouseLeave += (_, _) => row.Background = null;
        row.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Motion.To(press, ScaleTransform.ScaleXProperty, 0.96, Motion.PressDown);
            Motion.To(press, ScaleTransform.ScaleYProperty, 0.96, Motion.PressDown);
        };
        string id = session.Id;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Motion.To(press, ScaleTransform.ScaleXProperty, 1, Motion.Press);
            Motion.To(press, ScaleTransform.ScaleYProperty, 1, Motion.Press);
            var target = _bubbleClaudeSessions.FirstOrDefault(s => s.Id == id);
            CloseBubble();
            if (target == null || !ClaudeSessionWindow.TryActivate(target.Id, target.Title))
                _transitionCoordinator.RequestView(NotchView.Claude, "ActivityBubbleList");
        };
        _bubbleListRows.Add((id, star, name, status, time));
        return row;
    }

    /// <summary>Each row slides in from the left out of nothing, a beat after the one above.</summary>
    private static void SlideRowIn(FrameworkElement row, int index)
    {
        Motion.To(row, OpacityProperty, 1, Motion.Tile, from: 0, delay: Motion.Stagger(index));
        if (row.RenderTransform is TransformGroup { Children: [ScaleTransform scale, TranslateTransform translate] })
        {
            Motion.To(scale, ScaleTransform.ScaleXProperty, 1, Motion.TileScale, from: Motion.TileFromScale, delay: Motion.Stagger(index));
            Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.TileScale, from: Motion.TileFromScale, delay: Motion.Stagger(index));
            Motion.To(translate, TranslateTransform.XProperty, 0, Motion.Tile, from: -10, delay: Motion.Stagger(index));
        }
    }

    #endregion

    #region Input

    private void ActivityBubble_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PressBubble(down: true);
    }

    /// <summary>A tap opens the orb; a tap on the open pill goes to what it shows.</summary>
    private void ActivityBubble_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        PressBubble(down: false);
        if (!_bubbleOpen)
        {
            OpenBubble();
            return;
        }
        // In the list a row takes the tap; beside the rows it just closes.
        bool list = _bubbleListMode;
        CloseBubble();
        if (list) return;
        switch (_bubbleActivity)
        {
            case LiveActivity.Claude:
                if (_bubbleClaudeSessions.FirstOrDefault() is not { } session || !ClaudeSessionWindow.TryActivate(session.Id, session.Title))
                    _transitionCoordinator.RequestView(NotchView.Claude, "ActivityBubble");
                break;
            case LiveActivity.Music:
                _transitionCoordinator.RequestView(NotchView.Media, "ActivityBubble");
                break;
            case LiveActivity.Countdown:
                _transitionCoordinator.RequestView(NotchView.Timer, "ActivityBubble");
                break;
            case LiveActivity.Lesson:
                _transitionCoordinator.RequestView(NotchView.School, "ActivityBubble");
                break;
            case LiveActivity.Download when _downloads.Count > 0:
                OpenDownloadSource(_downloads[0]);
                break;
            case LiveActivity.Charging:
                // The system tab: power mode and the rest of the machine.
                _transitionCoordinator.RequestView(NotchView.Timer, "ActivityBubble");
                break;
        }
    }

    private void ActivityBubble_MouseEnter(object sender, MouseEventArgs e)
    {
        _bubbleCloseTimer?.Stop();
        if (e.StylusDevice == null && _bubbleActivity == LiveActivity.Job && _bubbleWanted == LiveActivity.Job)
            OpenBubble();
    }

    private void ActivityBubble_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_bubbleOpen) ScheduleBubbleClose(BubbleLinger);
    }

    private void ScheduleBubbleClose(TimeSpan? after)
    {
        _bubbleCloseTimer?.Stop();
        if (after is not { } delay) return;
        if (_bubbleCloseTimer == null)
        {
            _bubbleCloseTimer = new DispatcherTimer(DispatcherPriority.Background);
            _bubbleCloseTimer.Tick += (_, _) =>
            {
                _bubbleCloseTimer.Stop();
                CloseBubble();
            };
        }
        _bubbleCloseTimer.Interval = delay;
        _bubbleCloseTimer.Start();
    }

    #endregion
}
