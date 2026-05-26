using AgentHandoff.Engine.Agents;
using AgentHandoff.Engine.Configuration;
using AgentHandoff.Engine.Mcp;
using AgentHandoff.Engine.Orchestration;
using AgentHandoff.Engine.Sessions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.Engine;

/// <summary>
/// Top-level facade that wires the MCP server, builds the agents, and produces
/// orchestrators ready to chat. Used by both the Console host and the API project.
///
/// Supports two MCP modes:
///   - Embedded: spawns MCP server as a subprocess (original mode)
///   - Remote: connects to MCP server via HTTP (new mode for separate web app)
///
/// Lifecycle:
///   1. construct                          – cheap; just stores configuration
///   2. <see cref="StartAsync"/>           – initializes MCP (embedded or remote), builds the agent bundle
///   3. <see cref="CreateOrchestrator"/>   – returns a new per-session orchestrator that
///                                           shares the bundle but has its own chat history
///   4. <see cref="DisposeAsync"/>         – closes the MCP transport
/// </summary>
public sealed class CustomerSupportEngine : IAsyncDisposable
{
    private readonly AzureOpenAIOptions _options;
    private readonly AgentMeshOptions _meshOptions;
    private readonly AgentMeshRuntime _meshRuntime;
    private readonly FoundryAuthOptions? _foundryAuth;
    private readonly AnthropicOptions? _anthropic;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Action<AgentEvent>? _onEvent;
    private readonly ISessionRegistry? _registry;
    private readonly ApprovalOptions? _approvalOptions;
    private readonly Approvals.IApprovalPublisher _approvalPublisher;
    private IAsyncDisposable? _mcp;  // Can be KnowledgeBaseMcpClient or RemoteMcpClient
    private AgentBundle? _bundle;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public string? LastMcpServerPathTried { get; private set; }
    public string? LastMcpInitError { get; private set; }
    public int? LastMcpToolCount { get; private set; }

    public CustomerSupportEngine(AzureOpenAIOptions options,
                                 AgentMeshOptions meshOptions,
                                 ILoggerFactory? loggerFactory = null,
                                 Action<AgentEvent>? onEvent = null,
                                 ISessionRegistry? registry = null,
                                 ApprovalOptions? approvalOptions = null,
                                 Approvals.IApprovalPublisher? approvalPublisher = null,
                                 FoundryAuthOptions? foundryAuth = null,
                                 AnthropicOptions? anthropic = null)
    {
        _options = options;
        _meshOptions = meshOptions ?? throw new ArgumentNullException(nameof(meshOptions));
        _meshRuntime = AgentMeshValidator.ValidateAndBuildRuntime(_meshOptions);
        _foundryAuth = foundryAuth;
        _anthropic = anthropic;
        _loggerFactory = loggerFactory;
        _onEvent = onEvent;
        _registry = registry;
        _approvalOptions = approvalOptions;
        _approvalPublisher = approvalPublisher ?? new Approvals.NullApprovalPublisher();
    }

    /// <summary>
    /// Idempotently initialises the engine with MCP options.
    /// Supports both embedded (subprocess) and remote (HTTP) MCP modes.
    ///
    /// If MCP startup fails during an early request, the engine keeps serving with a degraded
    /// bundle and retries MCP init on subsequent starts. Once MCP tools are discovered,
    /// the bundle is rebuilt to include them.
    /// </summary>
    public async Task<AgentBundle> StartAsync(McpOptions? mcpOptions = null, CancellationToken cancellationToken = default)
    {
        mcpOptions ??= new McpOptions();
        var canAttemptMcp = !string.IsNullOrWhiteSpace(mcpOptions.ServerPath);
        var hasMcpTools = LastMcpToolCount is > 0;

        // Fast path: already initialized and either MCP is healthy or MCP isn't configured.
        if (_bundle is not null && (hasMcpTools || !canAttemptMcp))
            return _bundle;

        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            canAttemptMcp = !string.IsNullOrWhiteSpace(mcpOptions.ServerPath);
            hasMcpTools = LastMcpToolCount is > 0;

            if (_bundle is not null && (hasMcpTools || !canAttemptMcp))
                return _bundle;

            IList<AITool>? mcpTools = null;
            LastMcpServerPathTried = mcpOptions.ServerPath;
            LastMcpInitError = null;

            if (canAttemptMcp)
            {
                try
                    {
                        var configuredServerPath = mcpOptions.ServerPath!;

                        if (mcpOptions.IsRemoteMode)
                        {
                            // Remote HTTP-based MCP mode
                            if (_mcp is not RemoteMcpClient)
                            {
                                if (_mcp is IAsyncDisposable disposable)
                                    await disposable.DisposeAsync().ConfigureAwait(false);
                                _mcp = new RemoteMcpClient(configuredServerPath, _loggerFactory?.CreateLogger<RemoteMcpClient>());
                            }

                            var remoteMcp = (RemoteMcpClient)_mcp;
                            mcpTools = await remoteMcp.ConnectAsync(cancellationToken).ConfigureAwait(false);
                            LastMcpToolCount = mcpTools.Count;
                        }
                        else
                        {
                            // Embedded subprocess MCP mode
                            var canRunEmbedded = File.Exists(mcpOptions.ServerPath);
                            if (!canRunEmbedded)
                            {
                                throw new FileNotFoundException($"MCP server DLL not found at '{mcpOptions.ServerPath}'.");
                            }

                            if (_mcp is not KnowledgeBaseMcpClient)
                            {
                                if (_mcp is IAsyncDisposable disposable)
                                    await disposable.DisposeAsync().ConfigureAwait(false);
                                _mcp = new KnowledgeBaseMcpClient(_loggerFactory?.CreateLogger<KnowledgeBaseMcpClient>(), _onEvent);
                            }

                            var embeddedMcp = (KnowledgeBaseMcpClient)_mcp;
                            mcpTools = await embeddedMcp.ConnectAsync(configuredServerPath, cancellationToken).ConfigureAwait(false);
                            LastMcpToolCount = mcpTools.Count;
                        }
                    }
                catch (Exception ex)
                {
                    LastMcpToolCount = 0;
                    LastMcpInitError = ex.ToString();

                    _loggerFactory?.CreateLogger<CustomerSupportEngine>()
                        .LogWarning(ex, "MCP initialization failed ({Mode} mode). Continuing without MCP tools and will retry on next start.",
                            mcpOptions.Mode);
                }
            }

            var shouldBuild = _bundle is null || (LastMcpToolCount is > 0 && canAttemptMcp);
            if (shouldBuild)
            {
                var factory = new AgentFactory(_options, _loggerFactory, _onEvent, _foundryAuth, _anthropic);
                _bundle = factory.Build(_meshOptions, _meshRuntime, mcpTools);
            }

            return _bundle!;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Legacy overload for backward compatibility. Uses embedded mode with the provided DLL path.
    /// </summary>
    public async Task<AgentBundle> StartAsync(string? mcpServerDllPath = null, CancellationToken cancellationToken = default)
    {
        var opts = new McpOptions
        {
            Mode = "Embedded",
            ServerPath = mcpServerDllPath
        };
        return await StartAsync(opts, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a fresh Handoff-mode orchestrator for a new chat session.</summary>
    public CustomerSupportOrchestrator CreateOrchestrator() => CreateOrchestrator(sessionId: null);

    /// <summary>Creates a Handoff-mode orchestrator bound to a specific session id (for registry tracking).</summary>
    public CustomerSupportOrchestrator CreateOrchestrator(string? sessionId)
    {
        if (_bundle is null)
            throw new InvalidOperationException("StartAsync must be called before CreateOrchestrator.");

        return new CustomerSupportOrchestrator(
            _bundle,
            _loggerFactory?.CreateLogger<CustomerSupportOrchestrator>(),
            deploymentName: _options.DeploymentName,
            budgetOptions:  _options.Budget,
            registry:       _registry,
            approvalOptions: _approvalOptions,
            sessionId:      sessionId,
            approvalPublisher: _approvalPublisher);
    }

    /// <summary>Creates a fresh Magentic-mode orchestrator (Manager + plan + dispatch + synthesise).</summary>
    public MagenticOrchestrator CreateMagenticOrchestrator() => CreateMagenticOrchestrator(sessionId: null);

    public MagenticOrchestrator CreateMagenticOrchestrator(string? sessionId)
    {
        if (_bundle is null)
            throw new InvalidOperationException("StartAsync must be called before CreateMagenticOrchestrator.");

        return new MagenticOrchestrator(
            _bundle,
            _loggerFactory?.CreateLogger<MagenticOrchestrator>(),
            deploymentName: _options.DeploymentName,
            budgetOptions:  _options.Budget,
            registry:       _registry,
            approvalOptions: _approvalOptions,
            sessionId:      sessionId,
            approvalPublisher: _approvalPublisher);
    }

    public async ValueTask DisposeAsync()
    {
        if (_mcp is not null)
            await _mcp.DisposeAsync().ConfigureAwait(false);
    }
}
