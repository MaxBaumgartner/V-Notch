using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch;

public partial class MainWindow : ISpotlightMorphHost
{
    internal Func<string, Task<bool>>? TodoSender { get; set; }
    private static readonly Brush TodoPillPink = (Brush)new SolidColorBrush(Color.FromRgb(0xFF, 0x37, 0x5F)).GetAsFrozen();

    /// <summary>Adds the to-do and confirms the result in the notch.</summary>
    async Task<bool> ISpotlightMorphHost.AddPiTodoAsync(string text)
    {
        bool added = false;
        try
        {
            if (TodoSender != null) added = await TodoSender(text);
            else if (!_settings.EnableLocalOnlyMode && _piClient?.IsConfigured == true)
                added = await _piClient.AddTodoAsync(text);
        }
        catch (Exception ex)
        {
            Services.RuntimeLog.Error("PI-TODO", ex, "Could not add to-do");
        }
        if (!_cleanedUp)
            ShowNotchPill(Services.Loc.Get(added ? "notch.todoAdded" : "notch.todoFailed"), "",
                null, isClaude: false, tap: null, glyph: "\uE739", duration: CopiedPillDuration, accent: TodoPillPink);
        return added;
    }

    internal async Task MarkSpotlightIntroSeenAsync()
    {
        _settings.HasSeenSpotlightIntro = true;
        _activeSettingsWindow?.MarkSpotlightIntroSeen();
        _preparedSettingsWindow?.MarkSpotlightIntroSeen();
        // Persist the committed snapshot so an unrelated live preview is not saved.
        var committed = _settingsService.Load().Clone();
        committed.HasSeenSpotlightIntro = true;
        await _settingsService.SaveAsync(committed);
    }

    internal ImageSource? CaptureSpotlightMorphVisual()
    {
        double width = NotchBorder.ActualWidth;
        double height = NotchBorder.ActualHeight;
        if (width <= 0 || height <= 0) return null;

        try
        {
            DpiScale dpi = VisualTreeHelper.GetDpi(NotchBorder);
            var bitmap = new RenderTargetBitmap(
                Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX)),
                Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY)),
                dpi.PixelsPerInchX,
                dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            // Render via VisualBrush to neutralize parent layout offsets and keep
            // snapshots pixel-aligned with the notch across all views.
            var neutral = new DrawingVisual();
            using (DrawingContext ctx = neutral.RenderOpen())
            {
                var brush = new VisualBrush(NotchBorder) { Stretch = Stretch.None };
                ctx.DrawRectangle(brush, null, new Rect(0, 0, width, height));
            }
            bitmap.Render(neutral);
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            Services.RuntimeLog.Error(
                "SPOTLIGHT-MORPH", ex, "Could not capture notch handoff");
            return null;
        }
    }

    (double Left, double Top, double Width, double Height, double TopCornerRadius, double BottomCornerRadius)
        ISpotlightMorphHost.GetSpotlightMorphRect() => GetSpotlightMorphRect();

    ImageSource? ISpotlightMorphHost.CaptureSpotlightMorphVisual() =>
        CaptureSpotlightMorphVisual();

    void ISpotlightMorphHost.SetSpotlightMorphSessionActive(bool active) =>
        SetSpotlightMorphSessionActive(active);

    void ISpotlightMorphHost.SetSpotlightMorphActive(bool active) =>
        SetSpotlightMorphActive(active);

    void ISpotlightMorphHost.BeginSpotlightReturnHandoff(TimeSpan duration) =>
        BeginSpotlightReturnHandoff(duration);
}
