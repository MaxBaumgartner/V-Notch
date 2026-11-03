using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace VNotch.OmenHelper;

/// <summary>
/// The OMEN helper: started elevated by a logon task, it answers V-Notch on a named pipe that only the user who
/// runs it (and the system) may open. One request per connection, one JSON line each way, and only the fixed
/// commands below: nothing in a request is ever run or passed on as it is.
/// </summary>
internal static class Program
{
    public const string PipeName = "VNotch.OmenHelper";
    private const int MaxRequestBytes = 256;

    private static int Main()
    {
        using var single = new Mutex(true, @"Global\VNotch.OmenHelper", out bool first);
        if (!first) return 0;
        using var bios = new HpBios();
        var laptop = new Laptop(bios);
        Log.Write("Started");
        while (true)
        {
            try
            {
                using var pipe = CreatePipe();
                pipe.WaitForConnection();
                Serve(pipe, laptop);
            }
            catch (IOException ex)
            {
                // A client that hung up mid-way: wait for the next.
                Log.Write($"Pipe: {ex.Message}");
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.None, 1024, 1024, security);
    }

    private static void Serve(NamedPipeServerStream pipe, Laptop laptop)
    {
        string? line = ReadLine(pipe);
        JsonObject reply;
        try
        {
            reply = line != null && JsonNode.Parse(line) is JsonObject request
                ? Handle(request, laptop)
                : Error("bad request");
        }
        catch (System.Text.Json.JsonException)
        {
            reply = Error("bad request");
        }
        byte[] bytes = Encoding.UTF8.GetBytes(reply.ToJsonString() + "\n");
        pipe.Write(bytes, 0, bytes.Length);
        pipe.Flush();
        pipe.WaitForPipeDrain();
    }

    private static JsonObject Handle(JsonObject request, Laptop laptop)
    {
        string cmd = request["cmd"] is JsonValue c && c.TryGetValue(out string? s) ? s : "";
        JsonNode? value = request["value"];
        switch (cmd)
        {
            case "status":
                return laptop.Status();
            case "mode" when value is JsonValue v && v.TryGetValue(out string? mode) && mode is "eco" or "balanced" or "performance":
                return Done(laptop.SetMode(mode));
            case "maxFan" when value is JsonValue v && v.TryGetValue(out bool on):
                return Done(laptop.SetMaxFan(on));
            case "keyboard" when value is JsonValue v && v.TryGetValue(out bool on):
                return Done(laptop.SetKeyboardLight(on));
            case "color" when value is JsonValue v && v.TryGetValue(out string? hex) && TryParseColor(hex, out var rgb):
                return Done(laptop.SetKeyboardColor(rgb.R, rgb.G, rgb.B));
            default:
                return Error("unknown command");
        }
    }

    /// <summary>"#RRGGBB" only.</summary>
    private static bool TryParseColor(string hex, out (byte R, byte G, byte B) rgb)
    {
        rgb = default;
        if (hex.Length != 7 || hex[0] != '#' || !uint.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out uint value))
            return false;
        rgb = ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    private static string? ReadLine(Stream pipe)
    {
        var buffer = new List<byte>(64);
        int b;
        while ((b = pipe.ReadByte()) >= 0 && b != '\n')
        {
            if (buffer.Count == MaxRequestBytes) return null;
            buffer.Add((byte)b);
        }
        return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static JsonObject Done(bool ok) => ok ? new JsonObject { ["ok"] = true } : Error("the BIOS refused");

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };
}

/// <summary>A short log beside V-Notch's own (the task runs as the signed-in user).</summary>
internal static class Log
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "V-Notch", "omen-helper.log");

    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            if (File.Exists(Path) && new FileInfo(Path).Length > 256 * 1024) File.Delete(Path);
            File.AppendAllText(Path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging is best effort.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
