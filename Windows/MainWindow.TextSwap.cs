using System;
using System.Collections.Generic;
using System.Windows.Controls;
using VNotch.Services;
using static VNotch.Services.AnimationPrimitives;

namespace VNotch;

public partial class MainWindow
{
    private readonly Dictionary<TextBlock, (string Text, int Token)> _textSwaps = new();
    private int _textSwapToken;

    /// <summary>The drag label fades out, changes its words, and fades back in.</summary>
    private void CrossFadeText(TextBlock target, string text, int ms = 120)
    {
        if (!AnimationConfig.ReduceMotion && _textSwaps.TryGetValue(target, out var pending) && pending.Text == text)
            return;

        int token = ++_textSwapToken;
        double opacity = target.Opacity;
        target.BeginAnimation(OpacityProperty, null);
        target.Opacity = 1;
        _textSwaps.Remove(target);
        if (AnimationConfig.ReduceMotion || target.Text == text)
        {
            target.Text = text;
            return;
        }

        string previous = target.Text;
        _textSwaps[target] = (text, token);
        var half = TimeSpan.FromMilliseconds(ms / 2.0);
        var fadeOut = MakeAnim(opacity, 0d, half, _easeQuadOut);
        fadeOut.Completed += (_, _) =>
        {
            if (!_textSwaps.TryGetValue(target, out var current) || current.Token != token) return;
            // A new pill may have replaced the label while the drag was fading out.
            if (target.Text != previous)
            {
                _textSwaps.Remove(target);
                target.BeginAnimation(OpacityProperty, null);
                return;
            }
            target.Text = text;
            var fadeIn = MakeAnim(0d, 1d, half, _easeQuadOut);
            fadeIn.Completed += (_, _) =>
            {
                if (!_textSwaps.TryGetValue(target, out var latest) || latest.Token != token) return;
                _textSwaps.Remove(target);
                target.BeginAnimation(OpacityProperty, null);
            };
            target.BeginAnimation(OpacityProperty, fadeIn);
        };
        target.BeginAnimation(OpacityProperty, fadeOut);
    }
}
