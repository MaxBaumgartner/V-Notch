using System.Windows.Media;

namespace VNotch.Controls;

/// <summary>A gauge, for the Legion tab (power modes) in the nav bar and the tab settings.</summary>
public static class LegionGlyph
{
    // On a 20×20 grid like the other tab icons: a half ring, a needle pointing up right, its hub.
    public const string PathData =
        "M2,14.5 A8,8 0 0 1 18,14.5 L16.2,14.5 A6.2,6.2 0 0 0 3.8,14.5 Z " +
        "M9.29,13.79 L14.24,10.26 L10.71,15.21 Z " +
        "M8.4,14.5 A1.6,1.6 0 1 1 11.6,14.5 A1.6,1.6 0 1 1 8.4,14.5 Z";

    public static Geometry Data { get; } = CreateData();

    private static Geometry CreateData()
    {
        var geometry = Geometry.Parse(PathData);
        geometry.Freeze();
        return geometry;
    }
}
