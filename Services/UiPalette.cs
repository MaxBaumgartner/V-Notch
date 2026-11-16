using System.Windows.Media;

namespace VNotch.Services;

internal static class UiPalette
{
    // Alpha belongs to the ink, so it composes with the surface underneath.
    public static readonly Color PrimaryColor = Colors.White;
    public static readonly Color SecondaryColor = Color.FromArgb(0xB3, 255, 255, 255);
    public static readonly Color TertiaryColor = Color.FromArgb(0x73, 255, 255, 255);
    public static readonly SolidColorBrush PrimaryBrush = CreateBrush(PrimaryColor);
    public static readonly SolidColorBrush SecondaryBrush = CreateBrush(SecondaryColor);
    public static readonly SolidColorBrush TertiaryBrush = CreateBrush(TertiaryColor);
    public static readonly SolidColorBrush IconBrush = TertiaryBrush;
    // The control center's existing lilac mood stop; shared by notification actions.
    public static readonly SolidColorBrush NotificationIndigoBrush = CreateBrush(Color.FromRgb(0xA1, 0x9B, 0xD2));

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
