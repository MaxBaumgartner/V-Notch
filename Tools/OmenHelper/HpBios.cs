using System.Management;

namespace VNotch.OmenHelper;

/// <summary>
/// HP's BIOS interface (WMI class <c>hpqBIntM</c> in <c>root\wmi</c>), the one OMEN Gaming Hub uses. Every call is
/// a command, a command type and a few bytes in; the method's number says how many bytes come back. Needs admin
/// rights. Command values follow the Linux hp-wmi driver and OmenMon.
/// </summary>
internal sealed class HpBios : IDisposable
{
    /// <summary>Fans, temperature, performance mode, GPU power.</summary>
    public const uint Default = 0x20008;
    /// <summary>Keyboard backlight and colours.</summary>
    public const uint Keyboard = 0x20009;

    private static readonly byte[] Sign = "SECU"u8.ToArray();
    private readonly object _gate = new();
    private ManagementObject? _bios;

    /// <summary>Sends one call; null when the BIOS refused it (return code) or WMI failed.</summary>
    /// <param name="outSize">0, 4, 128, 1024 or 4096: picks <c>hpqBIOSInt&lt;outSize&gt;</c>.</param>
    public byte[]? Call(uint command, uint type, byte[] data, int outSize)
    {
        lock (_gate)
        {
            try
            {
                _bios ??= FindBios();
                using var inClass = new ManagementClass(@"root\wmi", "hpqBDataIn", null);
                using var input = inClass.CreateInstance();
                input["Sign"] = Sign;
                input["Command"] = command;
                input["CommandType"] = type;
                input["Size"] = (uint)data.Length;
                input["hpqBData"] = data;

                string method = "hpqBIOSInt" + outSize;
                using var parameters = _bios.GetMethodParameters(method);
                parameters["InData"] = input;
                using var result = _bios.InvokeMethod(method, parameters, null);
                if (result?["OutData"] is not ManagementBaseObject output) return null;
                if (Convert.ToUInt32(output["rwReturnCode"]) != 0) return null;
                return outSize == 0 ? [] : output["Data"] as byte[] ?? [];
            }
            catch (ManagementException ex)
            {
                Log.Write($"BIOS 0x{command:X}/0x{type:X2} failed: {ex.Message}");
                _bios?.Dispose();
                _bios = null;
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Write($"BIOS 0x{command:X}/0x{type:X2} denied: {ex.Message}");
                return null;
            }
        }
    }

    private static ManagementObject FindBios()
    {
        using var methods = new ManagementClass(@"root\wmi", "hpqBIntM", null);
        foreach (ManagementObject instance in methods.GetInstances()) return instance;
        throw new ManagementException("hpqBIntM has no instance");
    }

    public void Dispose()
    {
        lock (_gate) _bios?.Dispose();
    }
}
