# MCP Server Architecture — Dual Mode Configuration

## Overview

The AgentHandoff project now supports two MCP (Model Context Protocol) server modes:

1. **Embedded Mode** (default): MCP server runs as a subprocess within the API process
2. **Remote Mode**: MCP server runs as a separate web app and communicates via HTTP

## Architecture

### Mode 1: Embedded (Subprocess)
```
┌─────────────────────────────────────────┐
│  AgentHandoff.Api (Azure Web App)       │
│  ┌───────────────────────────────────┐  │
│  │ KnowledgeBaseMcpClient            │  │
│  │ └─ Spawns subprocess via stdio    │  │
│  │    └─ AgentHandoff.McpServer.dll  │  │
│  │       └─ Azure AI Search          │  │
│  └───────────────────────────────────┘  │
└─────────────────────────────────────────┘
```

**Pros:**
- Single deployment artifact
- No network latency
- Process stays isolated within the app

**Cons:**
- More resource-intensive
- Potential process lifecycle management issues
- Harder to debug/scale

### Mode 2: Remote (HTTP)
```
┌─────────────────────────────────────────┐
│  AgentHandoff.Api (Azure Web App)       │
│  ┌───────────────────────────────────┐  │
│  │ RemoteMcpClient                   │  │
│  │ └─ HTTP → https://mcp-server...   │  │
│  └───────────────────────────────────┘  │
└─────────────────────────────────────────┘
              ↓ HTTP
┌─────────────────────────────────────────┐
│  AgentHandoff.McpServerWeb (Web App)    │
│  ┌───────────────────────────────────┐  │
│  │ MCP Server (HTTP transport)       │  │
│  │ └─ Azure AI Search                │  │
│  └───────────────────────────────────┘  │
└─────────────────────────────────────────┘
```

**Pros:**
- Easier to scale independently
- Better isolation
- Can upgrade MCP server separately
- Easier debugging and monitoring

**Cons:**
- Two deployment artifacts
- Network latency between API and MCP server
- Requires additional Azure resource

## Configuration

### 1. Using Embedded Mode (Default)

#### In `appsettings.json` (API):
```json
{
  "Mcp": {
    "Mode": "Embedded",
    "ServerPath": ""
  }
}
```

Or set via environment variable:
```bash
Mcp__Mode=Embedded
Mcp__ServerPath=/home/site/wwwroot/mcpserver/AgentHandoff.McpServer.dll
```

#### Build & Deploy:
```bash
# Publish API with embedded MCP server
dotnet publish src/AgentHandoff.Api/AgentHandoff.Api.csproj -c Release -o ./publish

# The publish target in AgentHandoff.Api.csproj automatically includes McpServer:
# <Target Name="PublishMcpServer" AfterTargets="Publish">
#   <Exec Command="dotnet publish ... AgentHandoff.McpServer" />
# </Target>
```

### 2. Using Remote Mode

#### Step 1: Deploy MCP Server Web App

```bash
# Publish the web app
dotnet publish src/AgentHandoff.McpServerWeb/AgentHandoff.McpServerWeb.csproj -c Release -o ./mcp-publish

# Zip and deploy to Azure (separate web app)
# az webapp deploy --resource-group <rg> --name <mcp-app-name> --src-path ./mcp-publish.zip --type zip
```

#### Step 2: Configure API to Use Remote MCP

In `appsettings.json` (API):
```json
{
  "Mcp": {
    "Mode": "Remote",
    "ServerPath": "https://mcp-server-webapp.azurewebsites.net"
  }
}
```

Or set via environment variable:
```bash
Mcp__Mode=Remote
Mcp__ServerPath=https://mcp-server-webapp.azurewebsites.net
```

#### Step 3: Deploy API (without embedded MCP server)

```bash
dotnet publish src/AgentHandoff.Api/AgentHandoff.Api.csproj -c Release -o ./api-publish

# Note: The embedded MCP server DLL will still be published as a build artifact,
# but it won't be used by the API since Mode=Remote
```

## Project Structure

### New/Modified Projects

| Project | Type | Purpose | Changes |
|---------|------|---------|---------|
| **AgentHandoff.Engine** | Library | Core orchestration logic | Added `McpOptions` config; updated `CustomerSupportEngine` to support both modes |
| **AgentHandoff.Engine/Mcp** | Library | MCP client layer | Added `RemoteMcpClient` for HTTP-based MCP communication |
| **AgentHandoff.Api** | Web API | REST endpoints | Updated to wire both embedded and remote MCP modes |
| **AgentHandoff.Console** | Console | CLI demo | Updated to support both MCP modes |
| **AgentHandoff.McpServerWeb** | Web App | **NEW** — HTTP-hosted MCP server | Wraps MCP server logic in ASP.NET Core web host |

### Unchanged

- **AgentHandoff.McpServer** — No changes; used as-is by both embedded and remote modes
- **AgentHandoff.Web** — No changes

## MCP Clients

### KnowledgeBaseMcpClient (Embedded)
- **File**: `src/AgentHandoff.Engine/Mcp/KnowledgeBaseMcpClient.cs`
- **Transport**: `StdioClientTransport` (subprocess stdio)
- **Usage**: Embedded mode only
- **Lifecycle**: Spawns process, communicates via stdin/stdout

### RemoteMcpClient (Remote)
- **File**: `src/AgentHandoff.Engine/Mcp/RemoteMcpClient.cs`
- **Transport**: `HttpClientTransport` (HTTP)
- **Usage**: Remote mode only
- **Lifecycle**: Connects to running HTTP server

## Configuration Class

```csharp
public class McpOptions
{
    public const string SectionName = "Mcp";
    
    /// Mode: "Embedded" or "Remote" (default: "Embedded")
    public string Mode { get; set; } = "Embedded";
    
    /// For Embedded: path to DLL; For Remote: HTTP URL
    public string? ServerPath { get; set; }
    
    /// Backward-compatible alias for ServerPath
    public string? ServerDllPath { get; set; }
    
    public bool IsRemoteMode => Mode?.Equals("Remote", ...) ?? false;
    public bool IsEmbeddedMode => !IsRemoteMode;
}
```

**Location**: `src/AgentHandoff.Engine/Configuration/McpOptions.cs`

## CustomerSupportEngine Changes

The engine now supports both modes via two `StartAsync` overloads:

```csharp
// New: accepts McpOptions
public async Task<AgentBundle> StartAsync(McpOptions? mcpOptions, CancellationToken ct = default)

// Legacy: backward-compatible (uses Embedded mode)
public async Task<AgentBundle> StartAsync(string? mcpServerDllPath, CancellationToken ct = default)
```

**Internal Logic:**
- If `McpOptions.IsRemoteMode`: Creates `RemoteMcpClient` and connects via HTTP
- If `McpOptions.IsEmbeddedMode`: Creates `KnowledgeBaseMcpClient` and spawns subprocess
- Falls back to degraded bundle (no MCP tools) if initialization fails
- Retries MCP init on subsequent requests

## Deployment Scenarios

### Scenario 1: Single Web App (Embedded Mode)
```bash
# Build once, deploy to single app service
dotnet publish src/AgentHandoff.Api -c Release -o ./publish
az webapp deploy --resource-group rg --name api-app --src-path publish.zip --type zip
```

**Azure Resource Count**: 1 (API web app)

### Scenario 2: Two Web Apps (Remote Mode)
```bash
# 1. Deploy MCP server to separate app
dotnet publish src/AgentHandoff.McpServerWeb -c Release -o ./mcp-publish
az webapp deploy --resource-group rg --name mcp-app --src-path mcp-publish.zip --type zip

# 2. Deploy API pointing to remote MCP
dotnet publish src/AgentHandoff.Api -c Release -o ./api-publish
az webapp deploy --resource-group rg --name api-app --src-path api-publish.zip --type zip

# 3. Configure API app settings
az webapp config appsettings set --resource-group rg --name api-app \
  --settings Mcp__Mode=Remote Mcp__ServerPath=https://mcp-app.azurewebsites.net
```

**Azure Resource Count**: 2 (API + MCP web apps)

## Health Check & Debug Endpoint

### GET /api/debug/mcp

Returns diagnostics about the current MCP configuration:

```json
{
  "baseDir": "/home/site/wwwroot",
  "mcpMode": "Remote",
  "resolvedServerPath": "https://mcp-server.azurewebsites.net",
  "resolvedPathExists": true,
  "lastMcpServerPathTried": "https://mcp-server.azurewebsites.net",
  "lastMcpToolCount": 1,
  "lastMcpInitError": null,
  "discovered": [...]
}
```

### GET /health

Returns 200 OK if the app is running. Both MCP modes support this endpoint.

## Migration Path

To migrate from Embedded to Remote mode:

1. **Deploy MCP server web app**:
   ```bash
   dotnet publish src/AgentHandoff.McpServerWeb -c Release -o ./mcp-pub
   az webapp create --resource-group <rg> --plan <plan> --name <mcp-app-name> --runtime "dotnet|8.0"
   az webapp deploy --resource-group <rg> --name <mcp-app-name> --src-path mcp-pub.zip --type zip
   ```

2. **Update API app settings**:
   ```bash
   az webapp config appsettings set --resource-group <rg> --name <api-app-name> \
     --settings Mcp__Mode=Remote Mcp__ServerPath=https://<mcp-app-name>.azurewebsites.net
   ```

3. **Restart API**:
   ```bash
   az webapp restart --resource-group <rg> --name <api-app-name>
   ```

4. **Test**:
   ```bash
   curl https://<api-app-name>.azurewebsites.net/api/debug/mcp
   ```

## Troubleshooting

### Remote mode connection fails
- Check MCP server web app is running: `az webapp show --resource-group <rg> --name <mcp-app> --query state`
- Check firewall/CORS settings on MCP server
- Verify `Mcp__ServerPath` URL is correct (must be HTTPS in production)

### Embedded mode still uses old code
- Ensure the API rebuild included updated `AgentHandoff.McpServer` (check `/api/debug/mcp`)
- Verify `lastMcpToolCount > 0` to confirm MCP initialization succeeded

### Tools not appearing in chat
- Call `GET /api/debug/mcp` to verify `lastMcpToolCount > 0`
- Check MCP server logs (either console output for embedded, or app logs for remote)
- Verify Azure AI Search is configured and accessible

## Files Modified

- ✏️ `src/AgentHandoff.Engine/CustomerSupportEngine.cs` — Added dual-mode support
- ✏️ `src/AgentHandoff.Engine/Configuration/McpOptions.cs` — New configuration class
- ✏️ `src/AgentHandoff.Engine/Mcp/RemoteMcpClient.cs` — New HTTP client
- ✏️ `src/AgentHandoff.Api/Program.cs` — Updated DI and endpoints
- ✏️ `src/AgentHandoff.Api/appsettings.json` — New config section
- ✏️ `src/AgentHandoff.Console/Program.cs` — Added dual-mode support
- ✏️ `src/AgentHandoff.Console/appsettings.json` — New config section
- ✅ **NEW** `src/AgentHandoff.McpServerWeb/Program.cs` — Web host
- ✅ **NEW** `src/AgentHandoff.McpServerWeb/AgentHandoff.McpServerWeb.csproj` — Project file
- ✅ **NEW** `src/AgentHandoff.McpServerWeb/appsettings.json` — Server config
- ✏️ `AgentHandoff.sln` — Added new project

## Next Steps

1. **Build & test locally** with embedded mode
2. **Deploy MCP server web app** to Azure
3. **Switch API to remote mode** via app settings
4. **Monitor performance** and consider scaling independently
