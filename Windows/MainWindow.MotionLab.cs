using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VNotch.Services;

namespace VNotch;

/// <summary>
/// Testing aids for Tools/NotchMotion, off unless their environment variable is set:
/// VNOTCH_AUTOTOGGLE=&lt;ms&gt; opens and closes the notch on its own, so its motion can be filmed without moving
/// the user's mouse; VNOTCH_INTERRUPT_MS=&lt;ms&gt; closes each opening again after that delay; VNOTCH_NO_MEDIA=1
/// ignores every player, so the slim idle notch can be filmed while music plays; VNOTCH_RENDER_PROBE=1 logs every
/// few seconds what keeps WPF drawing (Rendering handlers, running animation clocks), to find loops that burn CPU
/// while nothing is on screen.
/// </summary>
public partial class MainWindow
{
    private static readonly bool MotionLabNoMedia = Environment.GetEnvironmentVariable("VNOTCH_NO_MEDIA") == "1";

    private DispatcherTimer? _autoToggleTimer;
    private DispatcherTimer? _renderProbeTimer;

    private void StartMotionLab()
    {
        if (Environment.GetEnvironmentVariable("VNOTCH_RENDER_PROBE") == "1")
        {
            _renderProbeTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _renderProbeTimer.Tick += (_, _) => LogRenderDrivers();
            _renderProbeTimer.Start();
            WatchDispatcher();
        }
        if (!double.TryParse(Environment.GetEnvironmentVariable("VNOTCH_AUTOTOGGLE"), NumberStyles.Float, CultureInfo.InvariantCulture, out double toggleMs) || toggleMs < 200)
            return;
        bool interrupt = double.TryParse(Environment.GetEnvironmentVariable("VNOTCH_INTERRUPT_MS"), NumberStyles.Float, CultureInfo.InvariantCulture, out double interruptMs) && interruptMs > 0;
        var closeAgain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(interrupt ? interruptMs : 1000) };
        closeAgain.Tick += (_, _) => { closeAgain.Stop(); CollapseNotch(); };
        _autoToggleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(toggleMs) };
        _autoToggleTimer.Tick += (_, _) =>
        {
            if (_isExpanded) { CollapseNotch(); return; }
            ExpandNotch();
            if (interrupt) closeAgain.Start();
        };
        _autoToggleTimer.Start();
    }

    /// <summary>
    /// Reads WPF's internals by reflection (test aid only): the handlers on the render loop and every animation clock
    /// that is still running. Any running clock makes WPF draw every window on this thread at the display's rate.
    /// </summary>
    private void LogRenderDrivers()
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        try
        {
            var contextType = typeof(Visual).Assembly.GetType("System.Windows.Media.MediaContext")!;
            object context = contextType.GetMethod("From", Any)!.Invoke(null, new object[] { Dispatcher })!;
            var lines = new List<string>();
            object? timeManager = null;
            foreach (var field in contextType.GetFields(Any))
            {
                object? value = field.GetValue(context);
                if (value is Delegate handlers && field.Name.Contains("Rendering", StringComparison.Ordinal))
                    foreach (var handler in handlers.GetInvocationList())
                        lines.Add($"Rendering handler {handler.Method.DeclaringType?.Name}.{handler.Method.Name}");
                if (field.FieldType.Name == "TimeManager") timeManager = value;
            }
            if (timeManager != null)
                foreach (var field in timeManager.GetType().GetFields(Any))
                    if (field.GetValue(timeManager) is Clock root)
                        CollectRunningClocks(root, lines);
            var shown = new List<string>();
            foreach (var (name, element) in new (string, System.Windows.UIElement?)[]
                     { ("Media", ExpandedContent), ("System", TimerContent), ("Home", PiContent), ("School", SchoolContent),
                       ("Claude", ClaudeContent), ("Weather", WeatherContent), ("Audio", AudioContent), ("Shelf", SecondaryContent) })
                if (element?.IsVisible == true) shown.Add(name);
            RuntimeLog.Info("RENDER-PROBE", $"expanded={_isExpanded} visible tabs=[{string.Join(",", shown)}] {lines.Count} driver(s): " + string.Join(" || ", lines));
            RuntimeLog.Info("RENDER-PROBE", "dispatcher, last 5 s: " + TakeDispatcherStats());
        }
        catch (Exception ex) when (ex is TargetInvocationException or NullReferenceException or InvalidOperationException)
        {
            RuntimeLog.Warn("RENDER-PROBE", "Probe failed: " + ex.GetType().Name);
        }
    }

    private readonly Dictionary<string, (int Count, double Ms)> _dispatcherStats = new();
    private readonly Dictionary<System.Windows.Threading.DispatcherOperation, long> _dispatcherStarts = new();

    /// <summary>Counts what the UI thread runs and for how long, by the method behind each operation.</summary>
    private void WatchDispatcher()
    {
        var methodField = typeof(System.Windows.Threading.DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
        Dispatcher.Hooks.OperationStarted += (_, e) => _dispatcherStarts[e.Operation] = System.Diagnostics.Stopwatch.GetTimestamp();
        Dispatcher.Hooks.OperationCompleted += (_, e) =>
        {
            if (!_dispatcherStarts.Remove(e.Operation, out long start)) return;
            double ms = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            string key = methodField?.GetValue(e.Operation) is Delegate d
                ? $"{d.Method.DeclaringType?.Name}.{d.Method.Name}@{e.Operation.Priority}"
                : $"?@{e.Operation.Priority}";
            var (count, total) = _dispatcherStats.GetValueOrDefault(key);
            _dispatcherStats[key] = (count + 1, total + ms);
        };
    }

    private string TakeDispatcherStats()
    {
        var top = new List<KeyValuePair<string, (int Count, double Ms)>>(_dispatcherStats);
        top.Sort((a, b) => b.Value.Ms.CompareTo(a.Value.Ms));
        _dispatcherStats.Clear();
        var parts = new List<string>();
        for (int i = 0; i < Math.Min(12, top.Count); i++)
            parts.Add($"{top[i].Key} x{top[i].Value.Count} {top[i].Value.Ms:0}ms");
        return string.Join(" || ", parts);
    }

    private static void CollectRunningClocks(Clock root, List<string> lines)
    {
        const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var seen = new HashSet<object>();
        for (var type = root.GetType(); type != null; type = type.BaseType)
        {
            var members = new List<Func<object?>>();
            foreach (var field in type.GetFields(Any)) members.Add(() => field.GetValue(root));
            foreach (var property in type.GetProperties(Any))
                if (property.GetIndexParameters().Length == 0) members.Add(() => property.GetValue(root));
            foreach (var read in members)
            {
                object? value;
                // Some internal getters throw on a root clock; they hold nothing of interest.
                try { value = read(); }
                catch (Exception ex) when (ex is TargetInvocationException or FieldAccessException or MethodAccessException) { continue; }
                if (value is not IEnumerable list || value is string || !seen.Add(value)) continue;
                foreach (object? item in list)
                {
                    var clock = item switch { WeakReference weak => weak.Target as Clock, Clock direct => direct, _ => null };
                    if (clock is { CurrentState: ClockState.Active })
                        lines.Add($"running {Describe(clock.Timeline)}");
                }
            }
        }

        static string Describe(Timeline timeline)
        {
            string text = $"{timeline.GetType().Name} {timeline.Duration} repeat={timeline.RepeatBehavior}";
            if (Storyboard.GetTargetName(timeline) is { } target) text += $" target={target}.{Storyboard.GetTargetProperty(timeline)?.Path}";
            if (timeline is TimelineGroup group)
                foreach (var child in group.Children) text += " | " + Describe(child);
            return text;
        }
    }
}
