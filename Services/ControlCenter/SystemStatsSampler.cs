using System;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Threading;

namespace VNotch.Services.ControlCenter;

/// <summary>One reading of the machine: load, memory, round trip and network throughput.</summary>
internal readonly record struct SystemStats(
    double? CpuPercent,
    double RamPercent,
    double RamUsedGb,
    double RamTotalGb,
    double? GpuPercent,
    long? PingMs,
    double DownBytesPerSecond,
    double UpBytesPerSecond);

/// <summary>
/// Reads <see cref="SystemStats"/> for the controls tab. Load and throughput are rates, so every reading
/// compares with the one before; it only runs while the tab is open.
/// </summary>
internal sealed class SystemStatsSampler : IDisposable
{
    // Answers ICMP from almost everywhere and sits close by: a fair "how is my connection" number.
    private const string PingHost = "1.1.1.1";
    private const int PingTimeoutMs = 900;
    private const double BytesPerGb = 1024d * 1024 * 1024;
    // Load and throughput are differences: right after the tab opens, a short first reading to compare with.
    private const int BaselineMs = 300;

    private readonly Ping _ping = new();
    private ulong _lastIdle, _lastKernel, _lastUser;
    private long _lastReceived, _lastSent, _lastNetTicks;

    public void Dispose() => _ping.Dispose();

    /// <summary>The tab closed: the next reading starts over instead of averaging over the time it was shut.</summary>
    public void Reset()
    {
        _lastKernel = 0;
        _lastNetTicks = 0;
    }

    public SystemStats Sample()
    {
        if (_lastKernel == 0)
        {
            SampleCpu();
            SampleNetwork();
            Thread.Sleep(BaselineMs);
        }
        double? cpu = SampleCpu();

        PerformanceDiagnosticService.SampleGlobalMemory(out ulong used, out ulong total, out _, out double load);

        var gpu = GpuMonitorService.Instance;
        double? gpuPercent = gpu.IsRunning ? gpu.GetGpuUsage().GlobalGpuPercent : null;

        var (down, up) = SampleNetwork();
        return new SystemStats(cpu, load, used / BytesPerGb, total / BytesPerGb, gpuPercent, SamplePing(), down, up);
    }

    private double? SampleCpu()
    {
        if (!Win32Interop.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return null;
        ulong idle = idleTime.ToUInt64(), kernel = kernelTime.ToUInt64(), user = userTime.ToUInt64();
        bool first = _lastKernel == 0;
        // Kernel time includes idle time.
        ulong total = kernel - _lastKernel + (user - _lastUser);
        ulong busy = total - (idle - _lastIdle);
        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;
        if (first || total == 0) return null;
        return Math.Clamp(100d * busy / total, 0, 100);
    }

    private (double Down, double Up) SampleNetwork()
    {
        long received = 0, sent = 0;
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = adapter.GetIPv4Statistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
            }
        }
        catch (NetworkInformationException)
        {
            return (0, 0);
        }

        long now = Stopwatch.GetTimestamp();
        double seconds = _lastNetTicks == 0 ? 0 : (now - _lastNetTicks) / (double)Stopwatch.Frequency;
        double down = seconds > 0 ? Math.Max(0, received - _lastReceived) / seconds : 0;
        double up = seconds > 0 ? Math.Max(0, sent - _lastSent) / seconds : 0;
        _lastReceived = received;
        _lastSent = sent;
        _lastNetTicks = now;
        return (down, up);
    }

    private long? SamplePing()
    {
        try
        {
            var reply = _ping.Send(PingHost, PingTimeoutMs);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException)
        {
            return null;
        }
    }
}
