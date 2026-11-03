using System.Text.Json.Nodes;

namespace VNotch.OmenHelper;

/// <summary>
/// What the notch may do with the laptop, and nothing else: read fans, temperature, performance mode and keyboard
/// light, and set the performance mode, maximum fans and the keyboard light. Written for HP Victus S boards (8BD4):
/// the performance modes are the BIOS thermal profile plus the GPU power modes, as OMEN Gaming Hub and the Linux
/// driver set them.
/// </summary>
internal sealed class Laptop(HpBios bios)
{
    // Default commands.
    private const uint FanCount = 0x10;
    private const uint SetPerformanceMode = 0x1A;
    private const uint GetGpuModes = 0x21;
    private const uint SetGpuModes = 0x22;
    private const uint GetTemperature = 0x23;
    private const uint GetMaxFan = 0x26;
    private const uint SetMaxFanSpeed = 0x27;
    private const uint GetFanSpeeds = 0x2D;
    private const uint SetFanSpeeds = 0x2E;
    // Keyboard commands.
    private const uint KbdHasLight = 0x01;
    private const uint KbdGetColors = 0x02;
    private const uint KbdSetColors = 0x03;
    private const uint KbdGetLight = 0x04;
    private const uint KbdSetLight = 0x05;

    private const byte LightOn = 0xE4;
    private const byte LightOff = 0x64;
    // The colour table: zone count, 24 bytes of padding, then up to four zones of red, green, blue.
    private const int ColorOffset = 25;
    private const int MaxZones = 4;

    private static byte[] Zero4 => new byte[4];

    public JsonObject Status()
    {
        var status = new JsonObject { ["ok"] = true };
        if (bios.Call(HpBios.Default, GetGpuModes, Zero4, 4) is { Length: >= 2 } gpu)
            status["mode"] = gpu[0] != 0 ? "performance" : gpu[1] != 0 ? "balanced" : "eco";
        if (bios.Call(HpBios.Default, GetMaxFan, Zero4, 4) is { Length: >= 1 } max)
            status["maxFan"] = (max[0] & 1) != 0;
        if (bios.Call(HpBios.Default, GetFanSpeeds, Zero4, 128) is { Length: >= 2 } fans)
            status["fans"] = new JsonArray(fans[0] * 100, fans[1] * 100);
        if (bios.Call(HpBios.Default, GetTemperature, [1, 0, 0, 0], 4) is { Length: >= 1 } temp && temp[0] > 0)
            status["temp"] = (int)temp[0];
        if (bios.Call(HpBios.Keyboard, KbdHasLight, Zero4, 4) is { Length: >= 1 } has && (has[0] & 1) != 0)
        {
            if (bios.Call(HpBios.Keyboard, KbdGetLight, Zero4, 4) is { Length: >= 1 } light)
                status["keyboard"] = light[0] == LightOn;
            if (bios.Call(HpBios.Keyboard, KbdGetColors, Zero4, 128) is { Length: >= ColorOffset + 3 } table &&
                table[0] is > 0 and <= MaxZones)
            {
                status["zones"] = (int)table[0];
                status["color"] = $"#{table[ColorOffset]:X2}{table[ColorOffset + 1]:X2}{table[ColorOffset + 2]:X2}";
            }
        }
        return status;
    }

    /// <summary>Eco, balanced or performance: the thermal profile, then the GPU's custom TGP and boost (PPAB).</summary>
    public bool SetMode(string mode)
    {
        (byte profile, byte ctgp, byte ppab) = mode switch
        {
            "performance" => ((byte)0x01, (byte)1, (byte)1),
            "balanced" => ((byte)0x00, (byte)0, (byte)1),
            "eco" => ((byte)0x00, (byte)0, (byte)0),
            _ => ((byte)0xFF, (byte)0, (byte)0)
        };
        if (profile == 0xFF) return false;
        // The GPU's slowdown temperature stays as it is.
        if (bios.Call(HpBios.Default, GetGpuModes, Zero4, 4) is not { Length: >= 4 } gpu) return false;
        bios.Call(HpBios.Default, FanCount, Zero4, 4);
        if (bios.Call(HpBios.Default, SetPerformanceMode, [0xFF, profile, 0, 0], 0) == null) return false;
        return bios.Call(HpBios.Default, SetGpuModes, [ctgp, ppab, 1, gpu[3]], 0) != null;
    }

    /// <summary>Both fans at full speed, or back to automatic (Victus S needs the speeds reset as well).</summary>
    public bool SetMaxFan(bool on)
    {
        if (on) return bios.Call(HpBios.Default, SetMaxFanSpeed, [1, 0, 0, 0], 0) != null;
        bool off = bios.Call(HpBios.Default, SetMaxFanSpeed, Zero4, 0) != null;
        bios.Call(HpBios.Default, FanCount, Zero4, 4);
        return bios.Call(HpBios.Default, SetFanSpeeds, Zero4, 0) != null && off;
    }

    public bool SetKeyboardLight(bool on) =>
        bios.Call(HpBios.Keyboard, KbdSetLight, [on ? LightOn : LightOff, 0, 0, 0], 0) != null;

    /// <summary>One colour for every zone of the keyboard.</summary>
    public bool SetKeyboardColor(byte red, byte green, byte blue)
    {
        if (bios.Call(HpBios.Keyboard, KbdGetColors, Zero4, 128) is not { Length: >= 128 } table ||
            table[0] is 0 or > MaxZones) return false;
        var updated = (byte[])table.Clone();
        for (int zone = 0; zone < table[0]; zone++)
        {
            updated[ColorOffset + zone * 3] = red;
            updated[ColorOffset + zone * 3 + 1] = green;
            updated[ColorOffset + zone * 3 + 2] = blue;
        }
        return bios.Call(HpBios.Keyboard, KbdSetColors, updated, 0) != null;
    }
}
