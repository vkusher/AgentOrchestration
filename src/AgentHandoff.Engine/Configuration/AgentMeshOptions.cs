namespace AgentHandoff.Engine.Configuration;

public sealed class AgentMeshOptions
{
    public const string SectionName = "AgentMesh";

    public string EntryAgentId { get; set; } = "triage";
    public string ManagerAgentId { get; set; } = "manager";
    public string FallbackSpecialistId { get; set; } = "banking_info";
    public string EscalationAgentId { get; set; } = "human_queue";

    public List<AgentDefinitionOptions> Agents { get; set; } = new();
    public HandoffOptions Handoff { get; set; } = new();
    public MagenticOptions Magentic { get; set; } = new();
}

public sealed class AgentDefinitionOptions
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = "unknown";
    public string Description { get; set; } = string.Empty;
    public string Instructions { get; set; } = string.Empty;
    public List<string> ToolKeys { get; set; } = new();
    public AgentRuntimeOptions Runtime { get; set; } = new();
}

public sealed class AgentRuntimeOptions
{
    // Supported values: in_process, in_process_a2a, foundry, anthropic
    public string Transport { get; set; } = "in_process";
    public string? A2ARemoteName { get; set; }

    // Optional logical id used for approval-related events.
    public string? EmitApprovalEventsAs { get; set; }

    // Foundry transport: project endpoint (e.g. https://<aiservice>.services.ai.azure.com/api/projects/<project>)
    // and the id of an existing persistent agent (asst_...). Required when Transport == "foundry".
    public string? FoundryProjectEndpoint { get; set; }
    public string? FoundryAgentId { get; set; }

    // Anthropic transport: per-agent model override (e.g. "claude-haiku-4-5"). When null
    // the global default from AnthropicOptions.Model is used. API key is process-wide
    // (see AnthropicOptions).
    public string? AnthropicModel { get; set; }
}

public sealed class HandoffOptions
{
    public string? EntryAgentId { get; set; }
    public List<HandoffLinkOptions> Links { get; set; } = new();
}

public sealed class HandoffLinkOptions
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public bool Bidirectional { get; set; }
}

public sealed class MagenticOptions
{
    public string? ManagerAgentId { get; set; }
    public List<string> SpecialistAgentIds { get; set; } = new();
    public string? FallbackSpecialistId { get; set; }
}

public sealed record HandoffEdge(string FromAgentId, string ToAgentId);

public sealed record AgentMeshRuntime(
    string EntryAgentId,
    string ManagerAgentId,
    string FallbackSpecialistId,
    string EscalationAgentId,
    string? ApprovalAgentId,
    IReadOnlyList<HandoffEdge> HandoffEdges,
    IReadOnlyList<string> MagenticSpecialistAgentIds);