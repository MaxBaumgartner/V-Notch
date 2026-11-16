using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace VNotch.Services.Phone;

public static class AdbOutput
{
    public static IReadOnlyList<(string Serial, PhoneState State)> ParseDevices(string devicesLOutput)
    {
        var devices = new List<(string Serial, PhoneState State)>();
        foreach (string line in devicesLOutput.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('*') || trimmed.StartsWith("List of devices", StringComparison.Ordinal)) continue;
            string[] fields = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 2) continue;
            PhoneState? state = fields[1] switch
            {
                "device" => PhoneState.Device,
                "unauthorized" => PhoneState.Unauthorized,
                "offline" => PhoneState.Offline,
                _ => null
            };
            if (state is { } known) devices.Add((fields[0], known));
        }
        return devices;
    }

    public static IEnumerable<IReadOnlyList<(string Serial, PhoneState State)>> ParseTrackDevices(StringBuilder buffer, string chunk)
    {
        buffer.Append(chunk);
        var blocks = new List<IReadOnlyList<(string Serial, PhoneState State)>>();
        while (buffer.Length >= 4)
        {
            if (!int.TryParse(buffer.ToString(0, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int length))
                throw new FormatException("Invalid adb track-devices length.");
            if (buffer.Length < 4 + length) break;
            blocks.Add(ParseDevices(buffer.ToString(4, length)));
            buffer.Remove(0, 4 + length);
        }
        return blocks;
    }

    public static (int? Level, bool Charging) ParseBattery(string dumpsys)
    {
        int? level = null;
        bool charging = false;
        foreach (string line in dumpsys.Split('\n'))
        {
            int colon = line.IndexOf(':');
            if (colon < 0) continue;
            string key = line[..colon].Trim();
            string value = line[(colon + 1)..].Trim();
            if (key == "level" && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed is >= 0 and <= 100)
                level = parsed;
            if (key is "AC powered" or "USB powered" or "Wireless powered" && bool.TryParse(value, out bool powered))
                charging |= powered;
        }
        return (level, charging);
    }
}
