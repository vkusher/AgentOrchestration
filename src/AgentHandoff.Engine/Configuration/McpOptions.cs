namespace AgentHandoff.Engine.Configuration;

/// <summary>
/// Configuration options for MCP server connection mode.
/// </summary>
public class McpOptions
{
    public const string SectionName = "Mcp";

    /// <summary>
    /// Mode of MCP server: "Embedded" (subprocess) or "Remote" (HTTP).
    /// Default: "Embedded"
    /// </summary>
    public string Mode { get; set; } = "Embedded";

    /// <summary>
    /// For Mode="Embedded": path to the AgentHandoff.McpServer.dll
    /// For Mode="Remote": URL to the AgentHandoff.McpServerWeb (e.g., https://mcp-server.azurewebsites.net)
    /// </summary>
    public string? ServerPath { get; set; }

    /// <summary>
    /// Legacy alias for ServerPath (for backward compatibility).
    /// If both are set, ServerPath takes precedence.
    /// </summary>
    public string? ServerDllPath
    {
        get => ServerPath;
        set => ServerPath = value;
    }

    public bool IsRemoteMode => Mode?.Equals("Remote", StringComparison.OrdinalIgnoreCase) ?? false;
    public bool IsEmbeddedMode => !IsRemoteMode;
}
