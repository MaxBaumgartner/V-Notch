using System.Windows.Media;

namespace VNotch.Controls;

/// <summary>
/// A smart-home house, for the tab that shows the user's Raspberry Pi dashboard (Home Assistant
/// switches, school, reminders), in the nav bar and the tab settings. Drawn as an outline with a
/// signal inside so it reads differently from the media tab's solid house.
/// </summary>
public static class PiGlyph
{
    // On a 20×20 grid like the other tab icons, filled even-odd (F0): the house outline
    // (outer minus inner shape), then a dot and one signal arc inside it.
    public const string PathData =
        "F0 M10,1.8 L18.6,9 L17,9 L17,17.8 L3,17.8 L3,9 L1.4,9 Z " +
        "M10,4.4 L15.2,8.8 L15.2,16 L4.8,16 L4.8,8.8 Z " +
        "M8.9,13.6 A1.1,1.1 0 1 1 11.1,13.6 A1.1,1.1 0 1 1 8.9,13.6 Z " +
        "M7.45,11.05 A3.6,3.6 0 0 1 12.55,11.05 L11.56,12.04 A2.2,2.2 0 0 0 8.44,12.04 Z";

    public static Geometry Data { get; } = CreateData();

    private static Geometry CreateData()
    {
        var geometry = Geometry.Parse(PathData);
        geometry.Freeze();
        return geometry;
    }
}
