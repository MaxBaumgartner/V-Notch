using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using VNotch.Services;

namespace VNotch;

public partial class MainWindow
{
    #region Greeting Animation (Apple-style "Hello" handwriting on startup)

    private bool _isGreetingActive = false;
    internal bool IsGreetingInteractionBlocked => _isGreetingActive;

    private void BlockGreetingInput(object sender, System.Windows.Input.PreProcessInputEventArgs e)
    {
        if (_isGreetingActive && e.StagingItem.Input is
            System.Windows.Input.MouseEventArgs or System.Windows.Input.KeyboardEventArgs or
            System.Windows.Input.TextCompositionEventArgs or System.Windows.Input.TouchEventArgs or
            System.Windows.Input.StylusEventArgs)
            e.Cancel();
    }
    private UiScheduledTimer? _greetingDismissTimer;
    private bool _isVietnameseGreeting = false;
    private int _greetingGeneration;

    private void PlayGreetingAnimation()
    {
        if (_cleanedUp || GreetingOverlay == null || HelloPath1 == null || HelloPath2 == null) return;

        int generation = ++_greetingGeneration;
        _greetingDismissTimer?.Stop();
        _greetingDismissTimer = null;

        _isGreetingActive = true;
        _isAnimating = true;
        _isVietnameseGreeting = StartupGreeting.UsesVietnamese(_settings.Language);
        // Hide the parent, preserving each widget's visibility and bindings.
        NotchLiveContent.Visibility = Visibility.Hidden;

        GreetingOverlay.Visibility = Visibility.Visible;
        Motion.To(GreetingOverlay, OpacityProperty, 1, Motion.Tile);
        PrepareGreetingPathsIfNeeded();

        AnimateCornerRadius(_cornerRadiusExpanded, Motion.ShellOpenHeight);
        Motion.To(NotchBorder, WidthProperty, _expandedWidth, Motion.ShellOpenWidth);
        Motion.To(NotchBorder, HeightProperty, _expandedHeight, Motion.ShellOpenHeight);
        if (!IsGreetingCurrent(generation)) return;
        if (AnimationConfig.ReduceMotion)
        {
            foreach (var path in new[] { HelloPath1, HelloPath2, ViPath1, ViPath2, ViPath3, ViPath4, ViPath5, ViPath6, ViPath7, ViPath8, ViPath9, ViPath10 })
                path.StrokeDashOffset = 0;
            ViDotI.Visibility = Visibility.Visible;
            ViDotI.Opacity = 1;
            ScheduleGreetingDismiss(TimeSpan.FromMilliseconds(1500));
            return;
        }
        if (_isVietnameseGreeting)
            PlayXinChaoStrokeAnimation();
        else
            PlayHelloStrokeAnimation();
    }

    private void PlayXinChaoStrokeAnimation()
    {
        var paths = new (Path path, double durationMs, double delayMs)[]
        {
            (ViPath1,  110,    0),
            (ViPath2,  230,  130),
            (ViPath3,  210,  340),
            (ViPath4,  150,  520),
            (ViPath5,  400,  640),
            (ViPath6,  520, 1010),
            (ViPath7,  500, 1490),
            (ViPath8,  560, 1950),
            (ViPath9,  900, 2460),
            (ViPath10, 480, 3420),
        };

        foreach (var (path, durationMs, delayMs) in paths)
        {
            double pathLength = (double)path.Tag;

            if (delayMs > 0)
            {
                path.Opacity = 0;
                var show = new DoubleAnimationUsingKeyFrames();
                show.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
                show.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs))));
                Timeline.SetDesiredFrameRate(show, AnimationConfig.TargetFps);
                path.BeginAnimation(OpacityProperty, show);
            }
            else
            {
                path.Opacity = 1;
            }

            var anim = new DoubleAnimation
            {
                From = pathLength,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };

            System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(anim, VNotch.Services.AnimationConfig.TargetFps);
            path.BeginAnimation(Shape.StrokeDashOffsetProperty, anim);
        }

        var totalDurationMs = 3420 + 480 + 1500;

        ViDotI.Visibility = Visibility.Visible;
        ViDotI.Opacity = 0;
        var dotFadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(160),
            BeginTime = TimeSpan.FromMilliseconds(340),
        };
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(dotFadeIn, VNotch.Services.AnimationConfig.TargetFps);
        ViDotI.BeginAnimation(OpacityProperty, dotFadeIn);

        ScheduleGreetingDismiss(TimeSpan.FromMilliseconds(totalDurationMs));
    }

    private void PrepareGreetingPathsIfNeeded()
    {
        if (_cleanedUp || GreetingOverlay == null || HelloPath1 == null || HelloPath2 == null) return;

        _isVietnameseGreeting = StartupGreeting.UsesVietnamese(_settings.Language);
        HelloPathContainer.Visibility = Visibility.Collapsed;
        XinChaoPathContainer.Visibility = Visibility.Collapsed;

        if (_isVietnameseGreeting)
        {
            XinChaoPathContainer.Visibility = Visibility.Visible;

            PreparePath(ViPath1);
            PreparePath(ViPath2);
            PreparePath(ViPath3);
            PreparePath(ViPath4);
            PreparePath(ViPath5);
            PreparePath(ViPath6);
            PreparePath(ViPath7);
            PreparePath(ViPath8);
            PreparePath(ViPath9);
            PreparePath(ViPath10);
        }
        else
        {
            HelloPathContainer.Visibility = Visibility.Visible;

            PreparePath(HelloPath1);
            PreparePath(HelloPath2);
        }
    }

    private static void PreparePath(Path path)
    {
        path.Visibility = Visibility.Visible;

        if (path.Tag is not double normalizedLength || normalizedLength <= 0)
        {
            var geometry = path.Data.GetFlattenedPathGeometry();
            double length = GetPathLength(geometry);
            normalizedLength = length / path.StrokeThickness;
            path.Tag = normalizedLength;
        }

        path.StrokeDashCap = PenLineCap.Flat;
        path.StrokeStartLineCap = PenLineCap.Flat;
        path.StrokeEndLineCap = PenLineCap.Flat;
        path.StrokeDashArray = new DoubleCollection { normalizedLength, normalizedLength * 3 };
        path.StrokeDashOffset = normalizedLength;
    }

    private static double GetPathLength(PathGeometry geometry)
    {
        double totalLength = 0;

        foreach (var figure in geometry.Figures)
        {
            var lastPoint = figure.StartPoint;

            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment polyLine)
                {
                    foreach (var point in polyLine.Points)
                    {
                        totalLength += (point - lastPoint).Length;
                        lastPoint = point;
                    }
                }
                else if (segment is LineSegment line)
                {
                    totalLength += (line.Point - lastPoint).Length;
                    lastPoint = line.Point;
                }
            }
        }

        return totalLength;
    }

    private void PlayHelloStrokeAnimation()
    {
        int generation = _greetingGeneration;
        double path1Length = (double)HelloPath1.Tag;
        double path2Length = (double)HelloPath2.Tag;

        var path1Anim = new DoubleAnimation
        {
            From = path1Length,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(900),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        var path2Anim = new DoubleAnimation
        {
            From = path2Length,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(3000),
            BeginTime = TimeSpan.FromMilliseconds(700),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        path2Anim.Completed += (s, e) =>
        {
            if (IsGreetingCurrent(generation)) ScheduleGreetingDismiss(TimeSpan.FromMilliseconds(1500));
        };

        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(path1Anim, VNotch.Services.AnimationConfig.TargetFps);
        HelloPath1.BeginAnimation(Shape.StrokeDashOffsetProperty, path1Anim);
        System.Windows.Media.Animation.Timeline.SetDesiredFrameRate(path2Anim, VNotch.Services.AnimationConfig.TargetFps);
        HelloPath2.BeginAnimation(Shape.StrokeDashOffsetProperty, path2Anim);
    }

    private void DismissGreeting()
    {
        int generation = _greetingGeneration;
        if (!IsGreetingCurrent(generation)) return;
        Motion.To(GreetingOverlay, OpacityProperty, 0, Motion.Exit, done: () =>
        {
            if (_cleanedUp || generation != _greetingGeneration) return;
            GreetingOverlay.Visibility = Visibility.Collapsed;
            Motion.Stop(GreetingOverlay, OpacityProperty, 0);
            foreach (var path in new[] { HelloPath1, HelloPath2, ViPath1, ViPath2, ViPath3, ViPath4, ViPath5, ViPath6, ViPath7, ViPath8, ViPath9, ViPath10 })
                path.BeginAnimation(OpacityProperty, null);

            if (_isVietnameseGreeting)
            {
                ViPath1.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath2.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath3.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath4.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath5.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath6.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath7.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath8.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath9.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViPath10.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                ViDotI.BeginAnimation(OpacityProperty, null);
                ViDotI.Opacity = 0;
                ViDotI.Visibility = Visibility.Collapsed;
            }
            else
            {
                HelloPath1.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                HelloPath2.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            }

        });
        CollapseAfterGreeting();
    }

    private void CollapseAfterGreeting()
    {
        int generation = _greetingGeneration;
        AnimateCornerRadius(_cornerRadiusCollapsed, Motion.ShellCloseHeight);
        int remaining = 2;
        void Arrived()
        {
            if (--remaining != 0 || !IsGreetingCurrent(generation)) return;
            Motion.Stop(NotchBorder, WidthProperty, _collapsedWidth);
            Motion.Stop(NotchBorder, HeightProperty, _collapsedHeight);
            _isAnimating = false;
            _isGreetingActive = false;
            RestorePrivacyDotVisibility();
            NotchLiveContent.Visibility = Visibility.Visible;

            CollapsedContent.Visibility = Visibility.Visible;
            Motion.To(CollapsedContent, OpacityProperty, 1, Motion.Tile, from: 0, done: () =>
            {
                if (_cleanedUp || generation != _greetingGeneration) return;
                StartStartupHold(TimeSpan.FromSeconds(3));
                SchedulePostAppearInitialization();
            });
        }
        Motion.To(NotchBorder, WidthProperty, _collapsedWidth, Motion.ShellCloseWidth, done: Arrived);
        Motion.To(NotchBorder, HeightProperty, _collapsedHeight, Motion.ShellCloseHeight, done: Arrived);
    }

    private bool IsGreetingCurrent(int generation) => !_cleanedUp && _isGreetingActive && generation == _greetingGeneration;

    private void ScheduleGreetingDismiss(TimeSpan delay)
    {
        _greetingDismissTimer?.Stop();
        _greetingDismissTimer?.Dispose();
        int generation = _greetingGeneration;
        var timer = new UiScheduledTimer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            if (ReferenceEquals(_greetingDismissTimer, timer)) _greetingDismissTimer = null;
            if (IsGreetingCurrent(generation)) DismissGreeting();
        };
        _greetingDismissTimer = timer;
        timer.Start();
    }

    private void DisposeGreetingLifecycle()
    {
        System.Windows.Input.InputManager.Current.PreProcessInput -= BlockGreetingInput;
        ++_greetingGeneration;
        _greetingDismissTimer?.Stop();
        _greetingDismissTimer?.Dispose();
        _greetingDismissTimer = null;
        _isGreetingActive = false;
        Motion.Stop(GreetingOverlay, OpacityProperty, 0);
        foreach (var path in new[] { HelloPath1, HelloPath2, ViPath1, ViPath2, ViPath3, ViPath4, ViPath5, ViPath6, ViPath7, ViPath8, ViPath9, ViPath10 })
        {
            path.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            path.BeginAnimation(OpacityProperty, null);
        }
        ViDotI.BeginAnimation(OpacityProperty, null);
    }

    #endregion
}
