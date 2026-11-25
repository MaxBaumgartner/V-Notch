using System.Windows;
using System.Windows.Media;

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

    internal ImageSource? CaptureSpotlightMorphVisual()
    {
        double width = NotchBorder.ActualWidth;
        double height = NotchBorder.ActualHeight;
        if (width <= 0 || height <= 0) return null;

        // Spotlight owns the brush only during the handoff. Reference the
        // notch below its hidden wrapper so composition can draw it directly.
        var bounds = new Rect(0, 0, width, height);
        var brush = new VisualBrush(NotchBorder)
        {
            AutoLayoutContent = false,
            Stretch = Stretch.None
        };
        return new DrawingImage(new GeometryDrawing(brush, null, new RectangleGeometry(bounds)));
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
