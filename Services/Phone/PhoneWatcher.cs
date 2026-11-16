using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services.Phone;

public enum PhoneState { Device, Unauthorized, Offline }

public sealed record PhoneInfo(string Serial, PhoneState State, string? Name, int? BatteryPercent, bool Charging)
{
    public bool IsWireless => Serial.Contains(':');
}

public sealed class PhoneWatcher : IDisposable
{
    private sealed class Entry(PhoneInfo info)
    {
        public PhoneInfo Info = info;
        public long BatteryVersion;
        public bool BatteryLoaded;
    }

    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _token;
    private readonly HashSet<Task<string?>> _commands = new();
    private readonly Dictionary<string, Entry> _phones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Queue<PhoneInfo?> _notifications = new();
    private PhoneInfo? _current;
    private Task? _runner;
    private bool _notifying;
    private bool _disposed;

    public PhoneWatcher() => _token = _stop.Token;

    public PhoneInfo? Current { get { lock (_gate) return _current; } }
    public event Action<PhoneInfo?>? Changed;

    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _runner is not null || !PhoneTools.Available) return;
            _runner = Task.Run(TrackAsync);
        }
    }

    private async Task TrackAsync()
    {
        int failures = 0;
        // adb's server restarts whenever another tool (Escrcpy, a second adb) takes over, which ends the tracker;
        // it is back within a second, so the wait stays short and only grows while adb keeps failing.
        int[] delays = [1, 2, 5, 10];
        while (!_token.IsCancellationRequested)
        {
            var lifetime = Stopwatch.StartNew();
            using var process = new Process { StartInfo = PhoneTools.StartInfo(PhoneTools.Adb!, ["track-devices"]) };
            try
            {
                if (!process.Start()) throw new InvalidOperationException("adb did not start.");
                using var registration = _token.Register(() => PhoneTools.Kill(process));
                // Drain stderr independently so a full pipe cannot stall device notifications.
                Task<string> errors = process.StandardError.ReadToEndAsync();
                try
                {
                    var buffer = new StringBuilder();
                    var chars = new char[4096];
                    while (!_token.IsCancellationRequested)
                    {
                        int read = await process.StandardOutput.ReadAsync(chars.AsMemory(), _token).ConfigureAwait(false);
                        if (read == 0) break;
                        foreach (var devices in AdbOutput.ParseTrackDevices(buffer, new string(chars, 0, read)))
                            ApplyDevices(devices);
                    }
                }
                finally
                {
                    PhoneTools.Kill(process);
                    await errors.ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or InvalidOperationException or OperationCanceledException or FormatException)
            {
                if (!_token.IsCancellationRequested) RuntimeLog.Warn("PHONE", "Device tracking interrupted.");
            }
            finally
            {
                PhoneTools.Kill(process);
                ApplyDevices(Array.Empty<(string, PhoneState)>());
            }
            if (_token.IsCancellationRequested) break;
            if (lifetime.Elapsed > TimeSpan.FromSeconds(10)) failures = 0;
            int delay = delays[Math.Min(failures++, delays.Length - 1)];
            RuntimeLog.Warn("PHONE", $"Restarting device tracking in {delay} seconds.");
            try { await Task.Delay(TimeSpan.FromSeconds(delay), _token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void ApplyDevices(IReadOnlyList<(string Serial, PhoneState State)> devices)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var present = new HashSet<string>(devices.Select(d => d.Serial), StringComparer.Ordinal);
            foreach (string serial in _phones.Keys.Where(s => !present.Contains(s)).ToArray()) _phones.Remove(serial);
            foreach (var (serial, state) in devices)
            {
                if (_phones.TryGetValue(serial, out var existing) && existing.Info.State == state) continue;
                _names.TryGetValue(serial, out string? name);
                var entry = new Entry(new PhoneInfo(serial, state, name, null, false));
                _phones[serial] = entry;
                if (state == PhoneState.Device) _ = Task.Run(() => FillDetailsAsync(entry));
            }
            UpdateCurrent();
        }
    }

    private async Task FillDetailsAsync(Entry entry)
    {
        string serial = entry.Info.Serial;
        Task<string?> name = ReadNameAsync(serial);
        Task<string?> battery = RunAdbAsync(["-s", serial, "shell", "dumpsys", "battery"]);
        await Task.WhenAll(name, battery).ConfigureAwait(false);
        string? resolved = await name.ConfigureAwait(false);
        string? batteryOutput = await battery.ConfigureAwait(false);
        lock (_gate)
        {
            // A reconnect gets a new entry, so an old request cannot overwrite it.
            if (_disposed || !_phones.TryGetValue(serial, out var active) || !ReferenceEquals(active, entry)) return;
            if (!string.IsNullOrWhiteSpace(resolved)) _names[serial] = resolved;
            var parsed = AdbOutput.ParseBattery(batteryOutput ?? "");
            entry.Info = entry.Info with { Name = resolved };
            if (!entry.BatteryLoaded && batteryOutput is not null)
            {
                entry.Info = entry.Info with { BatteryPercent = parsed.Level, Charging = parsed.Charging };
                entry.BatteryLoaded = true;
            }
            UpdateCurrent();
        }
    }

    private async Task<string?> ReadNameAsync(string serial)
    {
        lock (_gate)
            if (_names.TryGetValue(serial, out string? cached)) return cached;
        string? name = await RunAdbAsync(["-s", serial, "shell", "getprop", "ro.product.marketname"]).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(name))
            name = await RunAdbAsync(["-s", serial, "shell", "getprop", "ro.product.model"]).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    private void UpdateCurrent()
    {
        PhoneInfo? next = _phones.Values.Select(e => e.Info)
            .Where(p => p.State == PhoneState.Device)
            .OrderBy(p => p.IsWireless).FirstOrDefault()
            ?? _phones.Values.Select(e => e.Info).FirstOrDefault(p => p.State == PhoneState.Unauthorized);
        if (_current == next) return;
        _current = next;
        _notifications.Enqueue(next);
        if (_notifying) return;
        _notifying = true;
        _ = Task.Run(Notify);
    }

    private void Notify()
    {
        while (true)
        {
            PhoneInfo? phone;
            Action<PhoneInfo?>? handlers;
            lock (_gate)
            {
                if (_disposed || !_notifications.TryDequeue(out phone))
                {
                    _notifying = false;
                    return;
                }
                handlers = Changed;
            }
            if (handlers is null) continue;
            foreach (Action<PhoneInfo?> handler in handlers.GetInvocationList())
            {
                try { handler(phone); }
                catch (Exception) { RuntimeLog.Warn("PHONE", "Phone change handler failed."); }
            }
        }
    }

    public async Task RefreshBatteryAsync()
    {
        Entry entry;
        long version;
        lock (_gate)
        {
            if (_disposed || _current?.State != PhoneState.Device) return;
            entry = _phones[_current.Serial];
            version = ++entry.BatteryVersion;
        }
        string? output = await RunAdbAsync(["-s", entry.Info.Serial, "shell", "dumpsys", "battery"]).ConfigureAwait(false);
        if (output is null) return;
        var battery = AdbOutput.ParseBattery(output);
        lock (_gate)
        {
            if (_disposed || !_phones.TryGetValue(entry.Info.Serial, out var active) || !ReferenceEquals(active, entry)
                || entry.BatteryVersion != version) return;
            entry.Info = entry.Info with { BatteryPercent = battery.Level, Charging = battery.Charging };
            entry.BatteryLoaded = true;
            UpdateCurrent();
        }
    }

    public async Task<bool> ConnectWifiAsync(string host)
    {
        lock (_gate) { if (_disposed) return false; }
        if (string.IsNullOrWhiteSpace(host)) return false;
        string target = host.Trim();
        if (!target.Contains(':')) target += ":5555";
        string? result = await RunAdbAsync(["connect", target]).ConfigureAwait(false);
        return result is not null && Regex.IsMatch(result, @"^(?:already )?connected to\s", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    }

    public async Task<string?> EnableWifiAsync()
    {
        Entry? entry;
        lock (_gate)
        {
            if (_disposed) return null;
            entry = _phones.Values.FirstOrDefault(p => p.Info.State == PhoneState.Device && !p.Info.IsWireless);
        }
        if (entry is null) return null;
        string serial = entry.Info.Serial;
        string? output = await RunAdbAsync(["-s", serial, "shell", "ip", "-f", "inet", "addr", "show", "wlan0"]).ConfigureAwait(false);
        Match match = Regex.Match(output ?? "", @"\binet\s+(\d{1,3}(?:\.\d{1,3}){3})/\d+");
        if (!match.Success || !IPAddress.TryParse(match.Groups[1].Value, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) return null;
        lock (_gate)
            if (_disposed || !_phones.TryGetValue(serial, out var active) || !ReferenceEquals(active, entry)) return null;
        string? result = await RunAdbAsync(["-s", serial, "tcpip", "5555"]).ConfigureAwait(false);
        return result?.Contains("restarting in TCP mode", StringComparison.OrdinalIgnoreCase) == true ? ip.ToString() : null;
    }

    private async Task<string?> RunAdbAsync(IEnumerable<string> args)
    {
        Task<string?> command;
        lock (_gate)
        {
            if (_disposed) return null;
            command = PhoneTools.RunAdbAsync(args, ct: _token);
            _commands.Add(command);
        }
        try { return await command.ConfigureAwait(false); }
        finally { lock (_gate) _commands.Remove(command); }
    }

    public void Dispose()
    {
        Task? runner;
        Task<string?>[] commands;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _current = null;
            _phones.Clear();
            _notifications.Clear();
            Changed = null;
            runner = _runner;
            commands = _commands.ToArray();
        }
        _stop.Cancel();
        runner?.GetAwaiter().GetResult();
        Task.WhenAll(commands).GetAwaiter().GetResult();
        // Tokens remain usable by detail reads that are finishing cancellation.
        _stop.Dispose();
    }
}
