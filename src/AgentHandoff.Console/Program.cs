using AgentHandoff.Engine;
using AgentHandoff.Engine.Configuration;
using AgentHandoff.Engine.Orchestration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// ----------------------------------------------------------------------------------------------
// Standalone console host. Lets you chat with the Customer Support Hub from a terminal,
// and prints every agent switch / handoff / tool call / guardrail decision inline.
// ----------------------------------------------------------------------------------------------

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .AddUserSecrets<Marker>(optional: true)
    .AddEnvironmentVariables()
    .Build();

// Load agent mesh configuration from JSON or YAML (YAML takes precedence if both exist)
var yamlPath = Path.Combine(AppContext.BaseDirectory, "appsettings.agents.yaml");
var jsonPath = Path.Combine(AppContext.BaseDirectory, "appsettings.agents.json");

if (File.Exists(yamlPath))
{
    // Load YAML configuration and convert to JSON
    var yamlMesh = AgentMeshYamlLoader.LoadFromYamlFile(yamlPath);
    var meshJson = System.Text.Json.JsonSerializer.Serialize(
        new { AgentMesh = yamlMesh },
        new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }
    );
    var tempBuilder = new ConfigurationBuilder()
        .AddJsonStream(new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(meshJson)));
    
    // Rebuild config with both base settings and YAML mesh
    config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddInMemoryCollection(tempBuilder.Build().AsEnumerable())
        .AddUserSecrets<Marker>(optional: true)
        .AddEnvironmentVariables()
        .Build();
}
else if (File.Exists(jsonPath))
{
    config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
        .AddJsonFile(jsonPath, optional: false, reloadOnChange: false)
        .AddUserSecrets<Marker>(optional: true)
        .AddEnvironmentVariables()
        .Build();
}
else
{
    throw new InvalidOperationException(
        $"Neither appsettings.agents.yaml nor appsettings.agents.json found. Checked: {yamlPath}, {jsonPath}");
}

var azureOptions = new AzureOpenAIOptions();
config.GetSection(AzureOpenAIOptions.SectionName).Bind(azureOptions);

if (string.IsNullOrWhiteSpace(azureOptions.Endpoint))
{
    Console.Error.WriteLine("ERROR: AzureOpenAI:Endpoint is not configured.");
    Console.Error.WriteLine("Set it via:");
    Console.Error.WriteLine("  dotnet user-secrets set \"AzureOpenAI:Endpoint\" \"https://<your>.openai.azure.com/\"");
    Console.Error.WriteLine("  dotnet user-secrets set \"AzureOpenAI:ApiKey\" \"<your-key>\"");
    Console.Error.WriteLine("  dotnet user-secrets set \"AzureOpenAI:DeploymentName\" \"gpt-4o-mini\"");
    return 1;
}

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
}).SetMinimumLevel(LogLevel.Information));

var mcpOptions = new McpOptions();
config.GetSection(McpOptions.SectionName).Bind(mcpOptions);

var meshOptions = new AgentMeshOptions();
config.GetSection(AgentMeshOptions.SectionName).Bind(meshOptions);
AgentMeshValidator.ValidateAndBuildRuntime(meshOptions);

// If MCP mode is not explicitly configured, use embedded mode with resolved DLL
if (string.IsNullOrWhiteSpace(mcpOptions.Mode))
    mcpOptions.Mode = "Embedded";

if (mcpOptions.IsEmbeddedMode && string.IsNullOrWhiteSpace(mcpOptions.ServerPath))
{
    mcpOptions.ServerPath = ResolveMcpServerDll();
}

await using var engine = new CustomerSupportEngine(azureOptions, meshOptions, loggerFactory, onEvent: PrintEvent);
await engine.StartAsync(mcpOptions);
var orchestrator = engine.CreateOrchestrator();

Console.WriteLine();
Console.WriteLine("========================================================");
Console.WriteLine("  Customer Support Hub — multi-agent demo");
Console.WriteLine("========================================================");
Console.WriteLine($"  Azure deployment : {azureOptions.DeploymentName}");
if (mcpOptions.IsRemoteMode)
    Console.WriteLine($"  MCP server       : {mcpOptions.ServerPath} (remote)");
else
    Console.WriteLine($"  MCP server       : {(mcpOptions.ServerPath is null ? "(not running)" : $"{mcpOptions.ServerPath} (embedded)")}");
Console.WriteLine("  Agents:");
foreach (var a in orchestrator.Agents)
    Console.WriteLine($"    - [{a.Role,-10}] {a.Id,-15} → {a.Description}");
Console.WriteLine("--------------------------------------------------------");
Console.WriteLine("  Type your message and press Enter. Type /exit to quit.");
Console.WriteLine("========================================================");
Console.WriteLine();

while (true)
{
    Console.Write("You> ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input)) continue;
    if (input.Equals("/exit", StringComparison.OrdinalIgnoreCase)) break;
    if (input.Equals("/reset", StringComparison.OrdinalIgnoreCase)) { orchestrator.Reset(); Console.WriteLine("(history cleared)"); continue; }

    Console.WriteLine();
    await foreach (var evt in orchestrator.ChatAsync(input))
    {
        // The PrintEvent callback already printed it.
        _ = evt;
    }
    Console.WriteLine();
    Console.WriteLine();
}

return 0;

static string? ResolveMcpServerDll()
{
    // Look for the MCP server output dir relative to the bin folder.
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AgentHandoff.McpServer", "bin", "Debug", "net8.0", "AgentHandoff.McpServer.dll"),
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "AgentHandoff.McpServer", "bin", "Release", "net8.0", "AgentHandoff.McpServer.dll"),
    };

    foreach (var c in candidates)
    {
        var full = Path.GetFullPath(c);
        if (File.Exists(full)) return full;
    }
    return null;
}

static void PrintEvent(AgentEvent evt)
{
    var ts = evt.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
    switch (evt)
    {
        case AgentSwitchedEvent s:
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine();
            Console.WriteLine($"[{ts}] >>> {s.AgentDisplayName} ({s.Role}) is responding...");
            Console.ResetColor();
            break;
        case HandoffEvent h:
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[{ts}] ~~~ HANDOFF: {h.FromAgentId} → {h.ToAgentId} ({h.Reason})");
            Console.ResetColor();
            break;
        case AgentTokenEvent t:
            Console.Write(t.Token);
            break;
        case ToolCallEvent c:
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine();
            Console.WriteLine($"[{ts}] [tool/{c.Source}] {c.AgentId} → {c.ToolName}({c.ArgumentsJson})");
            Console.ResetColor();
            break;
        case GuardrailEvent g:
            Console.ForegroundColor = g.Verdict == "blocked" ? ConsoleColor.Red : ConsoleColor.DarkMagenta;
            Console.WriteLine();
            Console.WriteLine($"[{ts}] [guardrail/{g.Stage}] {g.AgentId}: {g.Verdict} — {g.Reason}");
            Console.ResetColor();
            break;
        case TurnCompletedEvent:
            Console.WriteLine();
            break;
    }
}

internal sealed class Marker { }
