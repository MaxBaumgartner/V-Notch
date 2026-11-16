using System.Windows;

namespace VNotch.Services.Displays;

/// <summary>Identifies which display setting the user is changing.</summary>
public enum DisplayChangeKind { Mode, Position, Primary, Active, Hdr, Zoom, Brightness }

/// <summary>Restores the previous layout after 15 seconds unless the user keeps it.</summary>
public sealed class DisplayChanges(IDisplayApi api, Func<DateTime> now)
{
    private DisplayInfo[]? _snapshot;
    private DateTime _deadline;

    public bool NeedsConfirm => _snapshot != null;
    public int SecondsLeft => NeedsConfirm ? Math.Max(0, (int)Math.Ceiling((_deadline - now()).TotalSeconds)) : 0;
    public event Action? Reverted;

    /// <summary>Applies a setting; a failed or unsafe write is reported without starting a countdown.</summary>
    public bool Apply(DisplayChangeKind kind, Action<IDisplayApi> change)
    {
        Tick();
        DisplayInfo[] before;
        try { before = api.List().ToArray(); }
        catch { return false; }

        var guarded = new GuardedApi(api);
        try { change(guarded); }
        catch { guarded.Failed = true; }
        if (guarded.Failed || !guarded.Wrote)
        {
            if (guarded.Wrote) Restore(before);
            return false;
        }

        if (guarded.Risky || kind is DisplayChangeKind.Mode or DisplayChangeKind.Position
            or DisplayChangeKind.Primary or DisplayChangeKind.Active)
        {
            _snapshot ??= before;
            _deadline = now().AddSeconds(15);
        }
        return true;
    }

    /// <summary>Keeps the current display settings and hides the countdown.</summary>
    public void Confirm() => _snapshot = null;

    /// <summary>Restores an unconfirmed change when the countdown expires.</summary>
    public void Tick()
    {
        if (_snapshot == null || now() < _deadline) return;
        var snapshot = _snapshot;
        _snapshot = null;
        Restore(snapshot);
        Reverted?.Invoke();
    }

    private void Restore(IReadOnlyList<DisplayInfo> snapshot)
    {
        HashSet<string> present;
        try { present = api.List().Select(d => d.Id).ToHashSet(StringComparer.Ordinal); }
        catch { return; }
        var remaining = snapshot.Where(d => present.Contains(d.Id)).ToArray();

        // Restore usable screens before turning any others off, including a former primary.
        foreach (var display in remaining.Where(d => d.Active))
            Attempt(() => api.SetActive(display.Id, true));
        var active = remaining.Where(d => d.Active).ToArray();
        if (active.Length > 0)
        {
            // Windows keeps the primary at (0,0); restore that origin before absolute positions.
            var primary = active.FirstOrDefault(d => d.Primary);
            if (primary != null) Attempt(() => api.SetPrimary(primary.Id));
            Attempt(() => api.SetModes(active.ToDictionary(d => d.Id, d => d.Mode)));
            Attempt(() => api.SetPositions(active.ToDictionary(d => d.Id, d => new Int32Point(d.Bounds.X, d.Bounds.Y))));
        }
        foreach (var display in remaining.Where(d => !d.Active))
        {
            Attempt(() =>
            {
                var current = api.List();
                return current.Count(d => d.Active) > 1 || !current.Any(d => d.Id == display.Id && d.Active)
                    ? api.SetActive(display.Id, false) : false;
            });
        }
    }

    private static void Attempt(Func<bool> write)
    {
        try { write(); }
        catch { /* A disconnected monitor must not prevent the other monitors being restored. */ }
    }

    // Action<IDisplayApi> discards return values, so record them at the API boundary.
    private sealed class GuardedApi(IDisplayApi inner) : IDisplayApi
    {
        public bool Wrote { get; private set; }
        public bool Risky { get; private set; }
        public bool Failed { get; set; }
        private bool Write(Func<bool> action, bool risky = false)
        {
            if (Failed) return false;
            bool success = action();
            Failed |= !success;
            Wrote |= success;
            Risky |= risky && success;
            return success;
        }
        public IReadOnlyList<DisplayInfo> List() => inner.List();
        public int? GetBrightness(string id) => inner.GetBrightness(id);
        public bool SetModes(IReadOnlyDictionary<string, DisplayMode> modes) => Write(() => inner.SetModes(modes), true);
        public bool SetPositions(IReadOnlyDictionary<string, Int32Point> topLefts) => Write(() => inner.SetPositions(topLefts), true);
        public bool SetPrimary(string id) => Write(() => inner.SetPrimary(id), true);
        public bool SetActive(string id, bool on) => Write(() =>
        {
            var displays = inner.List();
            var display = displays.FirstOrDefault(d => d.Id == id);
            if (display == null || (!on && display.Active && displays.Count(d => d.Active) <= 1)) return false;
            return inner.SetActive(id, on);
        }, true);
        public bool SetHdr(string id, bool on) => Write(() => inner.SetHdr(id, on));
        public bool SetZoom(string id, int percent) => Write(() => inner.SetZoom(id, percent));
        public bool SetBrightness(string id, int percent) => Write(() => inner.SetBrightness(id, percent));
    }
}
