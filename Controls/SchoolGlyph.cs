using System.Windows.Media;

namespace VNotch.Controls;

/// <summary>
/// A graduation cap, for the school tab (deadlines, tests, to-dos) in the nav bar and the tab settings.
/// </summary>
public static class SchoolGlyph
{
    // On a 20×20 grid like the other tab icons: the board, the cap below it (a thin gap between them)
    // and the tassel hanging off the right corner.
    public const string PathData =
        "M10,2.6 L19.4,7.4 L10,12.2 L0.6,7.4 Z " +
        "M4.6,9.8 L10,12.6 L15.4,9.8 L15.4,13.4 C15.4,15.2 13,16.6 10,16.6 C7,16.6 4.6,15.2 4.6,13.4 Z " +
        "M17.3,8.4 L18.3,7.9 L18.3,13.3 A0.95,0.95 0 1 1 17.3,13.3 Z";

    public static Geometry Data { get; } = CreateData();

    private static Geometry CreateData()
    {
        var geometry = Geometry.Parse(PathData);
        geometry.Freeze();
        return geometry;
    }
}
