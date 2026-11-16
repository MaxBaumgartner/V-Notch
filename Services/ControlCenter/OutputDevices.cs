using System;
using System.Collections.Generic;
using System.Linq;

namespace VNotch.Services.ControlCenter;

/// <summary>A speaker or headphones the system tab offers; <see cref="Name"/> is the short name it shows.</summary>
internal sealed record OutputDevice(string Id, string Name, string FriendlyName, bool IsDefault, bool Bluetooth);

/// <summary>
/// The real speakers and headphones, without the virtual ones (Wave Link mixes, streaming drivers), with short
/// names: "Lautsprecher (MOONDROP Rays)" is "Rays", "Kopfhörer (WF-1000XM6)" is "XM6", the 3.5 mm jack is
/// "Boxen". Bluetooth ones are told apart by the names of the paired Bluetooth devices.
/// </summary>
internal static class OutputDevices
{
    private static readonly string[] Virtual =
        { "Virtual", "Elgato", "Wave Link", "Steam Streaming", "NVIDIA", "Oculus", "VB-Audio", "Voicemeeter", "Sonar", "Hands-Free", "Freisprech" };

    public static List<OutputDevice> List(IEnumerable<string> bluetoothNames)
    {
        var bt = bluetoothNames.Where(n => n.Length >= 3).ToList();
        var list = AudioMixerService.EnumerateOutputDevices()
            .Where(d => !IsVirtual(d.FriendlyName))
            .Select(d => new OutputDevice(d.Id, Nickname(d.FriendlyName), d.FriendlyName, d.IsDefault,
                bt.Any(n => d.FriendlyName.Contains(n, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        // The laptop's own speakers only count when nothing else is there (on the go), or when they play.
        if (list.Count > 1) list.RemoveAll(o => IsBuiltInSpeaker(o.FriendlyName) && !o.IsDefault);
        return list;
    }

    internal static bool IsBuiltInSpeaker(string friendlyName)
    {
        int open = friendlyName.IndexOf(" (", StringComparison.Ordinal);
        if (open <= 0) return false;
        string inner = friendlyName[(open + 2)..];
        bool chip = inner.Contains("Realtek", StringComparison.OrdinalIgnoreCase) || inner.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase);
        return chip && !IsHeadphoneJack(friendlyName[..open]);
    }

    internal static bool IsVirtual(string friendlyName) =>
        Virtual.Any(v => friendlyName.Contains(v, StringComparison.OrdinalIgnoreCase));

    internal static string Nickname(string friendlyName)
    {
        string name = friendlyName.Trim();
        int open = name.IndexOf(" (", StringComparison.Ordinal);
        string kind = open > 0 ? name[..open] : name;
        string inner = open > 0 ? name[(open + 2)..].TrimEnd(')').Trim() : "";
        if (inner.Length == 0) return kind;

        // The laptop's own sound chip: its jack is the speakers on the desk, its speakers are the laptop.
        if (inner.Contains("Realtek", StringComparison.OrdinalIgnoreCase) || inner.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase))
            return IsHeadphoneJack(kind) ? Loc.Get("sound.jack") : Loc.Get("sound.builtIn");

        // "MOONDROP Rays" -> "Rays"; "WF-1000XM6" -> "XM6"; "Sony WH-1000XM5" -> "XM5".
        string last = inner.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
        int dash = last.LastIndexOf('-');
        if (dash >= 0 && dash < last.Length - 1)
        {
            string model = last[(dash + 1)..].TrimStart('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (model.Length >= 2) return model;
        }
        return last.Length >= 3 ? last : inner;
    }

    private static bool IsHeadphoneJack(string kind) =>
        kind.Contains("Kopfh", StringComparison.OrdinalIgnoreCase) || kind.Contains("Headphone", StringComparison.OrdinalIgnoreCase)
        || kind.Contains("Casque", StringComparison.OrdinalIgnoreCase) || kind.Contains("Auricular", StringComparison.OrdinalIgnoreCase);
}
