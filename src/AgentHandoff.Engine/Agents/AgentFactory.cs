using AgentHandoff.Engine.A2A;
using AgentHandoff.Engine.Configuration;
using AgentHandoff.Engine.Guardrails;
using AgentHandoff.Engine.Metrics;
using AgentHandoff.Engine.Orchestration;
using AgentHandoff.Engine.Tools;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.Engine.Agents;

/// <summary>
/// Constructs agents from <see cref="AgentMeshOptions"/>, wiring guardrails, local tools,
/// MCP tools, and optional in-process A2A wrappers.
/// </summary>
public sealed class AgentFactory
{
    private readonly AzureOpenAIOptions _options;
    private readonly FoundryAuthOptions? _foundryAuth;
    private readonly AnthropicOptions? _anthropic;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly Action<AgentEvent>? _onEvent;

    public AgentFactory(AzureOpenAIOptions options,
                        ILoggerFactory? loggerFactory = null,
                        Action<AgentEvent>? onEvent = null,
                        FoundryAuthOptions? foundryAuth = null,
                        AnthropicOptions? anthropic = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _foundryAuth = foundryAuth;
        _anthropic = anthropic;
        _loggerFactory = loggerFactory;
        _onEvent = onEvent;
    }

    public IChatClient CreateChatClient()
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            throw new InvalidOperationException(
                "AzureOpenAI:Endpoint is not configured. Set it via user-secrets or appsettings.");
        }

        var azureClient = string.IsNullOrWhiteSpace(_options.ApiKey)
            ? new AzureOpenAIClient(new Uri(_options.Endpoint), new DefaultAzureCredential())
            : new AzureOpenAIClient(new Uri(_options.Endpoint), new AzureKeyCredential(_options.ApiKey));

        // Tell the metrics middleware which model rate-card to use for cost computation.
        MetricsChatClient.DeploymentName = _options.DeploymentName;

        // Wrap the raw chat client with metrics middleware so every model call
        // (including handoffs and tool-call follow-ups) feeds the active TurnMetrics
        // AND the active SessionBudget.
        return azureClient
            .GetChatClient(_options.DeploymentName)
            .AsIChatClient()
            .AsBuilder()
                .Use(getResponseFunc:          MetricsChatClient.GetResponse,
                     getStreamingResponseFunc: MetricsChatClient.GetStreamingResponse)
            .Build();
    }

    public AgentBundle Build(AgentMeshOptions meshOptions, AgentMeshRuntime meshRuntime, IList<AITool>? mcpKnowledgeBaseTools = null)
    {
        ArgumentNullException.ThrowIfNull(meshOptions);
        ArgumentNullException.ThrowIfNull(meshRuntime);

        var chatClient = CreateChatClient();
        // Apply guardrails to every agent via run-middleware.
        var guardLog = _loggerFactory?.CreateLogger<GuardrailMiddleware>();
        var safety   = ContentSafetyAnalyzer.TryCreate(
            _options.ContentSafetyEndpoint,
            _options.ContentSafetyApiKey,
            _options.ContentSafetyThreshold,
            _loggerFactory);
        var guard    = new GuardrailMiddleware(guardLog, _onEvent, safety);

        AIAgent Guarded(AIAgent a) =>
            a.AsBuilder()
             .Use(runFunc: guard.Run, runStreamingFunc: guard.RunStreaming)
             .Build();

        var registry = new AgentRegistry();
        var builtForToolUse = new Dictionary<string, AIAgent>(StringComparer.OrdinalIgnoreCase);

        // Two-pass build so an agent can reference another as a tool (key "agent.<id>").
        // Pass 1: agents that do NOT reference any other agent.
        // Pass 2: agents that reference others (their target must already be in builtForToolUse).
        var pass1 = meshOptions.Agents.Where(a => !a.ToolKeys.Any(k => k.StartsWith("agent.", StringComparison.OrdinalIgnoreCase))).ToList();
        var pass2 = meshOptions.Agents.Except(pass1).ToList();

        foreach (var definition in pass1.Concat(pass2))
        {
            var tools = ResolveTools(definition, mcpKnowledgeBaseTools, builtForToolUse, _onEvent);
            AIAgent agent = new ChatClientAgent(
                chatClient,
                instructions: definition.Instructions,
                name: definition.Name,
                description: definition.Description,
                tools: tools);

            if (string.Equals(definition.Runtime.Transport, "in_process_a2a", StringComparison.OrdinalIgnoreCase))
            {
                var remoteName = string.IsNullOrWhiteSpace(definition.Runtime.A2ARemoteName)
                    ? definition.Id
                    : definition.Runtime.A2ARemoteName;
                agent = InProcessA2A.Wrap(agent, remoteName, _onEvent);
            }
            else if (string.Equals(definition.Runtime.Transport, "foundry", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(definition.Runtime.FoundryProjectEndpoint))
                    throw new InvalidOperationException(
                        $"Agent '{definition.Id}' uses transport=foundry but Runtime.FoundryProjectEndpoint is not set.");
                if (string.IsNullOrWhiteSpace(definition.Runtime.FoundryAgentId))
                    throw new InvalidOperationException(
                        $"Agent '{definition.Id}' uses transport=foundry but Runtime.FoundryAgentId is not set.");

                agent = FoundryAgentAdapter.Wrap(
                    agent,
                    definition.Runtime.FoundryProjectEndpoint!,
                    definition.Runtime.FoundryAgentId!,
                    definition.Name,
                    _foundryAuth,
                    _onEvent);
            }
            else if (string.Equals(definition.Runtime.Transport, "anthropic", StringComparison.OrdinalIgnoreCase))
            {
                if (_anthropic is null || !_anthropic.HasApiKey)
                    throw new InvalidOperationException(
                        $"Agent '{definition.Id}' uses transport=anthropic but Anthropic:ApiKey is not configured.");

                var model = string.IsNullOrWhiteSpace(definition.Runtime.AnthropicModel)
                    ? _anthropic.Model
                    : definition.Runtime.AnthropicModel!;

                agent = AnthropicAgentAdapter.Wrap(
                    agent,
                    _anthropic.ApiKey!,
                    model,
                    definition.Name,
                    definition.Instructions,
                    _onEvent);
            }

            registry.Register(new AgentDescriptor(
                definition.Id,
                definition.Name,
                definition.DisplayName,
                definition.Role,
                definition.Description,
                Guarded(agent)));

            // Capture the pre-guard agent (with its raw run path) for agent-as-tool consumers.
            // We deliberately use the un-guarded reference here so the host agent's own guardrails
            // don't double-fire on each per-document invocation.
            builtForToolUse[definition.Id] = agent;
        }

        return new AgentBundle(registry, meshRuntime);
    }

    private static List<AITool> ResolveTools(
        AgentDefinitionOptions definition,
        IList<AITool>? mcpKnowledgeBaseTools,
        IReadOnlyDictionary<string, AIAgent> builtAgents,
        Action<AgentEvent>? onEvent)
    {
        var tools = new List<AITool>();
        if (definition.ToolKeys.Count == 0)
            return tools;

        var accountTools = new AccountTools();
        var eventAgentId = string.IsNullOrWhiteSpace(definition.Runtime.EmitApprovalEventsAs)
            ? definition.Id
            : definition.Runtime.EmitApprovalEventsAs!;
        var billingTools = new BillingTools(eventAgentId);
        var transferTools = new MoneyTransferTools(eventAgentId);

        foreach (var key in definition.ToolKeys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            switch (key)
            {
                case "mcp.search_knowledge_base":
                    if (mcpKnowledgeBaseTools is not null)
                        tools.AddRange(mcpKnowledgeBaseTools);
                    break;

                case "local.get_transaction_status":
                    tools.Add(AIFunctionFactory.Create(accountTools.GetTransactionStatus));
                    break;

                case "local.request_card_replacement":
                    tools.Add(AIFunctionFactory.Create(accountTools.RequestCardReplacement));
                    break;

                case "local.lookup_balance":
                    tools.Add(AIFunctionFactory.Create(billingTools.LookupBalance));
                    break;

                case "local.issue_refund":
                    tools.Add(AIFunctionFactory.Create(billingTools.IssueRefund));
                    break;

                case "local.submit_transfer":
                    tools.Add(AIFunctionFactory.Create(transferTools.SubmitTransfer));
                    break;

                // The MoneyTransfer MCP toolkeys all surface the full MCP tool list to the
                // agent. The model picks the right tool by description (same pattern used by
                // mcp.search_knowledge_base today). Filtering by tool name can be added later.
                case "mcp.extract_transfer_request":
                case "mcp.resolve_bank":
                case "mcp.validate_account":
                case "mcp.ocr_document":
                case "mcp.ingest_mortgage_bundle":
                case "mcp.compute_required_documents":
                case "mcp.classify_document":
                case "mcp.authenticate_document":
                case "mcp.emit_validation_report":
                    if (mcpKnowledgeBaseTools is not null)
                        tools.AddRange(mcpKnowledgeBaseTools);
                    break;

                case "agent.doc_classifier":
                    if (!builtAgents.TryGetValue("doc_classifier", out var classifier))
                        throw new InvalidOperationException(
                            $"Agent '{definition.Id}' references agent.doc_classifier but the 'doc_classifier' agent has not been built. " +
                            "Ensure it is defined in Agents and has no agent.* tool keys of its own.");
                    tools.Add(AgentAsTool.CreateDocClassifierTool(classifier, onEvent));
                    break;

                default:
                    throw new InvalidOperationException($"Unknown tool key '{key}' in agent '{definition.Id}'.");
            }
        }

        return tools;
    }
}

public sealed record AgentBundle(AgentRegistry Registry, AgentMeshRuntime Runtime);
