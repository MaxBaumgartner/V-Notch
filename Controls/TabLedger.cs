namespace VNotch.Controls;

/// <summary>
/// What a tab last put on screen, so a tab that comes back after a while moves only the values that changed
/// while nobody was looking. Nothing is recorded while the tab is hidden: the first pass after it returns compares
/// against what the user actually saw, and a lamp switched in another room blooms once, instead of every value
/// replaying its arrival on every open. A tab that was away for less than <see cref="Away"/> (a quick flick to the
/// next tab and back) takes the new values silently.
/// </summary>
internal sealed class TabLedger
{
    /// <summary>How long a tab has to be hidden before its changes count as news.</summary>
    public static readonly TimeSpan Away = TimeSpan.FromSeconds(3);

    /// <summary>How long after a quick return the first pass keeps quiet.</summary>
    private static readonly TimeSpan QuietPass = TimeSpan.FromMilliseconds(600);

    private readonly Dictionary<string, string> _shown = new(StringComparer.Ordinal);
    private readonly Func<bool> _isShown;
    private readonly Func<DateTime> _now;
    private DateTime _hiddenAt = DateTime.MinValue;
    private DateTime _quietUntil = DateTime.MinValue;

    public TabLedger(Func<bool> isShown, Func<DateTime>? now = null)
    {
        _isShown = isShown;
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Call when the tab leaves the screen.</summary>
    public void Hidden() => _hiddenAt = _now();

    /// <summary>Call when the tab comes back, before its first refresh.</summary>
    public void Shown()
    {
        DateTime now = _now();
        bool quick = _hiddenAt != DateTime.MinValue && now - _hiddenAt < Away;
        _quietUntil = quick ? now + QuietPass : DateTime.MinValue;
    }

    /// <summary>
    /// Records <paramref name="value"/> under <paramref name="key"/> and says whether it should move: true only
    /// when the tab is on screen, has shown this key before, the value differs from what it showed, and the tab is
    /// not in the quiet pass after a quick return. While the tab is hidden nothing is recorded and the answer is false.
    /// </summary>
    public bool Changed(string key, string value)
    {
        if (!_isShown()) return false;
        bool seen = _shown.TryGetValue(key, out string? before);
        _shown[key] = value;
        if (!seen || before == value) return false;
        return _now() >= _quietUntil;
    }

    /// <inheritdoc cref="Changed(string, string)"/>
    public bool Changed(string key, double value, string format = "0.##") =>
        Changed(key, value.ToString(format, System.Globalization.CultureInfo.InvariantCulture));

    /// <inheritdoc cref="Changed(string, string)"/>
    public bool Changed(string key, bool value) => Changed(key, value ? "1" : "0");

    /// <summary>Forgets one key (a row that left the list) so it counts as new when it comes back.</summary>
    public void Forget(string key) => _shown.Remove(key);

    /// <summary>Forgets every key that starts with <paramref name="prefix"/>.</summary>
    public void ForgetAll(string prefix)
    {
        foreach (string key in _shown.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            _shown.Remove(key);
    }
}
