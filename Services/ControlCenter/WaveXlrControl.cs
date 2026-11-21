using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VNotch.Services.ControlCenter;

internal sealed record WaveXlrState(string DeviceId, string InputId, bool Muted);

/// <summary>Wave Link's hardware mute; Windows' endpoint SetMute does not control the Wave XLR.</summary>
internal static class WaveXlrControl
{
    public static Task<WaveXlrState?> ReadAsync(CancellationToken token = default) => UseAsync(async (socket, ct) =>
        Parse(await RpcAsync(socket, 1, "getInputDevices", null, ct)), token);

    public static async Task<bool> SetMutedAsync(WaveXlrState state, bool muted, CancellationToken token = default) =>
        await UseAsync(async (socket, ct) =>
        {
            // Wave Link 3 protocol: https://github.com/Raphiiko/wavelink-ts/blob/main/PROTOCOL.md
            var parameters = new JsonObject { ["id"] = state.DeviceId,
                ["inputs"] = new JsonArray(new JsonObject { ["id"] = state.InputId, ["isMuted"] = muted }) };
            var result = await RpcAsync(socket, 1, "setInputDevice", parameters, ct);
            return result != null ? state with { Muted = muted } : null;
        }, token) != null;

    internal static WaveXlrState? Parse(JsonNode? result)
    {
        if (result is not JsonObject root || root["inputDevices"] is not JsonArray devices) return null;
        foreach (var device in devices.OfType<JsonObject>())
        {
            if (device["name"] is not JsonValue name || !name.TryGetValue<string>(out var text) ||
                !text.Contains("Wave XLR", StringComparison.OrdinalIgnoreCase) || device["inputs"] is not JsonArray inputs) continue;
            if (device["id"] is not JsonValue id || !id.TryGetValue<string>(out var deviceId) || string.IsNullOrWhiteSpace(deviceId)) continue;
            foreach (var input in inputs.OfType<JsonObject>())
                if (input["id"] is JsonValue inputId && inputId.TryGetValue<string>(out var identifier) && !string.IsNullOrWhiteSpace(identifier) &&
                    input["isMuted"] is JsonValue mute && mute.TryGetValue<bool>(out bool muted))
                    return new(deviceId, identifier, muted);
        }
        return null;
    }

    private static async Task<WaveXlrState?> UseAsync(Func<ClientWebSocket, CancellationToken, Task<WaveXlrState?>> action, CancellationToken token)
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Elgato.WaveLink_g54w8ztgkx496", "LocalState", "ws-info.json");
            if (!File.Exists(path)) return null;
            var info = JsonNode.Parse(await File.ReadAllTextAsync(path, token));
            if (info is not JsonObject settings || settings["port"] is not JsonValue number || !number.TryGetValue<int>(out int port) || port is < 1 or > 65535) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", "streamdeck://");
            socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), timeout.Token);
            if (await RpcAsync(socket, 0, "getApplicationInfo", null, timeout.Token) is not JsonObject app ||
                app["appID"] is not JsonValue appId || !appId.TryGetValue<string>(out var name) || name != "EWL") return null;
            return await action(socket, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or WebSocketException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// A JSON-RPC request. Wave Link 3.2.10 silently drops requests that carry "params": null (no reply at all, so
    /// the mic read timed out and showed as missing), so the field is left out when there is nothing to send.
    /// </summary>
    internal static JsonObject BuildRequest(int id, string method, JsonNode? parameters)
    {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters != null) request["params"] = parameters;
        return request;
    }

    private static async Task<JsonNode?> RpcAsync(ClientWebSocket socket, int id, string method, JsonNode? parameters, CancellationToken token)
    {
        var request = BuildRequest(id, method, parameters);
        await socket.SendAsync(Encoding.UTF8.GetBytes(request.ToJsonString()), WebSocketMessageType.Text, true, token);
        var buffer = new byte[8192];
        while (socket.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult frame;
            do
            {
                frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                if (frame.MessageType != WebSocketMessageType.Text || message.Length + frame.Count > 1024 * 1024) return null;
                message.Write(buffer, 0, frame.Count);
            } while (!frame.EndOfMessage);
            var reply = JsonNode.Parse(message.ToArray());
            if (reply is not JsonObject responseObject || responseObject["id"] is not JsonValue responseId || !responseId.TryGetValue<int>(out int response) || response != id) continue;
            return responseObject["error"] == null ? responseObject["result"] : null;
        }
        return null;
    }
}
