using System;
using System.Windows;
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

    /// <summary>An open eye while sessions show in the closed notch; crossed out (and dimmed) while they stay in this tab.</summary>
    private FrameworkElement? ClaudeNotchButton()
    {
        if (_claudeInNotch == null) return null;
        bool on = _claudeInNotch();
        var eye = IconButton(on ? "" : "", Loc.Get(on ? "control.claude.notchOn" : "control.claude.notchOff"), () =>
        {
            _setClaudeInNotch?.Invoke(!_claudeInNotch());
            ClaudeNotchSwitchChanged();
        });
        eye.Width = eye.Height = 24;
        eye.Margin = new Thickness(0, 0, 8, 0);
        eye.Opacity = on ? 1 : 0.6;
        return eye;
    }

    #endregion
}
