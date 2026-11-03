using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.ControlCenter;

namespace VNotch.Controls;

/// <summary>
/// The laptop on the <see cref="ControlCenterPage.System"/> page, like OMEN Gaming Hub: performance mode,
/// temperature and fans, maximum fans and the keyboard light, through the OMEN helper (see <see cref="LaptopControl"/>).
/// </summary>
public partial class ControlCenterView
{
    #region Laptop

    private static readonly string[] LaptopModes = ["eco", "balanced", "performance"];
    // A few colours to pick for a keyboard with colour zones.
    private static readonly string[] KeyboardColors = ["#FFFFFF", "#FF3B30", "#30D158", "#0A84FF", "#BF5AF2"];

    private readonly StackPanel _laptopSection = new();
    private string _laptopKey = "";
    private TextBlock? _laptopReadings;

    private void BuildLaptopSectionIfChanged(LaptopStatus? laptop)
    {
        string key = laptop == null
            ? $"none|{IsExpanded("laptop")}"
            : $"{laptop.Mode}|{laptop.MaxFan}|{laptop.KeyboardLight}|{laptop.KeyboardZones}|{laptop.KeyboardColor}|{IsExpanded("laptop")}";
        if (key != _laptopKey)
        {
            _laptopKey = key;
            BuildLaptopSection(laptop);
        }
        // Temperature and fans change all the time: only the line moves.
        if (_laptopReadings != null && laptop != null) _laptopReadings.Text = Readings(laptop);
    }

    private void BuildLaptopSection(LaptopStatus? laptop)
    {
        _laptopSection.Children.Clear();
        _laptopReadings = null;
        _laptopSection.Children.Add(BuildSectionHeader("laptop", Loc.Get("control.laptop"), Loc.Get("control.status"), null, topMargin: 14));
        if (!IsExpanded("laptop")) return;
        if (laptop == null)
        {
            _laptopSection.Children.Add(NewRow("", Loc.Get("control.laptop.noHelper"), muted: true));
            return;
        }

        var modeRow = NewRow("", Loc.Get("control.laptop.mode"));
        var modes = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (string mode in LaptopModes)
        {
            string chosen = mode;
            modes.Children.Add(TextButton(Loc.Get("control.laptop." + mode), accent: laptop.Mode == mode,
                () => LaptopAction(() => LaptopControl.SetMode(chosen))));
        }
        AddAction(modeRow, modes);
        _laptopSection.Children.Add(modeRow);

        var sensorRow = NewRow("", Loc.Get("control.laptop.sensors"));
        _laptopReadings = MakeLabel(Readings(laptop), 12, Muted);
        _laptopReadings.Margin = new Thickness(0, 0, 10, 0);
        Grid.SetColumn(_laptopReadings, 1);
        Grid.SetColumnSpan(_laptopReadings, 2);
        sensorRow.Children.Add(_laptopReadings);
        _laptopSection.Children.Add(sensorRow);

        if (laptop.MaxFan is { } maxFan)
        {
            var fanRow = NewRow("", Loc.Get("control.laptop.maxFan"));
            AddStatus(fanRow, "", Muted, Toggle(maxFan, on => LaptopAction(() => LaptopControl.SetMaxFan(on))));
            _laptopSection.Children.Add(fanRow);
        }

        if (laptop.KeyboardLight is { } light)
        {
            var keyboardRow = NewRow("", Loc.Get("control.laptop.keyboard"));
            AddStatus(keyboardRow, "", Muted, Toggle(light, on => LaptopAction(() => LaptopControl.SetKeyboardLight(on))));
            if (laptop.KeyboardZones > 0)
            {
                var swatches = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
                foreach (string color in KeyboardColors)
                    swatches.Children.Add(ColorSwatch(color, string.Equals(color, laptop.KeyboardColor, StringComparison.OrdinalIgnoreCase)));
                AddAction(keyboardRow, swatches);
            }
            _laptopSection.Children.Add(keyboardRow);
        }
    }

    /// <summary>"52 °C · CPU 3100 · GPU 2900 U/min"; what the BIOS did not say is left out.</summary>
    internal static string Readings(LaptopStatus laptop)
    {
        string temp = laptop.Temperature is { } t ? $"{t} °C" : "";
        string fans = laptop.CpuFanRpm is { } cpu && laptop.GpuFanRpm is { } gpu
            ? Loc.Get("control.laptop.fans", cpu.ToString("N0", Loc.GetCulture()), gpu.ToString("N0", Loc.GetCulture()))
            : "";
        return temp.Length > 0 && fans.Length > 0 ? $"{temp} · {fans}" : temp + fans;
    }

    private Border ColorSwatch(string hex, bool current)
    {
        var swatch = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = (Brush)new BrushConverter().ConvertFromInvariantString(hex)!,
            BorderBrush = current ? VNotch.Services.UiPalette.PrimaryBrush : ComboBorder,
            BorderThickness = new Thickness(current ? 2 : 1),
            Margin = new Thickness(6, 0, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = hex
        };
        swatch.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            LaptopAction(() => LaptopControl.SetKeyboardColor(hex));
        };
        return swatch;
    }

    /// <summary>The BIOS takes a moment: ask off the UI thread, then read the laptop again.</summary>
    private void LaptopAction(Func<bool> action)
    {
        Task.Run(action);
        RefreshSoon(600);
    }

    #endregion
}
