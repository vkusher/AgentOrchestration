using System.Net.WebSockets;
using AgentHandoff.VoiceGateway.Realtime;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<RealtimeOptions>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>().GetSection("AzureOpenAIRealtime");
    var apiKey = cfg["ApiKey"];
    if (string.IsNullOrWhiteSpace(apiKey))
        apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_REALTIME_KEY")
              ?? Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");
    return new RealtimeOptions(
        Endpoint:    cfg["Endpoint"]    ?? throw new InvalidOperationException("AzureOpenAIRealtime:Endpoint missing"),
        Deployment:  cfg["Deployment"]  ?? throw new InvalidOperationException("AzureOpenAIRealtime:Deployment missing"),
        ApiVersion:  cfg["ApiVersion"]  ?? "2024-10-01-preview",
        ApiKey:      apiKey             ?? throw new InvalidOperationException("AzureOpenAIRealtime:ApiKey missing (or set AZURE_OPENAI_REALTIME_KEY)"));
});

builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(15),
});

app.MapHealthChecks("/healthz");

// GET /ws/voice  — bidirectional audio bridge.
// Browser sends binary frames (PCM16 mono 24 kHz, little-endian).
// Browser receives binary frames (PCM16 mono 24 kHz) for playback,
// and text frames (JSON) for transcripts / status.
app.Map("/ws/voice", async (HttpContext ctx, RealtimeOptions opts, ILoggerFactory lf) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("WebSocket required");
        return;
    }

    using var clientWs = await ctx.WebSockets.AcceptWebSocketAsync();
    var bridge = new RealtimeBridge(opts, lf.CreateLogger<RealtimeBridge>());
    try
    {
        await bridge.RunAsync(clientWs, ctx.RequestAborted);
    }
    catch (Exception ex)
    {
        lf.CreateLogger("voice").LogError(ex, "voice bridge failed");
    }
});

app.Run();
