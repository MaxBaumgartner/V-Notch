using System.Windows.Media;

namespace VNotch.Controls;

/// <summary>
/// A game controller, for the Roblox tab (all of the creator's games added up) in the nav bar and the tab settings.
/// A plain pad rather than a brand mark.
/// </summary>
public static class RobloxGlyph
{
    // On a 20×20 grid like the other tab icons, filled even-odd (F0): the pad with its two grips, a cross on the
    // left and two buttons on the right cut out of it.
    public const string PathData =
        "F0 M6,5 L14,5 C16.9,5 18.7,7.4 19.2,10.6 L19.6,13.6 C19.9,15.6 18.7,17 17.2,17 C16.2,17 15.5,16.4 14.9,15.5 L13.9,14 " +
        "L6.1,14 L5.1,15.5 C4.5,16.4 3.8,17 2.8,17 C1.3,17 0.1,15.6 0.4,13.6 L0.8,10.6 C1.3,7.4 3.1,5 6,5 Z " +
        "M5.2,7.6 L6.8,7.6 L6.8,9.2 L8.4,9.2 L8.4,10.8 L6.8,10.8 L6.8,12.4 L5.2,12.4 L5.2,10.8 L3.6,10.8 L3.6,9.2 L5.2,9.2 Z " +
        "M13.4,7.9 A1.05,1.05 0 1 1 13.39,7.9 Z M15.6,10.1 A1.05,1.05 0 1 1 15.59,10.1 Z";

    public static Geometry Data { get; } = CreateData();

    private static Geometry CreateData()
    {
        var geometry = Geometry.Parse(PathData);
        geometry.Freeze();
        return geometry;
    }
}
