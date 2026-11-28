using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.Spotlight;

namespace VNotch;

public partial class SpotlightWindow
{
    private VNotch.Controls.ThinkingOrb? _aiThinkingOrb;
    private VNotch.Controls.AgentOrbLabel? _aiThinkingLabel;
    private Border? _aiThinkingCard;
    private LinearGradientBrush? _aiPromptRevealMask;

    private RichTextBox? AddAiMessage(SpotlightAiMessage message, bool animate)
    {
        var sfPro = (FontFamily)FindResource("SFProDisplay");
        bool user = message.Role == "user";
        if (user)
        {
            var userContainer = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(64, 4, 0, 16),
                MaxWidth = 520,
                CornerRadius = new CornerRadius(18),
                // Balance the visible glyphs around SF Pro's baseline, not just the line box.
                Padding = new Thickness(16, 10, 16, 14)
            };
            userContainer.SetResourceReference(Border.BackgroundProperty, "AccentChatBubbleBrush");

            var textBlock = new TextBlock
            {
                Text = message.Content,
                FontFamily = sfPro,
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                Foreground = Controls.SpotlightChatPalette.Primary,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 22,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Left
            };
            System.Windows.Automation.AutomationProperties.SetName(textBlock, Loc.Get("spotlight.ai.you") + ": " + message.Content);
            userContainer.Child = textBlock;
            AiTranscript.Children.Add(userContainer);
            if (animate) AnimateAiArrival(userContainer);
            return null;
        }

        // Assistant Message - Flat Surface (NO FLOAT CARD DESIGN)
        var view = CreateMarkdownView(message.Content);
        view.FontFamily = sfPro;
        view.FontWeight = FontWeights.Bold;

        var card = new Border
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0, 4, 0, 8),
            Margin = new Thickness(0, 4, 0, 20),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var stack = new StackPanel();

        // Keep the selected brand and model visible beside each assistant response.
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var (provider, model) = CurrentAiIdentity();
        var brandStack = new Grid { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        brandStack.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        brandStack.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        brandStack.Children.Add(CreateAiBrandImage(provider, 14));

        var modelTitle = new TextBlock
        {
            Text = SpotlightAiModelName.Format(model, provider),
            ToolTip = provider + " · " + model,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = sfPro,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = Controls.SpotlightChatPalette.Primary,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(modelTitle, 1);
        brandStack.Children.Add(modelTitle);
        headerGrid.Children.Add(brandStack);

        // Per-message copy button
        var msgCopyBtn = new Button
        {
            Style = (Style)FindResource("AiToolbarStyle"),
            ToolTip = Loc.Get("spotlight.ai.copy"),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        SetAiToolLabel(msgCopyBtn, "spotlight.ai.copy");
        Grid.SetColumn(msgCopyBtn, 2);

        var copyPath = new System.Windows.Shapes.Path
        {
            Data = AiCopyGeometry,
            Fill = ChatIconBrush,
            Stretch = Stretch.Uniform,
            Width = 13,
            Height = 13
        };
        msgCopyBtn.Content = copyPath;

        msgCopyBtn.Click += async (_, _) =>
        {
            if (TryCopyToClipboard(message.Content))
            {
                msgCopyBtn.Content = new TextBlock
                {
                    Text = "\u2713",
                    FontFamily = sfPro,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = UiPalette.PrimaryBrush
                };
                // The check mark hops in on Press.
                var pop = new ScaleTransform();
                msgCopyBtn.RenderTransform = pop;
                Motion.To(pop, ScaleTransform.ScaleXProperty, 1, Motion.Press, from: 1.3);
                Motion.To(pop, ScaleTransform.ScaleYProperty, 1, Motion.Press, from: 1.3);
                await Task.Delay(1800, CancellationToken.None);
                msgCopyBtn.Content = copyPath;
            }
        };

        headerGrid.Children.Add(msgCopyBtn);
        stack.Children.Add(headerGrid);
        stack.Children.Add(view);
        card.Child = stack;

        AiTranscript.Children.Add(card);
        if (animate) AnimateAiArrival(card);
        return view;
    }

    private void ShowAiThinking()
    {
        if (_aiThinkingCard != null) return;
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        _aiThinkingOrb = new VNotch.Controls.ThinkingOrb
        {
            Width = 28,
            Height = 28,
            RandomizeStates = true,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_aiThinkingOrb);
        _aiThinkingLabel = new Controls.AgentOrbLabel
        {
            Source = _aiThinkingOrb,
            FontFamily = (FontFamily)FindResource("SFProDisplay"),
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = Controls.SpotlightChatPalette.Secondary,
            Height = 28,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(_aiThinkingLabel);
        _aiThinkingCard = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(0, 4, 0, 4),
            Margin = new Thickness(0, 2, 0, 8),
            Child = row
        };
        AiTranscript.Children.Add(_aiThinkingCard);
        AnimateAiArrival(_aiThinkingCard);
        AiTranscriptScroll.ScrollToEnd();
    }

    private void HideAiThinking()
    {
        if (_aiThinkingOrb != null)
        {
            AiActivityOrb.ContinueAgentFrom(_aiThinkingOrb);
            _aiThinkingOrb.IsActive = false;
        }
        _aiThinkingOrb = null;
        if (_aiThinkingLabel != null) _aiThinkingLabel.Source = null;
        _aiThinkingLabel = null;
        if (_aiThinkingCard == null) return;
        AiTranscript.Children.Remove(_aiThinkingCard);
        _aiThinkingCard = null;
    }

    private Task TransitionAiPageAsync(Action changePage)
    {
        if (!IsSpotlightOpen || _isClosing) return Task.CompletedTask;
        // Pin the presented height before Visibility changes invalidate layout.
        // Capture the animated value so rapid navigation continues from this frame.
        bool holdHeight = _contentShown && !_entranceActive && !AnimationConfig.ReduceMotion;
        double presentedHeight = ContentRegion.Height;
        if (!double.IsFinite(presentedHeight)) presentedHeight = ContentRegion.ActualHeight;
        if (holdHeight)
        {
            ++_contentHeightAnimationVersion;
            ContentRegion.BeginAnimation(HeightProperty, null);
            ContentRegion.Height = presentedHeight;
            ContentRegion.ClipToBounds = true;
            _contentHeightTarget = double.NaN;
        }
        // History and new-chat actions resize the shell without replaying the AI mode entrance.
        bool wasAiMode = _aiMode;
        changePage();
        if (_aiMode)
        {
            if (holdHeight)
            {
                var inset = ContentRegion.BorderThickness;
                var padding = ContentRegion.Padding;
                double width = Math.Max(1, ContentRegion.ActualWidth - inset.Left - inset.Right - padding.Left - padding.Right);
                ContentRegion.Child.Measure(new Size(width, double.PositiveInfinity));
                double target = ContentRegion.Child.DesiredSize.Height + inset.Top + inset.Bottom + padding.Top + padding.Bottom;
                if (double.IsFinite(target))
                    BeginContentHeightAnimation(presentedHeight, target, _contentSizeGeneration);
                else
                {
                    ContentRegion.Height = double.NaN;
                    ContentRegion.ClipToBounds = false;
                }
            }
            if (!wasAiMode)
            {
                AnimateAiArrival(AiWelcome.Visibility == Visibility.Visible ? AiWelcome : AiTranscriptScroll);
            }
        }
        ScheduleContentResize();
        return Task.CompletedTask;
    }

    private void AnimateAiPromptReveal()
    {
        ResetAiPromptReveal();
        if (AnimationConfig.ReduceMotion || string.IsNullOrEmpty(SearchBox.Text)) return;

        SearchBox.UpdateLayout();
        double width = Math.Max(1, SearchBox.ActualWidth);
        var caret = GetSearchBoxCaretRect(SearchBox.Text.Length, SearchBox.Text.Length);
        double end = caret.IsEmpty ? 1 : Math.Clamp((caret.Right + 4) / width, 0.08, 1);
        double feather = Math.Min(0.12, 28 / width);
        var ink = new GradientStop(Colors.Black, -feather);
        var edge = new GradientStop(Colors.Transparent, 0);
        var mask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5),
            GradientStops = { ink, edge }
        };
        _aiPromptRevealMask = mask;
        SearchBox.OpacityMask = mask;

        // Reveal the ink in place with a feathered sweep. The complete draft is
        // already editable; no character timers, layout shifts or delayed input.
        var duration = TimeSpan.FromMilliseconds(360);
        var ease = CubicBezierEase.FromEaseOutCurve(0.22, 0.61, 0.36, 1);
        var inkReveal = new DoubleAnimation(-feather, end, duration) { EasingFunction = ease };
        var edgeReveal = new DoubleAnimation(0, end + feather, duration) { EasingFunction = ease };
        Timeline.SetDesiredFrameRate(inkReveal, AnimationConfig.TargetFps);
        Timeline.SetDesiredFrameRate(edgeReveal, AnimationConfig.TargetFps);
        edgeReveal.Completed += (_, _) =>
        {
            if (ReferenceEquals(_aiPromptRevealMask, mask)) ResetAiPromptReveal();
        };
        ink.BeginAnimation(GradientStop.OffsetProperty, inkReveal);
        edge.BeginAnimation(GradientStop.OffsetProperty, edgeReveal);
    }

    private void ResetAiPromptReveal()
    {
        if (_aiPromptRevealMask is not { } mask) return;
        _aiPromptRevealMask = null;
        foreach (var stop in mask.GradientStops) stop.BeginAnimation(GradientStop.OffsetProperty, null);
        if (ReferenceEquals(SearchBox.OpacityMask, mask)) SearchBox.OpacityMask = null;
    }

    private static void AnimateAiArrival(FrameworkElement element)
    {
        if (!element.IsVisible || AnimationConfig.ReduceMotion ||
            Motion.IsRunning(element, OpacityProperty)) return;

        // The header or the prompt-side edge is the origin of the new card.
        var scale = new ScaleTransform(1, 0.9);
        var offset = new TranslateTransform(0, -4);
        element.RenderTransformOrigin = new Point(element.HorizontalAlignment == HorizontalAlignment.Right ? 1 : 0, 0);
        element.RenderTransform = new TransformGroup { Children = { scale, offset } };
        void Settle()
        {
            element.IsVisibleChanged -= OnVisibleChanged;
            Motion.Stop(scale, ScaleTransform.ScaleYProperty, 1);
            Motion.Stop(offset, TranslateTransform.YProperty, 0);
            Motion.Stop(element, OpacityProperty, 1);
        }
        void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!element.IsVisible) Settle();
        }
        element.IsVisibleChanged += OnVisibleChanged;
        Motion.To(scale, ScaleTransform.ScaleYProperty, 1, Motion.Shift);
        Motion.To(element, OpacityProperty, 1, Motion.Exit, from: 0.72);
        Motion.To(offset, TranslateTransform.YProperty, 0, Motion.Shift, done: Settle);
    }

    private void UpdateAiActivity()
    {
        if (_aiRequest == null || !_aiMode || !IsSpotlightOpen || _isClosing)
        {
            StopAiActivity();
            return;
        }
        // Carry the same randomized reference motion from waiting to streaming,
        // keeping one active orb until the last word has been revealed.
        if (_aiThinkingOrb != null) _aiThinkingOrb.IsActive = true;
        bool composing = _aiThinkingCard == null;
        AiActivityOrb.Visibility = composing ? Visibility.Visible : Visibility.Collapsed;
        AiActivityOrb.IsActive = composing;
    }

    private void StopAiActivity()
    {
        if (_aiThinkingOrb != null) _aiThinkingOrb.IsActive = false;
        AiActivityOrb.IsActive = false;
        AiActivityOrb.Visibility = Visibility.Collapsed;
    }

    private int _aiGlowVersion;

    private void UpdateAiAmbientGlow(bool active)
    {
        if (AiGlowBorder == null) return;
        int version = ++_aiGlowVersion;
        if (!IsVisible || AnimationConfig.ReduceMotion)
        {
            Motion.Stop(AiGlowBorder, OpacityProperty, active ? 0.5 : 0);
            Motion.Stop(AiGlowEffect, DropShadowEffect.BlurRadiusProperty, 16);
            AiGlowBorder.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            AiGlowBorder.Opacity = active ? 0.5 : 0;
            return;
        }

        if (active)
        {
            AiGlowBorder.Visibility = Visibility.Visible;
            Motion.To(AiGlowBorder, OpacityProperty, 0.5, Motion.Tile);

            // The glow settles when the mode changes.
            Motion.To(AiGlowEffect, DropShadowEffect.BlurRadiusProperty, 16, Motion.Shift);
        }
        else
        {
            Motion.To(AiGlowBorder, OpacityProperty, 0, Motion.Exit, done: () =>
            {
                if (version != _aiGlowVersion || _aiMode) return;
                Motion.Stop(AiGlowBorder, OpacityProperty, 0);
                AiGlowBorder.Visibility = Visibility.Collapsed;
                Motion.Stop(AiGlowEffect, DropShadowEffect.BlurRadiusProperty, 16);
            });
        }
    }


    private static Geometry GetLookupIconGeometry() => AiMagnifierGeometry;

    private Geometry GetChatIconGeometry() => VNotch.Controls.AiProviderBrand.GeometryFor(CurrentAiIdentity().Provider);

    private void SyncSearchIconState(bool animate = false)
    {
        if (SearchIcon == null) return;
        var geom = _aiMode ? GetChatIconGeometry() : GetLookupIconGeometry();
        SearchIcon.Brush = UiPalette.PrimaryBrush;
        SearchIcon.MorphTo(geom, animate: animate);
    }

    private void AnimateSearchIconToAi(bool toAi)
    {
        if (SearchIcon == null) return;

        var targetGeom = toAi ? GetChatIconGeometry() : GetLookupIconGeometry();
        SearchIcon.Brush = UiPalette.PrimaryBrush;

        if (AnimationConfig.ReduceMotion)
        {
            SearchIcon.MorphTo(targetGeom, animate: false);
            return;
        }

        // Morph between the lookup icon and the selected provider's brand mark.
        SearchIcon.MorphTo(targetGeom, animate: true, duration: TimeSpan.FromMilliseconds(380),
            easing: new CubicEase { EasingMode = EasingMode.EaseOut });

        // Subtle tactile elastic pop during the morph transition
        var scale = new DoubleAnimationUsingKeyFrames();
        scale.KeyFrames.Add(new LinearDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        scale.KeyFrames.Add(new EasingDoubleKeyFrame(0.85, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(75)), new CubicEase { EasingMode = EasingMode.EaseIn }));
        scale.KeyFrames.Add(new EasingDoubleKeyFrame(1.14, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(210)), new CubicEase { EasingMode = EasingMode.EaseOut }));
        scale.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)), new QuarticEase { EasingMode = EasingMode.EaseOut }));

        Timeline.SetDesiredFrameRate(scale, AnimationConfig.TargetFps);
        SearchIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        SearchIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);
    }
}
