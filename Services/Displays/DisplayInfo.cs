using System.Windows;

namespace VNotch.Services.Displays;

/// <summary>A supported display mode, in physical pixels and whole hertz.</summary>
public sealed record DisplayMode(int Width, int Height, int RefreshHz);

/// <summary>A desktop position in physical pixels.</summary>
public readonly record struct Int32Point(int X, int Y);

/// <summary>A connected display, including connected displays currently switched off.</summary>
public sealed record DisplayInfo(string Id, string Name, string DeviceName, bool Active,
    bool Primary, Int32Rect Bounds, DisplayMode Mode, IReadOnlyList<DisplayMode> Modes,
    bool HdrSupported, bool HdrOn, int ZoomPercent, IReadOnlyList<int> ZoomSteps, bool Internal);
