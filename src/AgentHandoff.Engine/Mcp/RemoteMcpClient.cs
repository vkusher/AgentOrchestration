using System.ComponentModel;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.Engine.Mcp;

/// <summary>
/// Connects to a remote MCP server exposed over HTTP (e.g., AgentHandoff.McpServerWeb).
/// This client communicates with the remote server via HTTP REST endpoints that expose
/// MCP tool discovery.
///
/// Note: This is a simplified implementation for the preview. In production, this would
/// need to properly deserialize and expose the remote tools to the agent framework.
/// </summary>
public sealed class RemoteMcpClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger<RemoteMcpClient>? _log;
    private readonly string _serverUrl;
    private readonly HttpClient _httpClient;
    private IList<AITool>? _tools;

    public RemoteMcpClient(string serverUrl, ILogger<RemoteMcpClient>? log = null)
    {
        _serverUrl = serverUrl ?? throw new ArgumentNullException(nameof(serverUrl));
        _log = log;
        _httpClient = new HttpClient { BaseAddress = new Uri(serverUrl) };
    }

    /// <summary>
    /// Connects to the remote MCP server and retrieves available tools.
    /// </summary>
    public async Task<IList<AITool>> ConnectAsync(CancellationToken ct = default)
    {
        try
        {
            _log?.LogInformation("Connecting to remote MCP server at {ServerUrl}", _serverUrl);

            // Call remote endpoint to list tools
            var response = await _httpClient.GetAsync("/mcp/tools", ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var jsonContent = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log?.LogDebug("Remote MCP response: {Response}", jsonContent);

            var payload = JsonSerializer.Deserialize<ToolListResponse>(jsonContent, _json)
                          ?? new ToolListResponse();

            var discovered = payload.Tools ?? [];
            var wrappers = new List<AITool>();

            foreach (var tool in discovered)
            {
                if (tool.Name.Equals("SearchKnowledgeBase", StringComparison.OrdinalIgnoreCase))
                {
                    wrappers.Add(AIFunctionFactory.Create(SearchKnowledgeBase));
                }
                else if (tool.Name.Equals("ListTopics", StringComparison.OrdinalIgnoreCase))
                {
                    wrappers.Add(AIFunctionFactory.Create(ListTopics));
                }
                else if (tool.Name.Equals("ExtractTransferRequest", StringComparison.OrdinalIgnoreCase))
                {
                    wrappers.Add(AIFunctionFactory.Create(ExtractTransferRequest));
                }
                else if (tool.Name.Equals("OcrDocument", StringComparison.OrdinalIgnoreCase))
                {
                    wrappers.Add(AIFunctionFactory.Create(OcrDocument));
                }
                else if (tool.Name.Equals("ResolveBank", StringComparison.OrdinalIgnoreCase))
                {
                    wrappers.Add(AIFunctionFactory.Create(ResolveBank));
                }
                else if (tool.Name.Equals("ValidateAccount", StringComparison.OrdinalIgnoreCase))
                {
                    wrappers.Add(AIFunctionFactory.Create(ValidateAccount));
                }
                else
                {
                    _log?.LogWarning("Remote MCP tool '{ToolName}' is advertised but has no executable wrapper.", tool.Name);
                }
            }

            _tools = wrappers;

            _log?.LogInformation(
                "Remote MCP server connected successfully. Discovered {DiscoveredCount} tool(s), wrapped {WrappedCount} tool(s).",
                discovered.Count,
                wrappers.Count);

            return _tools;
        }
        catch (Exception ex)
        {
            _log?.LogError(ex, "Failed to connect to remote MCP server at {ServerUrl}", _serverUrl);
            throw;
        }
    }

    [Description("Search the knowledge base for relevant information using semantic search.")]
    public Task<string> SearchKnowledgeBase(
        [Description("Free-text query from the customer question.")] string query,
        CancellationToken cancellationToken = default)
        => ExecuteToolAsync(
            "SearchKnowledgeBase",
            new Dictionary<string, object?> { ["query"] = query },
            cancellationToken);

    [Description("List available knowledge-base topic facets.")]
    public Task<string> ListTopics(CancellationToken cancellationToken = default)
        => ExecuteToolAsync("ListTopics", arguments: null, cancellationToken);

    [Description(
        "Extract a money-transfer request (English or Hebrew) from free text or an attached PDF/image. " +
        "Provide one of 'text', 'blobUri' (absolute https URL with SAS if private), or 'base64' (inline file bytes). " +
        "Returns strict JSON with per-field confidence and a 'toBank' that defaults to 'Discount' " +
        "(source='default') when no bank is named.")]
    public Task<string> ExtractTransferRequest(
        [Description("Free-text transfer request. Required when blobUri and base64 are both empty.")] string? text = null,
        [Description("Optional absolute https URL to a PDF/image of the request.")] string? blobUri = null,
        [Description("Optional base64-encoded PDF/image bytes (use when no blob is available).")] string? base64 = null,
        CancellationToken cancellationToken = default)
        => ExecuteToolAsync(
            "ExtractTransferRequest",
            new Dictionary<string, object?> { ["text"] = text, ["blobUri"] = blobUri, ["base64"] = base64 },
            cancellationToken);

    [Description("Generic OCR for an inline base64 PDF/image. Returns JSON with raw text, page count, language.")]
    public Task<string> OcrDocument(
        [Description("Base64-encoded PDF or image bytes.")] string base64,
        CancellationToken cancellationToken = default)
        => ExecuteToolAsync(
            "OcrDocument",
            new Dictionary<string, object?> { ["base64"] = base64 },
            cancellationToken);

    [Description("Resolve a bank name (EN/HE alias) to its canonical id. Empty/unknown → 'Discount'.")]
    public Task<string> ResolveBank(
        [Description("Bank name, alias, or empty for default.")] string? query = null,
        CancellationToken cancellationToken = default)
        => ExecuteToolAsync(
            "ResolveBank",
            new Dictionary<string, object?> { ["query"] = query },
            cancellationToken);

    [Description("Validate an Israeli account number ('bb-bbb-aaaaaaa') for the given bank.")]
    public Task<string> ValidateAccount(
        [Description("Account in 'bb-bbb-aaaaaaa' format.")] string account,
        [Description("Canonical bank id (use ResolveBank).")] string bank,
        CancellationToken cancellationToken = default)
        => ExecuteToolAsync(
            "ValidateAccount",
            new Dictionary<string, object?> { ["account"] = account, ["bank"] = bank },
            cancellationToken);

    private async Task<string> ExecuteToolAsync(
        string toolName,
        Dictionary<string, object?>? arguments,
        CancellationToken cancellationToken)
    {
        var requestBody = new
        {
            toolName,
            arguments = arguments ?? new Dictionary<string, object?>()
        };

        using var response = await _httpClient
            .PostAsJsonAsync("/mcp/execute", requestBody, _json, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log?.LogWarning("Remote MCP execute failed for {ToolName}. Status={StatusCode} Body={Body}", toolName, (int)response.StatusCode, body);
            throw new InvalidOperationException($"Remote MCP execute failed for '{toolName}' with status {(int)response.StatusCode}.");
        }

        if (string.IsNullOrWhiteSpace(body))
            return string.Empty;

        try
        {
            var payload = JsonSerializer.Deserialize<ToolExecuteResponse>(body, _json);

            if (!string.IsNullOrWhiteSpace(payload?.Error))
                throw new InvalidOperationException(payload.Error);

            if (payload?.Result is not null)
                return payload.Result;

            // Keep behavior resilient if server returns a non-standard payload.
            return body;
        }
        catch (JsonException)
        {
            // Allow plain text responses as a fallback contract.
            return body;
        }
    }

    public IList<AITool> Tools => _tools ?? Array.Empty<AITool>();

    public async ValueTask DisposeAsync()
    {
        _httpClient?.Dispose();
        await Task.CompletedTask;
    }

    private sealed class ToolListResponse
    {
        public List<ToolMetadata>? Tools { get; init; }
    }

    private sealed class ToolMetadata
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
    }

    private sealed class ToolExecuteResponse
    {
        public string? Result { get; init; }
        public string? Error { get; init; }
    }
}
