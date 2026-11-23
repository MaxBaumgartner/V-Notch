using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using VNotch.Services;

namespace VNotch.Controls;

public partial class ControlCenterView
{
    #region Claude in the notch

    private Func<bool>? _claudeInNotch;
    private Action<bool>? _setClaudeInNotch;

    /// <summary>The Claude page's eye button reads and flips whether Claude Code shows in the notch (the shell owns it).</summary>
    internal void AttachNotchSwitch(Func<bool> inNotch, Action<bool> setInNotch)
    {
        _claudeInNotch = inNotch;
        _setClaudeInNotch = setInNotch;
        _claudeKey = "";
    }

    /// <summary>Called when the setting changed elsewhere (settings window): the eye follows.</summary>
    internal void ClaudeNotchSwitchChanged()
    {
        _claudeKey = "";
        BuildClaudeSection();
    }

    private Button? _claudeEye;
    private TextBlock? _claudeEyeGlyph;

    /// <summary>
    /// The eye in the face's top right corner, above the turn's timer: open while sessions show in the closed notch,
    /// crossed out (and dimmed) while they stay in this tab.
    /// </summary>
    private void PaintClaudeNotchButton()
    {
        if (_claudeInNotch == null || _claudeFace == null) return;
        if (_claudeEye == null || _claudeEyeGlyph == null)
        {
            _claudeEyeGlyph = new TextBlock
            {
                FontFamily = FluentFont, FontSize = 13, Foreground = AccountWhite,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            _claudeEye = AccountButton(_claudeEyeGlyph, 30, 26, 13);
            _claudeEye.Click += (_, _) =>
            {
                _setClaudeInNotch?.Invoke(!_claudeInNotch());
                ClaudeNotchSwitchChanged();
            };
            PlaceAccount(_accountFront, _claudeEye, ClaudeFaceWidth - 24 - 30, 3);
        }
        bool on = _claudeInNotch();
        string tip = Loc.Get(on ? "control.claude.notchOn" : "control.claude.notchOff");
        _claudeEyeGlyph.Text = on ? "\uE7B3" : "\uED1A";
        _claudeEyeGlyph.Opacity = on ? 1 : .6;
        _claudeEye.ToolTip = tip;
        AutomationProperties.SetName(_claudeEye, tip);
    }

    #endregion
}
