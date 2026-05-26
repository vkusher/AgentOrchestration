using System.Text.Json;
using AgentHandoff.Api.Models;
using AgentHandoff.Api.Services;
using AgentHandoff.Engine;
using AgentHandoff.Engine.Approvals;
using AgentHandoff.Engine.Approvals.EventGrid;
using AgentHandoff.Engine.Configuration;
using AgentHandoff.Engine.Orchestration;
using AgentHandoff.Engine.Sessions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Load agent mesh configuration from JSON or YAML (YAML takes precedence if both exist)
var yamlPath = Path.Combine(AppContext.BaseDirectory, "appsettings.agents.yaml");
var jsonPath = Path.Combine(AppContext.BaseDirectory, "appsettings.agents.json");

// Try to locate the source-tree copy of the YAML so admin edits survive a rebuild.
// Walk up from the binary output directory looking for "AgentHandoff.Api.csproj"
// next to an "appsettings.agents.yaml". Falls back to the runtime path.
static string? TryLocateSourceYaml()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
    {
        // Heuristic: stop when we hit the repo root (contains src/AgentHandoff.Api)
        var candidate = Path.Combine(dir.FullName, "src", "AgentHandoff.Api", "appsettings.agents.yaml");
        if (File.Exists(candidate)) return candidate;
        var sibling = Path.Combine(dir.FullName, "appsettings.agents.yaml");
        if (File.Exists(sibling) && File.Exists(Path.Combine(dir.FullName, "AgentHandoff.Api.csproj")))
            return sibling;
    }
    return null;
}

var sourceYamlPath = Environment.GetEnvironmentVariable("AGENTHANDOFF_AGENTS_YAML_PATH") ?? TryLocateSourceYaml();

if (File.Exists(yamlPath))
{
    // Load YAML configuration and add as JSON
    var yamlMesh = AgentMeshYamlLoader.LoadFromYamlFile(yamlPath);
    var meshJson = System.Text.Json.JsonSerializer.Serialize(
        new { AgentMesh = yamlMesh },
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }
    );
    // Create a MemoryStream - keep it open (configuration system will manage it)
    var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(meshJson));
    stream.Position = 0;
    builder.Configuration.AddJsonStream(stream);
}
else if (File.Exists(jsonPath))
{
    builder.Configuration.AddJsonFile(jsonPath, optional: false, reloadOnChange: true);
}
else
{
    throw new InvalidOperationException(
        $"Neither appsettings.agents.yaml nor appsettings.agents.json found. Checked: {yamlPath}, {jsonPath}");
}

builder.Services.AddOptions<AzureOpenAIOptions>()
    .Bind(builder.Configuration.GetSection(AzureOpenAIOptions.SectionName));

builder.Services.AddOptions<FoundryAuthOptions>()
    .Bind(builder.Configuration.GetSection(FoundryAuthOptions.SectionName));

builder.Services.AddOptions<AnthropicOptions>()
    .Bind(builder.Configuration.GetSection(AnthropicOptions.SectionName));

builder.Services.AddOptions<AgentMeshOptions>()
    .Bind(builder.Configuration.GetSection(AgentMeshOptions.SectionName));

builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<AgentMeshOptions>>().Value;
    AgentMeshValidator.ValidateAndBuildRuntime(opts);
    return opts;
});

// MCP configuration — supports both Embedded (subprocess) and Remote (HTTP) modes
// Note: Read explicitly from IConfiguration with proper env var precedence
builder.Services.AddOptions<McpOptions>()
    .Configure(opts =>
    {
        var mcpSection = builder.Configuration.GetSection(McpOptions.SectionName);
        
        // Read Mode (respects env var Mcp__Mode)
        var mode = builder.Configuration["Mcp:Mode"];
        if (!string.IsNullOrWhiteSpace(mode))
            opts.Mode = mode;
        
        // Read ServerPath (respects env var Mcp__ServerPath)
        var serverPath = builder.Configuration["Mcp:ServerPath"];
        if (!string.IsNullOrWhiteSpace(serverPath))
            opts.ServerPath = serverPath;
        
        // Read ServerDllPath (respects env var Mcp__ServerDllPath)
        var serverDllPath = builder.Configuration["Mcp:ServerDllPath"];
        if (!string.IsNullOrWhiteSpace(serverDllPath))
            opts.ServerDllPath = serverDllPath;
    })
    .PostConfigure(opts =>
    {
        // Backward compatibility: if old Mcp:ServerDllPath is set but ServerPath is not, use it
        if (opts.ServerPath is null && !string.IsNullOrWhiteSpace(opts.ServerDllPath))
        {
            opts.ServerPath = opts.ServerDllPath;
        }
    });

// Resolve the MCP server configuration from appsettings.
builder.Services.AddSingleton(sp =>
{
    var mcpOpts = sp.GetRequiredService<IOptions<McpOptions>>().Value;
    
    // For Embedded mode, resolve DLL path using convention if not explicitly set
    if (mcpOpts.IsEmbeddedMode && string.IsNullOrWhiteSpace(mcpOpts.ServerPath))
    {
        static string? TryResolveMcpDllPath(string? rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
                return null;

            var candidate = rawPath.Trim();
            if (!OperatingSystem.IsWindows())
                candidate = candidate.Replace('\\', '/');

            string full;
            try
            {
                full = Path.GetFullPath(candidate);
            }
            catch
            {
                return null;
            }

            if (!File.Exists(full))
                return null;

            var runtimeConfig = Path.ChangeExtension(full, ".runtimeconfig.json");
            return File.Exists(runtimeConfig) ? full : null;
        }

        static string? FindPublishedMcpServerDll()
        {
            try
            {
                return Directory.EnumerateFiles(
                        AppContext.BaseDirectory,
                        "AgentHandoff.McpServer.dll",
                        SearchOption.AllDirectories)
                    .Select(TryResolveMcpDllPath)
                    .OfType<string>()
                    .OrderByDescending(path => path.Contains("/mcpserver/", StringComparison.OrdinalIgnoreCase)
                                            || path.Contains("\\mcpserver\\", StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        var resolvedFromConfig = TryResolveMcpDllPath(mcpOpts.ServerPath);
        if (!string.IsNullOrWhiteSpace(resolvedFromConfig))
        {
            mcpOpts.ServerPath = resolvedFromConfig;
            return mcpOpts;
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "mcpserver", "AgentHandoff.McpServer.dll"),
                     Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AgentHandoff.McpServer", "bin", "Debug",   "net8.0", "AgentHandoff.McpServer.dll"),
                     Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AgentHandoff.McpServer", "bin", "Release", "net8.0", "AgentHandoff.McpServer.dll"),
                 })
        {
            var resolved = TryResolveMcpDllPath(candidate);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                mcpOpts.ServerPath = resolved;
                return mcpOpts;
            }
        }

        mcpOpts.ServerPath = FindPublishedMcpServerDll();
    }

    return mcpOpts;
});

// One Engine per process — it's expensive to construct (spawns the MCP server child process
// and builds the agent bundle).
builder.Services.AddOptions<ApprovalOptions>()
    .Bind(builder.Configuration.GetSection(ApprovalOptions.SectionName));
builder.Services.AddOptions<SessionRegistryOptions>()
    .Bind(builder.Configuration.GetSection(SessionRegistryOptions.SectionName));
builder.Services.AddOptions<EventGridApprovalOptions>()
    .Bind(builder.Configuration.GetSection(EventGridApprovalOptions.SectionName));

// Approval publisher — EventGrid namespace topic when enabled, no-op otherwise.
builder.Services.AddSingleton<IApprovalPublisher>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<EventGridApprovalOptions>>().Value;
    if (!opts.Enabled) return new NullApprovalPublisher();
    var lf = sp.GetRequiredService<ILoggerFactory>();
    return new EventGridApprovalPublisher(opts, lf.CreateLogger<EventGridApprovalPublisher>());
});

// Approval dispatcher — shared by HTTP endpoints and the EventGrid listener.
builder.Services.AddSingleton<IApprovalDispatcher, SessionStoreApprovalDispatcher>();

// Session registry — selectable backend. Default: in-memory (process-local).
builder.Services.AddSingleton<ISessionRegistry>(sp =>
{
    var opts = sp.GetRequiredService<IOptions<SessionRegistryOptions>>().Value;
    var lf   = sp.GetRequiredService<ILoggerFactory>();

    if (string.Equals(opts.Provider, "Cosmos", StringComparison.OrdinalIgnoreCase))
    {
        var cosmos = opts.Cosmos;
        if (string.IsNullOrWhiteSpace(cosmos.AccountEndpoint))
            throw new InvalidOperationException("SessionRegistry:Cosmos:AccountEndpoint is required when Provider=Cosmos.");

        var client = string.IsNullOrWhiteSpace(cosmos.AccountKey)
            ? new CosmosClient(cosmos.AccountEndpoint, new Azure.Identity.DefaultAzureCredential())
            : new CosmosClient(cosmos.AccountEndpoint, cosmos.AccountKey);

        var container = CosmosSessionRegistry.EnsureContainerAsync(client, cosmos).GetAwaiter().GetResult();
        return new CosmosSessionRegistry(container, lf.CreateLogger<CosmosSessionRegistry>());
    }

    return new InMemorySessionRegistry();
});
builder.Services.AddSingleton(sp =>
{
    var opts      = sp.GetRequiredService<IOptions<AzureOpenAIOptions>>().Value;
    var mesh      = sp.GetRequiredService<AgentMeshOptions>();
    var lf        = sp.GetRequiredService<ILoggerFactory>();
    var registry  = sp.GetRequiredService<ISessionRegistry>();
    var approval  = sp.GetRequiredService<IOptions<ApprovalOptions>>().Value;
    var publisher = sp.GetRequiredService<IApprovalPublisher>();
    var foundry   = sp.GetRequiredService<IOptions<FoundryAuthOptions>>().Value;
    var anthropic = sp.GetRequiredService<IOptions<AnthropicOptions>>().Value;
    return new CustomerSupportEngine(opts, mesh, lf, onEvent: null, registry: registry,
        approvalOptions: approval, approvalPublisher: publisher, foundryAuth: foundry, anthropic: anthropic);
});

builder.Services.AddSingleton<SessionStore>();
builder.Services.AddHostedService<ApprovalSweeper>();

// Attachment preprocessing (text inlining + MCP-OCR for binary files).
builder.Services.AddHttpClient();
builder.Services.AddSingleton<AgentHandoff.Api.Services.AttachmentPreprocessor>();

// Conditionally register the EventGrid pull listener.
if (builder.Configuration.GetSection(EventGridApprovalOptions.SectionName).GetValue<bool>("Enabled"))
{
    builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<EventGridApprovalOptions>>().Value);
    builder.Services.AddHostedService<EventGridApprovalListener>();
}

builder.Services.AddCors(o =>
{
    var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
                  ?? new[] { "http://localhost:5173", "http://localhost:4173" };

    o.AddDefaultPolicy(p => p
        .WithOrigins(origins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddHttpLogging(_ => { });

var app = builder.Build();

app.UseCors();

app.UseDefaultFiles();
app.UseStaticFiles();

// Ensure the engine is ready (spawns MCP, builds the agent bundle) before the first turn.
async Task EnsureEngineStartedAsync(IServiceProvider sp, CancellationToken ct)
{
    var engine = sp.GetRequiredService<CustomerSupportEngine>();
    var mcpOpts = sp.GetRequiredService<McpOptions>();
    var log    = sp.GetRequiredService<ILoggerFactory>().CreateLogger("AgentHandoff.Api.McpStartup");

    if (string.IsNullOrWhiteSpace(mcpOpts.ServerPath))
        log.LogWarning("MCP server was not configured. Running without MCP tools.");
    else if (mcpOpts.IsRemoteMode)
        log.LogInformation("Using remote MCP mode at: {McpUrl}", mcpOpts.ServerPath);
    else
        log.LogInformation("Using embedded MCP mode with DLL: {McpDllPath}", mcpOpts.ServerPath);

    // Do not couple process-level engine init to an individual HTTP request lifetime.
    await engine.StartAsync(mcpOpts, CancellationToken.None);
}

// ----------------------------------------------------------------------------------------------
// GET /api/agents → list of agents the orchestrator knows about
// ----------------------------------------------------------------------------------------------
app.MapGet("/api/agents", async (HttpContext ctx, CustomerSupportEngine engine) =>
{
    await EnsureEngineStartedAsync(ctx.RequestServices, ctx.RequestAborted);
    var sample = engine.CreateOrchestrator();
    // Agents are shared between Handoff and Magentic — listing once is sufficient.
    var dto = new AgentsResponse(sample.Agents
        .Select(a => new AgentInfo(a.Id, a.DisplayName, a.Role, a.Description))
        .ToList());
    return Results.Json(dto);
});

// ----------------------------------------------------------------------------------------------
// POST /api/chat/stream → Server-Sent Events stream of agent events for one user turn
// ----------------------------------------------------------------------------------------------
app.MapPost("/api/chat/stream", async (
    [FromBody] ChatRequest request,
    HttpContext ctx,
    SessionStore store,
    CustomerSupportEngine engine,
    AgentHandoff.Api.Services.AttachmentPreprocessor attachments) =>
{
    var hasAttachments = request.Attachments is { Count: > 0 };
    if (string.IsNullOrWhiteSpace(request.Message) && !hasAttachments)
    {
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        await ctx.Response.WriteAsync("Message or at least one attachment is required.");
        return;
    }

    await EnsureEngineStartedAsync(ctx.RequestServices, ctx.RequestAborted);

    var sessionId = string.IsNullOrWhiteSpace(request.SessionId) ? "default" : request.SessionId;
    var mode      = string.Equals(request.Mode, "magentic", StringComparison.OrdinalIgnoreCase) ? "magentic" : "handoff";

    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";
    ctx.Response.Headers.Connection = "keep-alive";
    var bufferingFeature = ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>();
    bufferingFeature?.DisableBuffering();

    var jsonOpts = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    async Task SendAsync(string evtName, object payload)
    {
        // For AgentEvent, serialize as the base type so the polymorphic "type" discriminator is emitted.
        var json = payload is AgentEvent ae
            ? JsonSerializer.Serialize<AgentEvent>(ae, jsonOpts)
            : JsonSerializer.Serialize(payload, payload.GetType(), jsonOpts);

        await ctx.Response.WriteAsync($"event: {evtName}\n", ctx.RequestAborted);
        await ctx.Response.WriteAsync($"data: {json}\n\n", ctx.RequestAborted);
        await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
    }

    await SendAsync("ready", new { sessionId, mode, ts = DateTimeOffset.UtcNow });

    var augmentedMessage = await attachments
        .BuildAugmentedMessageAsync(request.Message ?? string.Empty, request.Attachments, ctx.RequestAborted)
        .ConfigureAwait(false);

    if (string.IsNullOrWhiteSpace(augmentedMessage))
    {
        await SendAsync("error", new { message = "All attachments were empty/invalid and no text was provided.", ts = DateTimeOffset.UtcNow });
        return;
    }

    try
    {
        // Route to the appropriate orchestrator based on mode.
        if (mode == "magentic")
        {
            var orchestrator = store.GetOrAddMagentic(sessionId, sid => engine.CreateMagenticOrchestrator(sid));
            await foreach (var evt in orchestrator.ChatAsync(augmentedMessage, cancellationToken: ctx.RequestAborted))
                await SendAsync("agent", evt);
        }
        else
        {
            var orchestrator = store.GetOrAddHandoff(sessionId, sid => engine.CreateOrchestrator(sid));
            await foreach (var evt in orchestrator.ChatAsync(augmentedMessage, cancellationToken: ctx.RequestAborted))
                await SendAsync("agent", evt);
        }

        await SendAsync("done", new { sessionId, mode, ts = DateTimeOffset.UtcNow });
    }
    catch (OperationCanceledException) { /* client disconnected */ }
    catch (Exception ex)
    {
        await SendAsync("error", new { message = ex.Message, ts = DateTimeOffset.UtcNow });
    }
});

// ----------------------------------------------------------------------------------------------
// POST /api/chat/reset/{sessionId}
// ----------------------------------------------------------------------------------------------
app.MapPost("/api/chat/reset/{sessionId}", (string sessionId, SessionStore store) =>
{
    store.Reset(sessionId);
    return Results.Ok(new { sessionId, reset = true });
});

// ----------------------------------------------------------------------------------------------
// POST /api/chat/approve → respond to an in-flight tool-approval request
// ----------------------------------------------------------------------------------------------
app.MapPost("/api/chat/approve", ([FromBody] ApprovalDecisionRequest req, SessionStore store) =>
{
    if (store.TryGetHandoff(req.SessionId, out var handoff))
    {
        var resolved = handoff.ProvideApproval(req.ApprovalId, req.Approved);
        return Results.Ok(new { req.SessionId, req.ApprovalId, req.Approved, resolved });
    }
    if (store.TryGetMagentic(req.SessionId, out var magentic))
    {
        var resolved = magentic.ProvideApproval(req.ApprovalId, req.Approved);
        return Results.Ok(new { req.SessionId, req.ApprovalId, req.Approved, resolved });
    }
    return Results.NotFound(new { req.SessionId, error = "Unknown session." });
});

// ----------------------------------------------------------------------------------------------
// Session + approval observability endpoints (in-memory registry)
// ----------------------------------------------------------------------------------------------
app.MapGet("/api/sessions", (ISessionRegistry registry, [FromQuery] string? status) =>
{
    SessionStatus? filter = null;
    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<SessionStatus>(status, ignoreCase: true, out var s))
        filter = s;
    return Results.Ok(registry.ListSessions(filter));
});

app.MapGet("/api/sessions/{sessionId}", (string sessionId, ISessionRegistry registry) =>
{
    var s = registry.GetSession(sessionId);
    return s is null ? Results.NotFound(new { sessionId }) : Results.Ok(s);
});

app.MapGet("/api/sessions/{sessionId}/audit", (string sessionId, ISessionRegistry registry) =>
    Results.Ok(registry.GetAudit(sessionId)));

app.MapGet("/api/approvals", (ISessionRegistry registry,
                              [FromQuery] string? status,
                              [FromQuery] string? sessionId) =>
{
    ApprovalStatus? filter = null;
    if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<ApprovalStatus>(status, ignoreCase: true, out var a))
        filter = a;
    return Results.Ok(registry.ListApprovals(filter, sessionId));
});

app.MapGet("/api/approvals/{approvalId}", (string approvalId, ISessionRegistry registry) =>
{
    var a = registry.GetApproval(approvalId);
    return a is null ? Results.NotFound(new { approvalId }) : Results.Ok(a);
});

app.MapPost("/api/approvals/{approvalId}/decision",
    (string approvalId, [FromBody] ApprovalDecisionBody body, ISessionRegistry registry, SessionStore store) =>
{
    var pending = registry.GetApproval(approvalId);
    if (pending is null)
        return Results.NotFound(new { approvalId, error = "Unknown approval id." });

    if (store.TryGetHandoff(pending.SessionId, out var handoff))
    {
        var resolved = handoff.ProvideApproval(approvalId, body.Approved, body.DecidedBy, body.Reason);
        return Results.Ok(new { approvalId, pending.SessionId, body.Approved, resolved });
    }
    if (store.TryGetMagentic(pending.SessionId, out var magentic))
    {
        var resolved = magentic.ProvideApproval(approvalId, body.Approved, body.DecidedBy, body.Reason);
        return Results.Ok(new { approvalId, pending.SessionId, body.Approved, resolved });
    }
    return Results.NotFound(new { approvalId, pending.SessionId, error = "Session not active." });
});

// ----------------------------------------------------------------------------------------------
// Admin: YAML-backed agent mesh editor (UI: /admin/index.html)
// ----------------------------------------------------------------------------------------------
app.MapGet("/api/admin/mesh", () =>
{
    try
    {
        var loadPath = File.Exists(yamlPath) ? yamlPath : sourceYamlPath;
        if (loadPath is null || !File.Exists(loadPath))
            return Results.NotFound(new { error = "appsettings.agents.yaml not found.", yamlPath, sourceYamlPath });
        var mesh = AgentMeshYamlLoader.LoadFromYamlFile(loadPath);
        return Results.Json(new
        {
            mesh,
            paths = new { runtime = yamlPath, source = sourceYamlPath, loaded = loadPath }
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "Failed to load mesh.", detail: ex.Message);
    }
});

app.MapPut("/api/admin/mesh", async (HttpContext ctx) =>
{
    AgentMeshOptions? mesh;
    try
    {
        mesh = await System.Text.Json.JsonSerializer.DeserializeAsync<AgentMeshOptions>(
            ctx.Request.Body,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = "Invalid JSON body.", detail = ex.Message });
    }

    if (mesh is null)
        return Results.BadRequest(new { error = "Empty body." });

    try
    {
        AgentMeshValidator.ValidateAndBuildRuntime(mesh);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = "Mesh validation failed.", detail = ex.Message });
    }

    var written = new List<string>();
    try
    {
        AgentMeshYamlLoader.SaveToYamlFile(yamlPath, mesh);
        written.Add(yamlPath);
        if (!string.IsNullOrEmpty(sourceYamlPath)
            && !string.Equals(Path.GetFullPath(sourceYamlPath), Path.GetFullPath(yamlPath), StringComparison.OrdinalIgnoreCase))
        {
            AgentMeshYamlLoader.SaveToYamlFile(sourceYamlPath, mesh);
            written.Add(sourceYamlPath);
        }
    }
    catch (Exception ex)
    {
        return Results.Problem(title: "Failed to write YAML.", detail: ex.Message);
    }

    return Results.Ok(new
    {
        saved = true,
        written,
        note = "Restart the API process to apply changes to the running engine."
    });
});

app.MapGet("/api/admin/catalog", (IOptions<AgentMeshOptions> meshOpts) =>
{
    var current = meshOpts.Value;

    var tools = new object[]
    {
        new { key = "mcp.search_knowledge_base",      kind = "mcp",   description = "RAG over the configured knowledge base." },
        new { key = "local.get_transaction_status",   kind = "local", description = "Lookup status of a transaction by id." },
        new { key = "local.request_card_replacement", kind = "local", description = "Mail the customer a replacement card." },
        new { key = "local.lookup_balance",           kind = "local", description = "Return the balance of an account." },
        new { key = "local.issue_refund",             kind = "local", description = "Credit a previously charged fee (gated)." },
        new { key = "local.submit_transfer",          kind = "local", description = "Submit a money-transfer (gated)." },
        new { key = "mcp.extract_transfer_request",   kind = "mcp",   description = "Extract money-transfer fields from free text / OCR." },
        new { key = "mcp.resolve_bank",               kind = "mcp",   description = "Resolve a bank from a name / hint." },
        new { key = "mcp.validate_account",           kind = "mcp",   description = "Validate account-number format for a bank." },
        new { key = "mcp.ocr_document",               kind = "mcp",   description = "Extract text from a PDF/image (base64)." },
        new { key = "mcp.ingest_mortgage_bundle",     kind = "mcp",   description = "Register a mortgage document bundle." },
        new { key = "mcp.compute_required_documents", kind = "mcp",   description = "Compute required docs for a mortgage profile." },
        new { key = "mcp.classify_document",          kind = "mcp",   description = "Classify a single mortgage document." },
        new { key = "mcp.authenticate_document",      kind = "mcp",   description = "Verify a document is genuine (anti-forgery)." },
        new { key = "mcp.emit_validation_report",     kind = "mcp",   description = "Aggregate validation rows into a final report." },
    };

    var agentTools = current.Agents.Select(a => (object)new
    {
        key = $"agent.{a.Id}",
        kind = "agent",
        description = $"Invoke agent '{a.DisplayName}' as a tool."
    }).ToArray();

    var transports = new[]
    {
        new { value = "in_process",     description = "Local in-process agent." },
        new { value = "in_process_a2a", description = "Local agent wrapped in the A2A adapter." },
        new { value = "foundry",        description = "Hosted Microsoft AI Foundry agent (asst_*)." },
        new { value = "anthropic",      description = "Anthropic-backed (Claude) chat completion." },
    };

    var roles = new[] { "router", "planner", "specialist" };

    var middlewares = new[]
    {
        new { name = "GuardrailMiddleware",   scope = "per-agent (auto)",      description = "Pre/post content-safety + blocklist guard. Wired automatically into every agent." },
        new { name = "MetricsChatClient",     scope = "per-chat-client (auto)", description = "Token / cost accounting on every model call." },
        new { name = "ContentSafetyAnalyzer", scope = "per-agent (auto)",      description = "Azure AI Content Safety analyzer used by the guardrail." },
    };

    return Results.Json(new
    {
        tools = tools.Concat(agentTools).ToArray(),
        transports,
        roles,
        middlewares
    });
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", ts = DateTimeOffset.UtcNow }));

app.MapGet("/api/debug/mcp", async (HttpContext ctx) =>
{
    var sp = ctx.RequestServices;
    var mcpOpts = sp.GetRequiredService<McpOptions>();
    var engine = sp.GetRequiredService<CustomerSupportEngine>();

    await EnsureEngineStartedAsync(sp, ctx.RequestAborted);

    var baseDir = AppContext.BaseDirectory;
    var discovered = Array.Empty<string>();
    try
    {
        discovered = Directory.EnumerateFiles(baseDir, "*AgentHandoff.McpServer*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Take(20)
            .ToArray();
    }
    catch
    {
        // Keep diagnostics resilient if directory traversal fails.
    }

    var pathExists = false;
    if (mcpOpts.IsRemoteMode)
    {
        // For remote mode, the "path" is a URL; just check it's set
        pathExists = !string.IsNullOrWhiteSpace(mcpOpts.ServerPath);
    }
    else
    {
        // For embedded mode, check file exists
        pathExists = !string.IsNullOrWhiteSpace(mcpOpts.ServerPath) && File.Exists(mcpOpts.ServerPath);
    }

    return Results.Ok(new
    {
        baseDir,
        mcpMode = mcpOpts.Mode,
        resolvedServerPath = mcpOpts.ServerPath,
        resolvedPathExists = pathExists,
        lastMcpServerPathTried = engine.LastMcpServerPathTried,
        lastMcpToolCount = engine.LastMcpToolCount,
        lastMcpInitError = engine.LastMcpInitError,
        discovered
    });
});

app.Run();
