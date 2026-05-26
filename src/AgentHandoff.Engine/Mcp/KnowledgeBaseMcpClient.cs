using AgentHandoff.Engine.Orchestration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace AgentHandoff.Engine.Mcp;

/// <summary>
/// Spawns the AgentHandoff.McpServer executable and exposes its tools as <see cref="AITool"/>s
/// the Technical Support agent can call. This follows the canonical Microsoft Agent Framework
/// pattern documented at https://learn.microsoft.com/agent-framework/agents/tools/local-mcp-tools.
/// </summary>
public sealed class KnowledgeBaseMcpClient : IAsyncDisposable
{
    private readonly ILogger<KnowledgeBaseMcpClient>? _log;
    private readonly Action<AgentEvent>? _onEvent;
    private IMcpClient? _client;
    private IList<AITool>? _tools;

    public KnowledgeBaseMcpClient(ILogger<KnowledgeBaseMcpClient>? log = null, Action<AgentEvent>? onEvent = null)
    {
        _log = log;
        _onEvent = onEvent;
    }

    /// <summary>
    /// Connects to the MCP server. <paramref name="serverDllPath"/> should point at the published
    /// AgentHandoff.McpServer.dll (or .exe) on disk.
    /// </summary>
    public async Task<IList<AITool>> ConnectAsync(string serverDllPath, CancellationToken ct = default)
    {
        var resolvedDllPath = Path.GetFullPath(serverDllPath);
        if (!OperatingSystem.IsWindows())
            resolvedDllPath = resolvedDllPath.Replace('\\', '/');

        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name      = "AgentHandoff.KnowledgeBase",
            Command   = "dotnet",
            Arguments = new[] { resolvedDllPath },
        });

        _log?.LogInformation("Starting MCP server: dotnet {Dll}", resolvedDllPath);
        _client = await McpClientFactory.CreateAsync(transport, cancellationToken: ct).ConfigureAwait(false);

        var mcpTools = await _client.ListToolsAsync(cancellationToken: ct).ConfigureAwait(false);
        _tools = mcpTools.Cast<AITool>().ToList();

        _log?.LogInformation("MCP server returned {Count} tools: {Names}",
            _tools.Count, string.Join(", ", _tools.Select(t => t.Name)));

        return _tools;
    }

    public IList<AITool> Tools => _tools ?? Array.Empty<AITool>();

    public async ValueTask DisposeAsync()
    {
        if (_client is IAsyncDisposable d)
        {
            await d.DisposeAsync().ConfigureAwait(false);
        }
    }
}
