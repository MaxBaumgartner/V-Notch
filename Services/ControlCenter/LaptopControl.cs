using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace VNotch.Services.ControlCenter;

/// <summary>The laptop as the OMEN helper reports it; null where the BIOS gave no answer.</summary>
/// <param name="Mode">"eco", "balanced" or "performance".</param>
/// <param name="KeyboardZones">Colour zones of the keyboard light; 0 when it has one colour only.</param>
internal sealed record LaptopStatus(string? Mode, bool? MaxFan, int? CpuFanRpm, int? GpuFanRpm, int? Temperature,
    bool? KeyboardLight, int KeyboardZones, string? KeyboardColor);

/// <summary>
/// Talks to the OMEN helper (Tools/OmenHelper), which runs elevated because HP's BIOS interface needs admin
/// rights: reads the laptop's state and asks for a performance mode, maximum fans or the keyboard light. Without
/// the helper everything answers null or false, and it is only looked for again after a while.
/// </summary>
internal static class LaptopControl
{
    internal const string PipeName = "VNotch.OmenHelper";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RetryAfterMissing = TimeSpan.FromSeconds(30);
    private static DateTime _missingUntilUtc;

    public static LaptopStatus? Read() => Send(new JsonObject { ["cmd"] = "status" }) is { } reply ? Parse(reply) : null;

    public static bool SetMode(string mode) => Ok(Send(new JsonObject { ["cmd"] = "mode", ["value"] = mode }));

    public static bool SetMaxFan(bool on) => Ok(Send(new JsonObject { ["cmd"] = "maxFan", ["value"] = on }));

    public static bool SetKeyboardLight(bool on) => Ok(Send(new JsonObject { ["cmd"] = "keyboard", ["value"] = on }));

    /// <summary>"#RRGGBB" for every zone.</summary>
    public static bool SetKeyboardColor(string hex) => Ok(Send(new JsonObject { ["cmd"] = "color", ["value"] = hex }));

    internal static LaptopStatus? Parse(JsonObject reply)
    {
        if (reply["ok"] is not JsonValue ok || !ok.TryGetValue(out bool fine) || !fine) return null;
        int? cpu = null, gpu = null;
        if (reply["fans"] is JsonArray { Count: >= 2 } fans)
        {
            cpu = fans[0] is JsonValue c && c.TryGetValue(out int a) ? a : null;
            gpu = fans[1] is JsonValue g && g.TryGetValue(out int b) ? b : null;
        }
        return new LaptopStatus(Str(reply, "mode"), Bool(reply, "maxFan"), cpu, gpu, Int(reply, "temp"), Bool(reply, "keyboard"),
            Int(reply, "zones") ?? 0, Str(reply, "color"));
    }

    private static bool Ok(JsonObject? reply) => reply?["ok"] is JsonValue ok && ok.TryGetValue(out bool fine) && fine;

    /// <summary>One request, one JSON line back; null when the helper is not there or does not answer in time.</summary>
    private static JsonObject? Send(JsonObject request)
    {
        if (DateTime.UtcNow < _missingUntilUtc) return null;
        try
        {
            return SendAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            _missingUntilUtc = DateTime.UtcNow + RetryAfterMissing;
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<JsonObject?> SendAsync(JsonObject request)
    {
        using var cancel = new CancellationTokenSource(Timeout);
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cancel.Token).ConfigureAwait(false);
        byte[] line = Encoding.UTF8.GetBytes(request.ToJsonString() + "\n");
        await pipe.WriteAsync(line, cancel.Token).ConfigureAwait(false);
        await pipe.FlushAsync(cancel.Token).ConfigureAwait(false);
        var reply = new MemoryStream();
        var buffer = new byte[1024];
        int read;
        while ((read = await pipe.ReadAsync(buffer, cancel.Token).ConfigureAwait(false)) > 0)
        {
            int newline = Array.IndexOf(buffer, (byte)'\n', 0, read);
            reply.Write(buffer, 0, newline >= 0 ? newline : read);
            if (newline >= 0) break;
        }
        return JsonNode.Parse(reply.ToArray()) as JsonObject;
    }

    private static string? Str(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static bool? Bool(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue(out bool b) ? b : null;

    private static int? Int(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue(out int i) ? i : null;
}
