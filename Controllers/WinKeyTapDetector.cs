namespace VNotch.Controllers;

internal enum WinKeyTapAction { PassThrough, Tap }

/// <summary>
/// Recognises a lone tap of a Windows key: pressed and released with no other key in between.
/// Combinations such as Win+E or Win+Shift+S stay with Windows. Runs on the keyboard hook thread.
/// </summary>
internal sealed class WinKeyTapDetector
{
    internal const uint VkLWin = 0x5B;
    internal const uint VkRWin = 0x5C;

    private bool _winDown;
    private bool _otherKey;

    internal static bool IsWinKey(uint vk) => vk is VkLWin or VkRWin;

    internal WinKeyTapAction Process(uint vk, bool isDown)
    {
        if (IsWinKey(vk))
        {
            if (isDown)
            {
                // Auto-repeat keeps sending key-downs while the key is held.
                if (!_winDown) { _winDown = true; _otherKey = false; }
                return WinKeyTapAction.PassThrough;
            }
            bool tap = _winDown && !_otherKey;
            _winDown = false;
            _otherKey = false;
            return tap ? WinKeyTapAction.Tap : WinKeyTapAction.PassThrough;
        }
        if (_winDown && isDown) _otherKey = true;
        return WinKeyTapAction.PassThrough;
    }

    /// <summary>A mouse click while Win is held is a combination too (Win+click), not a tap.</summary>
    internal void NoteOtherInput()
    {
        if (_winDown) _otherKey = true;
    }
}
