using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VNotch.Services;
using VNotch.Services.Phone;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    private Func<PhoneInfo?>? _phone;
    private Action? _openPhone;
    private Action? _prewarmPhone;

    /// <summary>The connected phone sits right under the System header: name, battery and a button that shows its screen.</summary>
    internal void AttachPhone(Func<PhoneInfo?> phone, Action open, Action prewarm)
    {
        _phone = phone;
        _openPhone = open;
        _prewarmPhone = prewarm;
    }

    internal void RefreshPhone()
    {
        if (!_disposed && Page == ControlCenterPage.System && IsVisible) BuildStatsSection();
    }

    private void AddPhoneRow(Panel target)
    {
        if (_phone?.Invoke() is not { } phone || phone.State == PhoneState.Offline) return;
        bool ready = phone.State == PhoneState.Device;
        var glyph = new TextBlock
        {
            Text = "\uE8EA", FontFamily = SegoeSymbolFont, FontSize = 13, Foreground = _moodInk,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        };
        var name = MakeLabel(phone.Name ?? Loc.Get("phone.name"), 12, UiPalette.PrimaryBrush, 200);
        var status = MakeLabel(ready ? MainWindow.PhoneStatus(phone) : Loc.Get("phone.allow"), 11, Muted);
        status.Margin = new Thickness(8, 0, 0, 0);

        var row = new DockPanel { Margin = new Thickness(0, 6, 0, 4), LastChildFill = false };
        if (ready)
        {
            var label = MakeLabel(Loc.Get("phone.mirror"), 11, _moodInk);
            var button = new Border
            {
                Background = Wash(_mood, .25), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 10, 5),
                Cursor = Cursors.Hand, Child = label, Name = "PhoneMirrorButton"
            };
            Squish(button);
            // Hovering starts scrcpy already, so the click shows the screen at once.
            button.MouseEnter += (_, _) => _prewarmPhone?.Invoke();
            button.MouseLeftButtonUp += (_, e) => { e.Handled = true; _openPhone?.Invoke(); };
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
        }
        row.Children.Add(glyph);
        row.Children.Add(name);
        row.Children.Add(status);
        target.Children.Add(row);
    }
}
