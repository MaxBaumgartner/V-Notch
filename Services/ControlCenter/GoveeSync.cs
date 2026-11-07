using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace VNotch.Services.ControlCenter;

/// <summary>
/// The Govee strip's music sync runs on this PC as the scheduled task "Govee PC-Sync" (it listens to the sound
/// output and sends colours to the strip's bridge on the Pi). Starting or stopping that task is the home tab's
/// "Music sync" switch; without it the strip just shows the colour picked in Home Assistant.
/// Talks to the Task Scheduler over its COM API, so no console window flashes up.
/// </summary>
internal static class GoveeSync
{
    private const string TaskName = "Govee PC-Sync";
    private const int StateRunning = 4;

    /// <summary>True or false while the task exists; null when it is not set up on this PC.</summary>
    public static Task<bool?> IsRunningAsync() => Task.Run(() =>
    {
        try
        {
            return WithTask(task => (bool?)((int)task.State == StateRunning));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return null;
        }
    });

    public static Task<bool> SetRunningAsync(bool run) => Task.Run(() =>
    {
        try
        {
            return WithTask(task =>
            {
                if (run) task.Run(null);
                else task.Stop(0);
                return true;
            });
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            RuntimeLog.Warn("GOVEE", $"Music sync could not be {(run ? "started" : "stopped")}: {ex.Message}");
            return false;
        }
    });

    private static T WithTask<T>(Func<dynamic, T> use)
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new COMException("Task Scheduler is not available");
        dynamic scheduler = Activator.CreateInstance(type)!;
        try
        {
            scheduler.Connect();
            dynamic task = scheduler.GetFolder("\\").GetTask(TaskName);
            return use(task);
        }
        finally
        {
            Marshal.FinalReleaseComObject(scheduler);
        }
    }
}
