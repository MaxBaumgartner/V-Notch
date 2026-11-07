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
            lesson: IsLessonLive, download: _downloads.Count > 0);
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
        UpdateActivityBubble();
        UpdateIdleShape();
    }

    /// <summary>The orb shows its activity while the notch rests closed with its own content on screen.</summary>
    private void UpdateActivityBubble()
    {
        HookActivityBubble();
        bool surface = MusicCompactContent.IsVisible || CollapsedContent.IsVisible;
        LiveActivity want = !_cleanedUp && surface && !_isExpanded && !_isMusicExpanded && !_isAnimating
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
            SetBubbleFace(want);
            BudBubble();
            return;
        }
        if (want != _bubbleActivity) SwapBubbleFace(want);
        else UpdateBubbleContent();
    }

    private void HookActivityBubble()
    {
        if (_bubbleHooked) return;
        _bubbleHooked = true;
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

    /// <summary>Just right of the notch (and its ear), flush with its top.</summary>
    private void PositionBubble()
    {
        if (ActivityBubble.Visibility != Visibility.Visible) return;
        double ear = LeftEar.Visibility == Visibility.Visible ? LeftEar.Width - 1 : 0;
        double notchWidth = _isExpanded ? _collapsedWidth : NotchBorder.ActualWidth;
        BubbleAnchor.X = Math.Round(NotchContainer.ActualWidth / 2 + notchWidth / 2 + ear + BubbleGap);
        BubbleAnchor.Y = 0;
    }

    private double BubbleOpenTargetWidth() =>
        Math.Max(BubbleSize, Math.Min(BubbleOpenWidth, NotchContainer.ActualWidth - BubbleAnchor.X - 6));

    private void ActivityBubble_SizeChanged(object sender, SizeChangedEventArgs e) =>
        // A perfect pill whatever size the spring has it at; the taller list keeps the pill's corners.
        ActivityBubble.CornerRadius = new CornerRadius(Math.Min(e.NewSize.Height / 2, BubbleOpenHeight / 2));

    #endregion

    #region Faces and words

    private static double FaceSize(LiveActivity activity, bool open) => activity switch
    {
        LiveActivity.Music => open ? 30 : 24,
        LiveActivity.Countdown or LiveActivity.Lesson or LiveActivity.Download => open ? 26 : 21,
        _ => open ? 19 : 16
    };

    private Thickness FaceMargin(LiveActivity activity, bool open)
    {
        double height = open ? BubbleOpenHeight : BubbleSize;
        return new Thickness((height - FaceSize(activity, open)) / 2, 0, 0, 0);
    }

    private void SetBubbleFace(LiveActivity activity)
    {
        _bubbleActivity = activity;
        BubbleClaudeFace.Visibility = activity == LiveActivity.Claude ? Visibility.Visible : Visibility.Collapsed;
        BubbleMusicFace.Visibility = activity == LiveActivity.Music ? Visibility.Visible : Visibility.Collapsed;
        BubbleTimerFace.Visibility = activity == LiveActivity.Countdown ? Visibility.Visible : Visibility.Collapsed;
        BubbleLessonFace.Visibility = activity == LiveActivity.Lesson ? Visibility.Visible : Visibility.Collapsed;
        BubbleDownloadFace.Visibility = activity == LiveActivity.Download ? Visibility.Visible : Visibility.Collapsed;
        foreach (var property in new[] { WidthProperty, HeightProperty, MarginProperty })
            BubbleIcon.BeginAnimation(property, null);
        double size = FaceSize(activity, _bubbleOpen);
        BubbleIcon.Width = BubbleIcon.Height = size;
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
        }
        BubbleTitle.Text = title;
        BubbleSubtitle.Text = subtitle;
        BubbleSubtitle.Foreground = subtitleBrush;
        BubbleSubtitle.Visibility = subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        BubbleTrailing.Text = trailing;
        UpdateBubbleClaudeCount();
        if (_bubbleListMode) UpdateBubbleList(animate: false);
        ApplyBubbleMusicSpin();
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
        ResetBubbleShape();
        ActivityBubble.Visibility = Visibility.Visible;
        PositionBubble();
        UpdateBubbleContent();
        if (AnimationConfig.ReduceMotion) return;

        var duration = TimeSpan.FromMilliseconds(620);
        var slide = new DoubleAnimation(-(BubbleSize + BubbleGap), 0, new Duration(duration))
        {
            EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 7, EasingMode = EasingMode.EaseOut }
        };
        Fps(slide);
        BubbleSlide.BeginAnimation(TranslateTransform.XProperty, slide);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, Keys(duration, (0, 0.45), (0.38, 1.2), (0.68, 0.94), (1, 1)));
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, Keys(duration, (0, 0.45), (0.38, 0.84), (0.68, 1.06), (1, 1)));

        var faceIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            BeginTime = TimeSpan.FromMilliseconds(170),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Fps(faceIn);
        BubbleIcon.Opacity = 0;
        BubbleIcon.BeginAnimation(OpacityProperty, faceIn);
    }

    /// <summary>Back in under the notch, quicker than it came: a short pull, then gone.</summary>
    private void RetractBubble()
    {
        int token = ++_bubbleToken;
        _bubbleCloseTimer?.Stop();
        if (ActivityBubble.Visibility != Visibility.Visible) return;
        if (_bubbleOpen && !_bubbleListMode) HideBubbleDetail(fast: true);
        _bubbleOpen = false;

        if (AnimationConfig.ReduceMotion || !ActivityBubble.IsVisible)
        {
            FinishRetract();
            return;
        }

        var duration = new Duration(TimeSpan.FromMilliseconds(300));
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var slide = new DoubleAnimation(-(BubbleSize + BubbleGap), duration) { EasingFunction = ease };
        slide.Completed += (_, _) => { if (token == _bubbleToken) FinishRetract(); };
        var shrink = new DoubleAnimation(0.4, duration) { EasingFunction = ease };
        var close = new DoubleAnimation(BubbleSize, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Fps(slide);
        Fps(shrink);
        Fps(close);
        ActivityBubble.BeginAnimation(WidthProperty, close);
        ActivityBubble.BeginAnimation(HeightProperty, close);
        BubbleSlide.BeginAnimation(TranslateTransform.XProperty, slide);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);

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
        if (AnimationConfig.ReduceMotion)
        {
            SetBubbleFace(next);
            return;
        }

        var scale = BubbleIconScale();
        var outDuration = new Duration(TimeSpan.FromMilliseconds(150));
        var fadeOut = new DoubleAnimation(0, outDuration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        var sink = new DoubleAnimation(0.5, outDuration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        Fps(fadeOut);
        Fps(sink);
        fadeOut.Completed += (_, _) =>
        {
            if (token != _bubbleToken) return;
            SetBubbleFace(next);
            var inDuration = new Duration(TimeSpan.FromMilliseconds(420));
            var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(180)));
            var rise = new DoubleAnimation(0.5, 1, inDuration) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut } };
            Fps(fadeIn);
            Fps(rise);
            BubbleIcon.BeginAnimation(OpacityProperty, fadeIn);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, rise);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, rise);
            var bump = Keys(TimeSpan.FromMilliseconds(380), (0, 1), (0.35, 1.08), (1, 1));
            BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, bump);
            BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, bump);
        };
        BubbleIcon.BeginAnimation(OpacityProperty, fadeOut);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, sink);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, sink);
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
        if (_bubbleActivity == LiveActivity.Claude && _bubbleClaudeSessions.Count > 1)
        {
            OpenBubbleList();
            return;
        }
        _bubbleOpen = true;
        UpdateBubbleContent();
        ApplyClaudeLiveMotion();

        var spring = new BackEase { Amplitude = 0.32, EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(BubbleOpenTargetWidth(), new Duration(TimeSpan.FromMilliseconds(520))) { EasingFunction = spring };
        var tall = new DoubleAnimation(BubbleOpenHeight, new Duration(TimeSpan.FromMilliseconds(460))) { EasingFunction = spring };
        var face = new DoubleAnimation(FaceSize(_bubbleActivity, open: true), new Duration(TimeSpan.FromMilliseconds(460))) { EasingFunction = spring };
        var faceMargin = new ThicknessAnimation(FaceMargin(_bubbleActivity, open: true), new Duration(TimeSpan.FromMilliseconds(460))) { EasingFunction = spring };
        Fps(grow);
        Fps(tall);
        Fps(face);
        Fps(faceMargin);
        ActivityBubble.BeginAnimation(WidthProperty, grow);
        ActivityBubble.BeginAnimation(HeightProperty, tall);
        BubbleIcon.BeginAnimation(WidthProperty, face);
        BubbleIcon.BeginAnimation(HeightProperty, face);
        BubbleIcon.BeginAnimation(MarginProperty, faceMargin);

        BubbleDetail.Margin = new Thickness(FaceMargin(_bubbleActivity, open: true).Left * 2 + FaceSize(_bubbleActivity, open: true), 0, 14, 0);
        BubbleDetail.Visibility = Visibility.Visible;
        var begin = TimeSpan.FromMilliseconds(110);
        var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(240))) { BeginTime = begin, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        var slideIn = new DoubleAnimation(-8, 0, new Duration(TimeSpan.FromMilliseconds(420))) { BeginTime = begin, EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut } };
        var unblur = new DoubleAnimation(7, 0, new Duration(TimeSpan.FromMilliseconds(300))) { BeginTime = begin, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        Fps(fade);
        Fps(slideIn);
        Fps(unblur);
        BubbleDetail.BeginAnimation(OpacityProperty, fade);
        BubbleDetailSlide.BeginAnimation(TranslateTransform.XProperty, slideIn);
        BubbleDetailBlur.BeginAnimation(BlurEffect.RadiusProperty, unblur);

        ScheduleBubbleClose(ActivityBubble.IsMouseOver ? null : BubbleUntouched);
    }

    /// <summary>The words go first, then the pill draws back into the orb without bouncing.</summary>
    private void CloseBubble()
    {
        if (!_bubbleOpen) return;
        if (_bubbleListMode)
        {
            CloseBubbleList();
            return;
        }
        _bubbleOpen = false;
        _bubbleCloseTimer?.Stop();
        HideBubbleDetail(fast: false);

        var ease = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(420));
        var begin = TimeSpan.FromMilliseconds(60);
        var narrow = new DoubleAnimation(BubbleSize, duration) { EasingFunction = ease, BeginTime = begin };
        var face = new DoubleAnimation(FaceSize(_bubbleActivity, open: false), duration) { EasingFunction = ease, BeginTime = begin };
        var faceMargin = new ThicknessAnimation(FaceMargin(_bubbleActivity, open: false), duration) { EasingFunction = ease, BeginTime = begin };
        Fps(narrow);
        Fps(face);
        Fps(faceMargin);
        ActivityBubble.BeginAnimation(WidthProperty, narrow);
        ActivityBubble.BeginAnimation(HeightProperty, narrow);
        BubbleIcon.BeginAnimation(WidthProperty, face);
        BubbleIcon.BeginAnimation(HeightProperty, face);
        BubbleIcon.BeginAnimation(MarginProperty, faceMargin);
        ApplyClaudeLiveMotion();
    }

    private void HideBubbleDetail(bool fast)
    {
        int token = _bubbleToken;
        var duration = new Duration(TimeSpan.FromMilliseconds(fast ? 80 : 130));
        var fade = new DoubleAnimation(0, duration) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        var blur = new DoubleAnimation(5, duration);
        Fps(fade);
        Fps(blur);
        fade.Completed += (_, _) =>
        {
            if (token == _bubbleToken && !_bubbleOpen) BubbleDetail.Visibility = Visibility.Collapsed;
        };
        BubbleDetail.BeginAnimation(OpacityProperty, fade);
        BubbleDetailBlur.BeginAnimation(BlurEffect.RadiusProperty, blur);
    }

    /// <summary>Closed orb, nothing running on it: the state it buds out in.</summary>
    private void ResetBubbleShape()
    {
        _bubbleOpen = false;
        _bubbleListMode = false;
        BubbleList.BeginAnimation(OpacityProperty, null);
        BubbleList.Children.Clear();
        BubbleList.Visibility = Visibility.Collapsed;
        _bubbleListRows.Clear();
        ActivityBubble.BeginAnimation(WidthProperty, null);
        ActivityBubble.BeginAnimation(HeightProperty, null);
        ActivityBubble.Width = ActivityBubble.Height = BubbleSize;
        BubbleSlide.BeginAnimation(TranslateTransform.XProperty, null);
        BubbleSlide.X = 0;
        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        BubbleScale.ScaleX = BubbleScale.ScaleY = 1;
        var iconScale = BubbleIconScale();
        iconScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        iconScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        iconScale.ScaleX = iconScale.ScaleY = 1;
        foreach (var property in new[] { WidthProperty, HeightProperty, MarginProperty, OpacityProperty })
            BubbleIcon.BeginAnimation(property, null);
        BubbleIcon.Width = BubbleIcon.Height = FaceSize(_bubbleActivity, open: false);
        BubbleIcon.Margin = FaceMargin(_bubbleActivity, open: false);
        BubbleIcon.Opacity = 1;
        BubbleDetail.BeginAnimation(OpacityProperty, null);
        BubbleDetail.Opacity = 0;
        BubbleDetail.Visibility = Visibility.Collapsed;
        BubbleDetailBlur.BeginAnimation(BlurEffect.RadiusProperty, null);
        BubbleDetailBlur.Radius = 0;
    }

    /// <summary>The cover turns like a record while the music plays, and only while the orb is on screen.</summary>
    private void ApplyBubbleMusicSpin()
    {
        bool spin = !AnimationConfig.ReduceMotion && _bubbleShown && _bubbleActivity == LiveActivity.Music &&
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
        if (AnimationConfig.ReduceMotion) return;
        DoubleAnimation scale = down
            ? new DoubleAnimation(0.9, new Duration(TimeSpan.FromMilliseconds(90))) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } }
            : new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(460))) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 5, EasingMode = EasingMode.EaseOut } };
        Fps(scale);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        BubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
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
        BubbleCountBadge.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            _bubbleShownCount = 0;
            return;
        }
        BubbleCountText.Text = count > 9 ? "9+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (count == _bubbleShownCount) return;
        _bubbleShownCount = count;
        if (AnimationConfig.ReduceMotion || !ActivityBubble.IsVisible) return;
        var pop = Keys(TimeSpan.FromMilliseconds(360), (0, 0.3), (0.45, 1.25), (1, 1));
        BubbleCountBadgeScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        BubbleCountBadgeScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
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
        BubbleCountBadge.Visibility = Visibility.Collapsed;
        UpdateBubbleList(animate: true);
        ApplyClaudeLiveMotion();

        var spring = new BackEase { Amplitude = 0.28, EasingMode = EasingMode.EaseOut };
        var grow = new DoubleAnimation(BubbleOpenTargetWidth(), new Duration(TimeSpan.FromMilliseconds(520))) { EasingFunction = spring };
        var tall = new DoubleAnimation(BubbleListHeight(_bubbleClaudeSessions.Count), new Duration(TimeSpan.FromMilliseconds(500))) { EasingFunction = spring };
        var starOut = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(140)));
        Fps(grow);
        Fps(tall);
        Fps(starOut);
        ActivityBubble.BeginAnimation(WidthProperty, grow);
        ActivityBubble.BeginAnimation(HeightProperty, tall);
        BubbleIcon.BeginAnimation(OpacityProperty, starOut);
        ScheduleBubbleClose(ActivityBubble.IsMouseOver ? null : BubbleUntouched);
    }

    /// <summary>The rows fade first, then the list draws back into the orb and its star returns.</summary>
    private void CloseBubbleList()
    {
        _bubbleOpen = false;
        _bubbleCloseTimer?.Stop();
        int token = _bubbleToken;
        var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(110)));
        Fps(fade);
        fade.Completed += (_, _) =>
        {
            if (token != _bubbleToken || _bubbleOpen) return;
            BubbleList.Visibility = Visibility.Collapsed;
            BubbleList.Children.Clear();
            _bubbleListRows.Clear();
        };
        BubbleList.BeginAnimation(OpacityProperty, fade);

        var ease = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(440));
        var begin = TimeSpan.FromMilliseconds(70);
        var narrow = new DoubleAnimation(BubbleSize, duration) { EasingFunction = ease, BeginTime = begin };
        var starIn = new DoubleAnimation(1, new Duration(TimeSpan.FromMilliseconds(240))) { BeginTime = TimeSpan.FromMilliseconds(220) };
        Fps(narrow);
        Fps(starIn);
        ActivityBubble.BeginAnimation(WidthProperty, narrow);
        ActivityBubble.BeginAnimation(HeightProperty, narrow);
        BubbleIcon.BeginAnimation(OpacityProperty, starIn);
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
        if (!sameSet)
        {
            BubbleList.Children.Clear();
            _bubbleListRows.Clear();
            for (int i = 0; i < sessions.Count; i++)
            {
                var row = BuildBubbleRow(sessions[i]);
                BubbleList.Children.Add(row);
                if (animate && !AnimationConfig.ReduceMotion) SlideRowIn(row, i);
            }
            if (_bubbleOpen && !animate)
            {
                var tall = new DoubleAnimation(BubbleListHeight(sessions.Count), new Duration(TimeSpan.FromMilliseconds(300)))
                {
                    EasingFunction = new ExponentialEase { Exponent = 5, EasingMode = EasingMode.EaseOut }
                };
                Fps(tall);
                ActivityBubble.BeginAnimation(HeightProperty, tall);
            }
        }
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            var row = _bubbleListRows[i];
            row.Star.Fill = ClaudeStarBrush(session);
            row.Name.Text = session.Name;
            row.Status.Text = session.Status == ClaudeSessionStatus.Working && session.Activity is { Length: > 0 } activity
                ? activity
                : Loc.Get(ClaudeLiveStatusKey(session));
            row.Status.Foreground = session.Status == ClaudeSessionStatus.Waiting ? ClaudeLiveWaitingBrush : BubbleMuted;
            row.Time.Text = ControlCenterView.FormatElapsed(DateTime.UtcNow - (session.TurnStartedUtc ?? DateTime.UtcNow));
        }
        BubbleList.BeginAnimation(OpacityProperty, null);
        BubbleList.Opacity = 1;
        BubbleList.Visibility = Visibility.Visible;
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
            var down = new DoubleAnimation(0.96, new Duration(TimeSpan.FromMilliseconds(80)));
            press.BeginAnimation(ScaleTransform.ScaleXProperty, down);
            press.BeginAnimation(ScaleTransform.ScaleYProperty, down);
        };
        string id = session.Id;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            press.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            press.BeginAnimation(ScaleTransform.ScaleYProperty, null);
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
        var begin = TimeSpan.FromMilliseconds(120 + index * 55);
        var fade = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(260))) { BeginTime = begin, FillBehavior = FillBehavior.Stop };
        var slide = new DoubleAnimation(-10, 0, new Duration(TimeSpan.FromMilliseconds(420)))
        {
            BeginTime = begin,
            EasingFunction = new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut }
        };
        Fps(fade);
        Fps(slide);
        row.Opacity = 0;
        fade.Completed += (_, _) => row.Opacity = 1;
        row.BeginAnimation(OpacityProperty, fade);
        if (row.RenderTransform is TransformGroup { Children: [_, TranslateTransform translate] })
            translate.BeginAnimation(TranslateTransform.XProperty, slide);
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
        }
    }

    private void ActivityBubble_MouseEnter(object sender, MouseEventArgs e) => _bubbleCloseTimer?.Stop();

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
