using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.VoiceGateway.Realtime;

/// <summary>
/// Bridges a browser WebSocket (raw PCM16 24 kHz mono) with the Azure OpenAI Realtime
/// WebSocket. Phase 1: configure the model as an echo bot; Phase 2 will register a
/// `runOrchestrator` tool that calls into AgentHandoff.Engine.
/// </summary>
public sealed class RealtimeBridge
{
    private readonly RealtimeOptions _opts;
    private readonly ILogger<RealtimeBridge> _log;

    public RealtimeBridge(RealtimeOptions opts, ILogger<RealtimeBridge> log)
    {
        _opts = opts;
        _log = log;
    }

    public async Task RunAsync(WebSocket clientWs, CancellationToken ct)
    {
        using var aoaiWs = new ClientWebSocket();
        aoaiWs.Options.SetRequestHeader("api-key", _opts.ApiKey);

        var host = _opts.Endpoint.Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase)
                                 .TrimEnd('/');
        var url = new Uri($"{host}/openai/realtime?api-version={_opts.ApiVersion}&deployment={_opts.Deployment}");

        _log.LogInformation("connecting to AOAI realtime {Url}", url);
        try
        {
            await aoaiWs.ConnectAsync(url, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "AOAI realtime connect failed");
            await SendBrowserErrorAsync(clientWs, $"upstream connect failed: {ex.Message}", ct);
            return;
        }

        // Configure the session: PCM16 24 kHz, server VAD, echo-bot persona.
        var sessionUpdate = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "audio", "text" },
                instructions = "You are an echo bot. When the user speaks, repeat their utterance back verbatim in the same language. Be brief.",
                voice = "alloy",
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                input_audio_transcription = new { model = "whisper-1" },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.5,
                    prefix_padding_ms = 300,
                    silence_duration_ms = 500,
                },
            },
        };
        try
        {
            await SendJsonAsync(aoaiWs, sessionUpdate, ct);
            await SendBrowserStatusAsync(clientWs, "session.ready", ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "session.update failed");
            await SendBrowserErrorAsync(clientWs, $"session.update failed: {ex.Message}", ct);
            return;
        }

        var browserToAoai = PumpBrowserToAoaiAsync(clientWs, aoaiWs, ct);
        var aoaiToBrowser = PumpAoaiToBrowserAsync(aoaiWs, clientWs, ct);

        await Task.WhenAny(browserToAoai, aoaiToBrowser);

        try { await clientWs.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        try { await aoaiWs.CloseAsync(WebSocketCloseStatus.NormalClosure,   "bye", CancellationToken.None); } catch { }
    }

    private static async Task SendBrowserErrorAsync(WebSocket ws, string message, CancellationToken ct)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "error", error = new { message } });
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch { }
    }

    private static async Task SendBrowserStatusAsync(WebSocket ws, string status, CancellationToken ct)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { type = "status", status });
            if (ws.State == WebSocketState.Open)
                await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
        }
        catch { }
    }

    private async Task PumpBrowserToAoaiAsync(WebSocket browser, ClientWebSocket aoai, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        while (!ct.IsCancellationRequested && browser.State == WebSocketState.Open)
        {
            var msg = await ReceiveFullAsync(browser, buffer, ct);
            if (msg is null) break;

            if (msg.MessageType == WebSocketMessageType.Binary)
            {
                var b64 = Convert.ToBase64String(buffer, 0, msg.Count);
                await SendJsonAsync(aoai, new { type = "input_audio_buffer.append", audio = b64 }, ct);
            }
            else if (msg.MessageType == WebSocketMessageType.Text)
            {
                var text = Encoding.UTF8.GetString(buffer, 0, msg.Count);
                _log.LogDebug("browser control: {Text}", text);
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var t = doc.RootElement.GetProperty("type").GetString();
                    if (t == "commit")
                    {
                        await SendJsonAsync(aoai, new { type = "input_audio_buffer.commit" }, ct);
                        await SendJsonAsync(aoai, new { type = "response.create" }, ct);
                    }
                    else if (t == "cancel")
                    {
                        await SendJsonAsync(aoai, new { type = "response.cancel" }, ct);
                    }
                }
                catch (JsonException) { /* ignore */ }
            }
        }
    }

    private async Task PumpAoaiToBrowserAsync(ClientWebSocket aoai, WebSocket browser, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested && aoai.State == WebSocketState.Open)
        {
            sb.Clear();
            WebSocketReceiveResult? result;
            do
            {
                result = await aoai.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            } while (!result.EndOfMessage);

            var json = sb.ToString();
            JsonElement evt;
            try { using var doc = JsonDocument.Parse(json); evt = doc.RootElement.Clone(); }
            catch { continue; }

            var type = evt.TryGetProperty("type", out var tp) ? tp.GetString() : null;
            switch (type)
            {
                case "response.audio.delta":
                {
                    if (evt.TryGetProperty("delta", out var d) && d.ValueKind == JsonValueKind.String)
                    {
                        var pcm = Convert.FromBase64String(d.GetString()!);
                        await browser.SendAsync(pcm, WebSocketMessageType.Binary, true, ct);
                    }
                    break;
                }
                case "response.audio_transcript.delta":
                case "response.audio_transcript.done":
                case "conversation.item.input_audio_transcription.completed":
                case "input_audio_buffer.speech_started":
                case "input_audio_buffer.speech_stopped":
                case "response.done":
                case "error":
                {
                    var fwd = Encoding.UTF8.GetBytes(json);
                    await browser.SendAsync(fwd, WebSocketMessageType.Text, true, ct);
                    if (type == "error") _log.LogWarning("AOAI error: {Json}", json);
                    break;
                }
            }
        }
    }

    private static async Task<WebSocketReceiveResult?> ReceiveFullAsync(WebSocket ws, byte[] buffer, CancellationToken ct)
    {
        try
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            return r;
        }
        catch { return null; }
    }

    private static async Task SendJsonAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
}
