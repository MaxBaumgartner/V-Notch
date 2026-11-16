using System.Windows;
using VNotch.Services.Displays;
using Xunit;

namespace VNotch.Tests;

public sealed class DisplayChangesTests
{
    private static readonly DisplayMode Original = new(2560, 1440, 60);
    private static readonly DisplayMode Changed = new(1920, 1080, 144);

    [Fact]
    public void UnconfirmedChangeRevertsAfter15s()
    {
        var api = new FakeDisplayApi();
        var clock = new ManualClock();
        var changes = new DisplayChanges(api, () => clock.Now);
        int reverted = 0;
        changes.Reverted += () => reverted++;
        Assert.True(changes.Apply(DisplayChangeKind.Mode, a => a.SetModes(Modes(Changed))));
        Assert.True(changes.NeedsConfirm);
        Assert.Equal(15, changes.SecondsLeft);
        clock.Advance(14.1);
        changes.Tick();
        Assert.Equal(1, changes.SecondsLeft);
        Assert.Equal(Changed, api.Displays[0].Mode);
        clock.Advance(0.9);
        changes.Tick();
        Assert.Equal(Original, api.Displays[0].Mode);
        Assert.False(changes.NeedsConfirm);
        Assert.Equal(0, changes.SecondsLeft);
        Assert.Equal(1, reverted);
        changes.Tick();
        Assert.Equal(1, reverted);
    }

    [Fact]
    public void ConfirmKeepsTheChange()
    {
        var api = new FakeDisplayApi();
        var clock = new ManualClock();
        var changes = new DisplayChanges(api, () => clock.Now);
        changes.Apply(DisplayChangeKind.Mode, a => a.SetModes(Modes(Changed)));
        changes.Confirm();
        clock.Advance(20);
        changes.Tick();
        Assert.False(changes.NeedsConfirm);
        Assert.Equal(Changed, api.Displays[0].Mode);
    }

    [Fact]
    public void SecondChangeKeepsTheFirstSnapshot()
    {
        var api = new FakeDisplayApi();
        var clock = new ManualClock();
        var changes = new DisplayChanges(api, () => clock.Now);
        changes.Apply(DisplayChangeKind.Mode, a => a.SetModes(Modes(Changed)));
        clock.Advance(10);
        changes.Apply(DisplayChangeKind.Mode, a => a.SetModes(Modes(Changed with { RefreshHz = 120 })));
        Assert.Equal(15, changes.SecondsLeft);
        clock.Advance(15);
        changes.Tick();
        Assert.Equal(Original, api.Displays[0].Mode);
    }

    [Fact]
    public void RevertSkipsMonitorThatWentAway()
    {
        var api = new FakeDisplayApi();
        api.Displays.Add(Screen("second", false) with { Bounds = new Int32Rect(2560, 0, 2560, 1440) });
        var clock = new ManualClock();
        var changes = new DisplayChanges(api, () => clock.Now);
        changes.Apply(DisplayChangeKind.Mode, a => a.SetModes(Modes(Changed)));
        api.Displays.RemoveAt(1);
        api.Writes.Clear();
        clock.Advance(15);
        changes.Tick();
        Assert.DoesNotContain("second", api.Writes);
        Assert.Equal(Original, api.Displays[0].Mode);
    }

    [Fact]
    public void BrightnessHdrZoomNeedNoConfirm()
    {
        var api = new FakeDisplayApi();
        var changes = new DisplayChanges(api, () => DateTime.UnixEpoch);
        Assert.True(changes.Apply(DisplayChangeKind.Brightness, a => a.SetBrightness("main", 40)));
        Assert.True(changes.Apply(DisplayChangeKind.Hdr, a => a.SetHdr("main", true)));
        Assert.True(changes.Apply(DisplayChangeKind.Zoom, a => a.SetZoom("main", 125)));
        Assert.False(changes.NeedsConfirm);
        Assert.Equal(40, api.GetBrightness("main"));
        Assert.True(api.Displays[0].HdrOn);
        Assert.Equal(125, api.Displays[0].ZoomPercent);
    }

    [Fact]
    public void RefusesToTurnOffTheLastActiveMonitor()
    {
        var api = new FakeDisplayApi();
        var changes = new DisplayChanges(api, () => DateTime.UnixEpoch);
        Assert.False(changes.Apply(DisplayChangeKind.Active, a => a.SetActive("main", false)));
        Assert.True(api.Displays[0].Active);
        Assert.Empty(api.Writes);
        Assert.False(changes.NeedsConfirm);
    }

    [Fact]
    public void FailedWriteDoesNotStartConfirmation()
    {
        var api = new FakeDisplayApi { FailModes = true };
        var changes = new DisplayChanges(api, () => DateTime.UnixEpoch);
        Assert.False(changes.Apply(DisplayChangeKind.Mode, a => a.SetModes(Modes(Changed))));
        Assert.False(changes.NeedsConfirm);
    }

    [Fact]
    public void PartialFailureRestoresTheStateBeforeThisApply()
    {
        var api = new FakeDisplayApi();
        api.Displays.Add(Screen("second", false));
        var changes = new DisplayChanges(api, () => DateTime.UnixEpoch);
        Assert.False(changes.Apply(DisplayChangeKind.Active, a =>
        {
            a.SetActive("second", false);
            a.SetActive("main", false);
        }));
        Assert.All(api.Displays, d => Assert.True(d.Active));
        Assert.False(changes.NeedsConfirm);
    }

    [Fact]
    public void RiskyWriteStillNeedsConfirmationIfCallerSuppliesDirectKind()
    {
        var api = new FakeDisplayApi();
        var changes = new DisplayChanges(api, () => DateTime.UnixEpoch);
        Assert.True(changes.Apply(DisplayChangeKind.Brightness, a => a.SetModes(Modes(Changed))));
        Assert.True(changes.NeedsConfirm);
    }

    [Fact]
    public void FailedSecondChangePreservesPendingSnapshotAndDeadline()
    {
        var api = new FakeDisplayApi();
        var clock = new ManualClock();
        var changes = new DisplayChanges(api, () => clock.Now);
        changes.Apply(DisplayChangeKind.Mode, a => a.SetModes(Modes(Changed)));
        clock.Advance(10);
        api.FailModes = true;
        Assert.False(changes.Apply(DisplayChangeKind.Mode, a => a.SetModes(Modes(Original))));
        Assert.Equal(5, changes.SecondsLeft);
        api.FailModes = false;
        clock.Advance(5);
        changes.Tick();
        Assert.Equal(Original, api.Displays[0].Mode);
    }

    [Fact]
    public void RevertRestoresActiveStateLayoutAndPrimary()
    {
        var api = new FakeDisplayApi();
        api.Displays.Add(Screen("second", false) with { Bounds = new Int32Rect(2560, 0, 2560, 1440) });
        var initial = api.List().ToArray();
        var clock = new ManualClock();
        var changes = new DisplayChanges(api, () => clock.Now);
        Assert.True(changes.Apply(DisplayChangeKind.Primary, a => a.SetPrimary("second")));
        Assert.True(changes.Apply(DisplayChangeKind.Position, a => a.SetPositions(
            new Dictionary<string, Int32Point> { ["main"] = new(-2560, 1440) })));
        Assert.True(changes.Apply(DisplayChangeKind.Active, a => a.SetActive("main", false)));
        clock.Advance(15);
        changes.Tick();
        Assert.Equal(initial, api.List());
    }

    [Fact]
    public void DirectChangeDoesNotExtendPendingConfirmation()
    {
        var api = new FakeDisplayApi();
        var clock = new ManualClock();
        var changes = new DisplayChanges(api, () => clock.Now);
        changes.Apply(DisplayChangeKind.Mode, a => a.SetModes(Modes(Changed)));
        clock.Advance(10);
        changes.Apply(DisplayChangeKind.Hdr, a => a.SetHdr("main", true));
        Assert.Equal(5, changes.SecondsLeft);
        clock.Advance(5);
        changes.Tick();
        Assert.Equal(Original, api.Displays[0].Mode);
        Assert.True(api.Displays[0].HdrOn);
    }

    private static Dictionary<string, DisplayMode> Modes(DisplayMode mode) => new() { ["main"] = mode };
    private static DisplayInfo Screen(string id, bool primary) => new(id, id, id, true, primary,
        new Int32Rect(0, 0, 2560, 1440), Original, new[] { Original, Changed }, true, false, 100, new[] { 100, 125 }, false);

    private sealed class ManualClock
    {
        public DateTime Now { get; private set; } = DateTime.UnixEpoch;
        public void Advance(double seconds) => Now = Now.AddTicks((long)Math.Round(seconds * TimeSpan.TicksPerSecond));
    }

    private sealed class FakeDisplayApi : IDisplayApi
    {
        public List<DisplayInfo> Displays { get; } = new() { Screen("main", true) };
        public List<string> Writes { get; } = new();
        public bool FailModes { get; set; }
        private int _brightness = 50;
        public IReadOnlyList<DisplayInfo> List() => Displays.ToArray();
        private bool Update(string id, Func<DisplayInfo, DisplayInfo> change)
        {
            int index = Displays.FindIndex(d => d.Id == id);
            if (index < 0) throw new InvalidOperationException("A removed display was written.");
            Writes.Add(id);
            Displays[index] = change(Displays[index]);
            return true;
        }
        public bool SetModes(IReadOnlyDictionary<string, DisplayMode> modes)
        {
            if (FailModes) return false;
            foreach (var (id, mode) in modes) Update(id, d => d with { Mode = mode });
            return true;
        }
        public bool SetPositions(IReadOnlyDictionary<string, Int32Point> positions)
        {
            foreach (var (id, point) in positions)
                if (Displays.Any(d => d.Id == id && d.Primary) && point != new Int32Point(0, 0)) return false;
            foreach (var (id, point) in positions)
                Update(id, d => d with { Bounds = new Int32Rect(point.X, point.Y, d.Bounds.Width, d.Bounds.Height) });
            return true;
        }
        public bool SetPrimary(string id)
        {
            var next = Displays.First(d => d.Id == id);
            foreach (var display in Displays.ToArray()) Update(display.Id, d => d with
            {
                Primary = d.Id == id,
                Bounds = new Int32Rect(d.Bounds.X - next.Bounds.X, d.Bounds.Y - next.Bounds.Y, d.Bounds.Width, d.Bounds.Height)
            });
            return true;
        }
        public bool SetActive(string id, bool on) => Update(id, d => d with { Active = on });
        public bool SetHdr(string id, bool on) => Update(id, d => d with { HdrOn = on });
        public bool SetZoom(string id, int percent) => Update(id, d => d with { ZoomPercent = percent });
        public int? GetBrightness(string id) => _brightness;
        public bool SetBrightness(string id, int percent) { _brightness = percent; return true; }
    }
}
