using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;
using VNotch.Services.ClaudeCode;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private const double AskSheetSidePadding = 18;
    private static readonly SpringMotion AskSheetOpenSpring = new(TimeSpan.FromMilliseconds(280), 0.08);
    private static readonly SpringMotion AskSheetFoldSpring = new(TimeSpan.FromMilliseconds(142), 0);
    private static readonly SpringMotion AskSheetSlideSpring = new(TimeSpan.FromMilliseconds(178), 0);
    private static readonly Brush AskSheetText = CreateFrozenBrush(255, 255, 255, 230);
    private static readonly Brush AskSheetMuted = CreateFrozenBrush(255, 255, 255, 115);
    private static readonly Brush AskSheetLink = CreateFrozenBrush(255, 255, 255, 140);
    private static readonly Brush AskChipFill = CreateFrozenBrush(255, 255, 255, 15);
    private static readonly Brush AskChipHover = CreateFrozenBrush(255, 255, 255, 26);
    private static readonly Brush AskChipSelected = CreateFrozenBrush(0xA6, 0xC9, 0xA0, 56);

    private Grid? _askSheet;
    private bool _askSheetOpen;
    private Action? _afterAskSheetFold;
    private ClaudePermissionRequest? _askSheetRequest;
    private ClaudeQuestionAnswers? _askSheetAnswers;
    private int _askQuestionIndex;
    private Win32Interop.LowLevelKeyboardHook? _askSheetEscape;

    private void OpenAskSheet(ClaudePermissionRequest request)
    {
        if (_askSheet != null || request.Questions is not { Count: > 0 } questions || !CanShowClaudeAttention) return;
        if (!ReferenceEquals(_askSheetRequest, request))
        {
            _askSheetRequest = request;
            _askSheetAnswers = new ClaudeQuestionAnswers(questions);
            _askQuestionIndex = 0;
        }

        _hoverThumbnailDelayTimer.Stop();
        Motion.To(NotchScale, ScaleTransform.ScaleXProperty, 1, Motion.Press);
        Motion.To(NotchShadowScale, ScaleTransform.ScaleXProperty, 1, Motion.Press);
        Motion.To(HoverGlow, OpacityProperty, 0, Motion.Exit);
        NotchPillRow.Visibility = Visibility.Hidden;
        // Size the sheet against the question pill even if the pull hint was still fading.
        _textSwaps.Remove(ClaudeNotificationStatus);
        ClaudeNotificationStatus.BeginAnimation(OpacityProperty, null);
        ClaudeNotificationStatus.Opacity = 1;
        ClaudeNotificationStatus.Text = _askStatus;
        var sheet = new Grid { VerticalAlignment = VerticalAlignment.Top };
        ClaudeNotification.Children.Add(sheet);
        _askSheet = sheet;
        _askSheetOpen = true;
        ShowAskQuestion(sheet, advancing: false);
        UpdateOutsideClickMonitoring();

        // The compact overlay leaves focus in Claude's terminal. Observe Escape only while the sheet is open.
        _askSheetEscape = new Win32Interop.LowLevelKeyboardHook((key, message) =>
        {
            if (message != Win32Interop.WM_KEYDOWN || key.vkCode != 0x1B || !_askSheetOpen) return false;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (ReferenceEquals(_askSheet, sheet) && _askSheetOpen) FoldAskSheet();
            });
            return true;
        });
        _askSheetEscape.Install();
    }

    private void ShowAskQuestion(Grid sheet, bool advancing)
    {
        var questions = _askSheetRequest!.Questions!;
        var answers = _askSheetAnswers!;
        var question = questions[_askQuestionIndex];
        var page = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 12),
            RenderTransform = new TranslateTransform()
        };
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        if (questions.Count > 1)
        {
            var counter = AskSheetLabel($"{_askQuestionIndex + 1}/{questions.Count}", 11, AskSheetMuted);
            counter.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(counter, Dock.Right);
            heading.Children.Add(counter);
        }
        heading.Children.Add(AskSheetLabel(question.Header, 11, AskSheetMuted));
        if (heading.Children.Count > 1 || question.Header.Length > 0) page.Children.Add(heading);
        var prompt = AskSheetLabel(question.Text, 13, AskSheetText);
        prompt.TextWrapping = TextWrapping.Wrap;
        prompt.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        prompt.LineHeight = 17;
        prompt.MaxHeight = 34;
        prompt.ToolTip = question.Text;
        prompt.Margin = new Thickness(0, 0, 0, 8);
        page.Children.Add(prompt);

        var options = new WrapPanel { Margin = new Thickness(0, 0, -6, -6) };
        page.Children.Add(options);
        Button? done = null;
        foreach (var option in question.Options)
        {
            var chip = MakeAskChip(option.Label, option.Description);
            chip.Tag = answers.IsPicked(question, option.Label);
            PaintAskChip(chip);
            chip.Click += (_, _) =>
            {
                answers.Pick(question, option.Label);
                chip.Tag = answers.IsPicked(question, option.Label);
                PaintAskChip(chip);
                if (done == null) CompleteAskQuestion(sheet, page, question);
                else
                {
                    var visibility = answers.HasPick(question) ? Visibility.Visible : Visibility.Collapsed;
                    if (done.Visibility == visibility) return;
                    done.Visibility = visibility;
                    FitAskQuestion(page, options);
                }
            };
            options.Children.Add(chip);
        }
        if (question.MultiSelect)
        {
            done = MakeAskChip(Loc.Get("claude.pill.done"), null, AskAllowBrush);
            done.Visibility = answers.HasPick(question) ? Visibility.Visible : Visibility.Collapsed;
            done.Click += (_, _) => CompleteAskQuestion(sheet, page, question);
            options.Children.Add(done);
        }
        var open = MakeAskChip(Loc.Get("claude.pill.openInClaude"), null, AskSheetLink);
        open.Click += (_, _) => FoldAskSheet(() => OpenClaudeTabFromPill());
        options.Children.Add(open);
        sheet.Children.Clear();
        sheet.Children.Add(page);
        FitAskQuestion(page, options);

        var delay = TimeSpan.FromMilliseconds(advancing ? 0 : 60);
        var translate = (TranslateTransform)page.RenderTransform;
        Motion.To(translate, advancing ? TranslateTransform.XProperty : TranslateTransform.YProperty, 0,
            AskSheetSlideSpring, from: advancing ? 12 : 4, delay: delay);
        FadeAskSheet(page, 0, 1, advancing ? 200 : 150, _easeQuadOut, delay);
        for (int i = 0; i < options.Children.Count; i++)
        {
            var chip = (Button)options.Children[i];
            if (chip.Visibility == Visibility.Visible)
                FadeAskSheet(chip, 0, 1, 130, _easeQuadOut, delay + Motion.Stagger(i, 25));
        }
    }

    private void FitAskQuestion(StackPanel page, WrapPanel options)
    {
        options.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = GetNotificationPillWidth(options.DesiredSize.Width + 2 * AskSheetSidePadding);
        page.Width = width - 2 * AskSheetSidePadding;
        page.Measure(new Size(page.Width, double.PositiveInfinity));
        MoveAskSheetShell(width, Math.Max(_collapsedHeight, Math.Ceiling(page.DesiredSize.Height)), folding: false);
    }

    private TextBlock AskSheetLabel(string text, double size, Brush? brush = null) => new()
    {
        Text = text,
        FontFamily = ClaudeNotificationStatus.FontFamily,
        FontSize = size,
        Foreground = brush,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private Button MakeAskChip(string label, string? description, Brush? quiet = null)
    {
        var scale = new ScaleTransform();
        var chip = new Button
        {
            Content = AskSheetLabel(label, 12),
            Template = (ControlTemplate)FindResource("AskChipTemplate"),
            Height = 28,
            MaxWidth = NotificationPillMaxWidth - 2 * AskSheetSidePadding + 6,
            Margin = new Thickness(0, 0, 6, 6),
            RenderTransform = scale,
            RenderTransformOrigin = new Point(.5, .5),
            ToolTip = description ?? label,
            Tag = quiet
        };
        // Let the label inherit the foreground from the chip's template.
        ((TextBlock)chip.Content).ClearValue(TextBlock.ForegroundProperty);
        AutomationProperties.SetName(chip, label);
        void React(double target, SpringMotion spring, bool hovered)
        {
            PaintAskChip(chip, hovered);
            Motion.To(scale, ScaleTransform.ScaleXProperty, target, spring);
            Motion.To(scale, ScaleTransform.ScaleYProperty, target, spring);
        }
        chip.MouseEnter += (_, _) => React(1.03, Motion.Press, hovered: true);
        chip.MouseLeave += (_, _) => React(1, Motion.Press, hovered: false);
        Point start = default;
        chip.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(this);
            React(.97, Motion.PressDown, hovered: true);
        };
        chip.PreviewMouseMove += (_, e) =>
        {
            if (!chip.IsMouseCaptured || !IsAskSheetPullUp(e.GetPosition(this) - start)) return;
            chip.ReleaseMouseCapture();
            FoldAskSheet();
            e.Handled = true;
        };
        chip.LostMouseCapture += (_, _) => React(chip.IsMouseOver ? 1.03 : 1, Motion.Press, chip.IsMouseOver);
        PaintAskChip(chip);
        return chip;
    }

    private static void PaintAskChip(Button chip, bool? hovered = null)
    {
        bool selected = chip.Tag is true;
        bool quiet = chip.Tag is Brush brush && brush == AskSheetLink;
        chip.Background = (quiet, selected, hovered ?? chip.IsMouseOver) switch
        {
            (true, _, _) => Brushes.Transparent,
            (_, true, _) => AskChipSelected,
            (_, _, true) => AskChipHover,
            _ => AskChipFill
        };
        chip.BorderBrush = quiet ? Brushes.Transparent : AskChipHover;
        chip.Foreground = selected ? AskAllowBrush : chip.Tag as Brush ?? AskSheetText;
    }

    private void CompleteAskQuestion(Grid sheet, StackPanel page, ClaudeQuestion question)
    {
        if (!_askSheetOpen || !page.IsHitTestVisible || !ReferenceEquals(_askSheet, sheet)) return;
        page.IsHitTestVisible = false;
        var answers = _askSheetAnswers!;
        var request = _askSheetRequest!;
        if (_askQuestionIndex + 1 < request.Questions!.Count)
        {
            _askQuestionIndex++;
            Motion.To((TranslateTransform)page.RenderTransform, TranslateTransform.XProperty, -12,
                new SpringMotion(TimeSpan.FromMilliseconds(82), 0));
            FadeAskSheet(page, page.Opacity, 0, 90, _easeQuadInOut, done: () =>
            {
                if (_askSheetOpen && ReferenceEquals(_askSheet, sheet)) ShowAskQuestion(sheet, advancing: true);
            });
            return;
        }

        if (!answers.TrySubmit(values => _claudeTracker?.Answer(request.Id, values) == true))
        {
            SyncClaudeAskPill();
            return;
        }
        _askRequest = null;
        _askSheetRequest = null;
        _askSheetAnswers = null;
        _askStatus = Loc.Get("claude.pill.answered", answers.LabelsFor(question));
        ClaudeNotificationStatus.Foreground = AskAllowBrush;
        ClaudeNotificationStatus.Text = _askStatus;
        FoldAskSheet(LingerAskSettled, flash: !question.MultiSelect);
    }

    private static bool IsAskSheetPullUp(Vector moved) => -moved.Y >= 20 && -moved.Y > Math.Abs(moved.X);

    private void FoldAskSheet(Action? done = null, bool flash = false)
    {
        if (_askSheet is not { } sheet)
        {
            done?.Invoke();
            return;
        }
        if (done != null) _afterAskSheetFold = done;
        if (!_askSheetOpen) return;
        _askSheetOpen = false;
        StopAskSheetInput();
        sheet.IsHitTestVisible = false;
        var delay = TimeSpan.FromMilliseconds(flash ? 55 : 0);
        FadeAskSheet(sheet, sheet.Opacity, 0, 125, _easeCubicIn, delay);
        NotchPillRow.Visibility = Visibility.Visible;
        FadeAskSheet(NotchPillRow, 0, 1, 100, _easeQuadOut, delay + TimeSpan.FromMilliseconds(78));
        MoveAskSheetShell(GetNotificationPillWidth(), _collapsedHeight, folding: true, delay: delay, done: () =>
        {
            var afterFold = _afterAskSheetFold;
            ResetAskSheet();
            afterFold?.Invoke();
        });
    }

    private void HideAskSheet()
    {
        if (_askSheet == null) return;
        var afterFold = _afterAskSheetFold;
        ResetAskSheet();
        Motion.Stop(NotchBorder, WidthProperty, GetNotificationPillWidth());
        Motion.Stop(NotchBorder, HeightProperty, _collapsedHeight);
        afterFold?.Invoke();
    }

    private void MoveAskSheetShell(double width, double height, bool folding, TimeSpan? delay = null, Action? done = null)
    {
        _notchShellPresenter?.CancelCurrentAnimation();
        // Keep the chips at their measured size while the same shell that carries the pill reveals them.
        _claudePillAttachment?.Detach(true);
        var spring = folding ? AskSheetFoldSpring : AskSheetOpenSpring;
        double? speed = folding ? 0 : null;
        double acceleration = folding ? .4 : 0;
        int remaining = 2;
        var sheet = _askSheet;
        void Arrived()
        {
            if (--remaining == 0 && ReferenceEquals(_askSheet, sheet)) done?.Invoke();
        }
        Motion.To(NotchBorder, WidthProperty, width, spring, delay: delay, done: Arrived, velocity: speed, acceleration: acceleration);
        Motion.To(NotchBorder, HeightProperty, height, spring, delay: delay, done: Arrived, velocity: speed, acceleration: acceleration);
    }

    private void StopAskSheetInput()
    {
        _askSheetEscape?.Dispose();
        _askSheetEscape = null;
        UpdateOutsideClickMonitoring();
    }

    private void ResetAskSheet()
    {
        if (_askSheet == null) return;
        _askSheetOpen = false;
        StopAskSheetInput();
        ClaudeNotification.Children.Remove(_askSheet);
        _askSheet = null;
        _afterAskSheetFold = null;
        NotchPillRow.BeginAnimation(OpacityProperty, null);
        NotchPillRow.Opacity = 1;
        NotchPillRow.Visibility = Visibility.Visible;
        if (_askGesture != AskGesture.None) EndAskGesture();
    }

    private static void FadeAskSheet(UIElement element, double from, double to, int ms, IEasingFunction ease,
        TimeSpan? delay = null, Action? done = null)
    {
        element.BeginAnimation(OpacityProperty, null);
        element.Opacity = AnimationConfig.ReduceMotion ? to : from;
        if (AnimationConfig.ReduceMotion)
        {
            done?.Invoke();
            return;
        }
        var fade = MakeAnim(from, to, TimeSpan.FromMilliseconds(ms), ease, delay);
        fade.Completed += (_, _) =>
        {
            element.Opacity = to;
            element.BeginAnimation(OpacityProperty, null);
            done?.Invoke();
        };
        element.BeginAnimation(OpacityProperty, fade);
    }
}
