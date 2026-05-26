namespace AgentHandoff.Engine.Configuration;

public static class AgentMeshValidator
{
    public static AgentMeshRuntime ValidateAndBuildRuntime(AgentMeshOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        var errors = new List<string>();

        if (options.Agents.Count == 0)
            errors.Add("AgentMesh:Agents must contain at least one agent definition.");

        var byId = new Dictionary<string, AgentDefinitionOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in options.Agents)
        {
            agent.Id = (agent.Id ?? string.Empty).Trim();
            agent.Name = string.IsNullOrWhiteSpace(agent.Name) ? agent.Id : agent.Name.Trim();
            agent.DisplayName = string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.Name : agent.DisplayName.Trim();
            agent.Role = string.IsNullOrWhiteSpace(agent.Role) ? "unknown" : agent.Role.Trim();
            agent.Description ??= string.Empty;
            agent.Instructions ??= string.Empty;

            if (string.IsNullOrWhiteSpace(agent.Id))
            {
                errors.Add("AgentMesh:Agents contains an entry with empty id.");
                continue;
            }

            if (!byId.TryAdd(agent.Id, agent))
                errors.Add($"AgentMesh:Agents contains duplicate id '{agent.Id}'.");
        }

        var entryAgentId = FirstNonEmpty(options.Handoff.EntryAgentId, options.EntryAgentId);
        if (string.IsNullOrWhiteSpace(entryAgentId))
            errors.Add("AgentMesh:EntryAgentId (or AgentMesh:Handoff:EntryAgentId) must be configured.");
        else if (!byId.ContainsKey(entryAgentId))
            errors.Add($"AgentMesh:EntryAgentId '{entryAgentId}' does not exist in AgentMesh:Agents.");

        var managerAgentId = FirstNonEmpty(options.Magentic.ManagerAgentId, options.ManagerAgentId);
        if (string.IsNullOrWhiteSpace(managerAgentId))
            errors.Add("AgentMesh:ManagerAgentId (or AgentMesh:Magentic:ManagerAgentId) must be configured.");
        else if (!byId.ContainsKey(managerAgentId))
            errors.Add($"AgentMesh:ManagerAgentId '{managerAgentId}' does not exist in AgentMesh:Agents.");

        var escalationAgentId = string.IsNullOrWhiteSpace(options.EscalationAgentId)
            ? "human_queue"
            : options.EscalationAgentId.Trim();

        var specialistIds = options.Magentic.SpecialistAgentIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (specialistIds.Count == 0)
        {
            specialistIds = options.Agents
                .Where(a => string.Equals(a.Role, "specialist", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Id)
                .ToList();
        }

        if (specialistIds.Count == 0)
            errors.Add("AgentMesh:Magentic:SpecialistAgentIds is empty and no agents with role 'specialist' were found.");

        foreach (var id in specialistIds)
        {
            if (!byId.ContainsKey(id))
                errors.Add($"AgentMesh:Magentic:SpecialistAgentIds references unknown agent '{id}'.");
        }

        var fallbackSpecialistId = FirstNonEmpty(options.Magentic.FallbackSpecialistId, options.FallbackSpecialistId)
            ?? specialistIds.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(fallbackSpecialistId))
            errors.Add("AgentMesh:FallbackSpecialistId could not be resolved.");
        else if (!byId.ContainsKey(fallbackSpecialistId))
            errors.Add($"AgentMesh:FallbackSpecialistId '{fallbackSpecialistId}' does not exist in AgentMesh:Agents.");

        if (options.Handoff.Links.Count == 0)
            errors.Add("AgentMesh:Handoff:Links must contain at least one handoff link.");

        var directional = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in options.Handoff.Links)
        {
            var from = (link.From ?? string.Empty).Trim();
            var to = (link.To ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            {
                errors.Add("AgentMesh:Handoff:Links contains an entry with empty from/to.");
                continue;
            }

            if (!byId.ContainsKey(from))
                errors.Add($"AgentMesh:Handoff:Links references unknown source agent '{from}'.");
            if (!byId.ContainsKey(to))
                errors.Add($"AgentMesh:Handoff:Links references unknown target agent '{to}'.");
            if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                errors.Add($"AgentMesh:Handoff:Links contains a self-loop for agent '{from}'.");

            directional.Add($"{from}\u001F{to}");
            if (link.Bidirectional)
                directional.Add($"{to}\u001F{from}");
        }

        string? approvalAgentId = null;
        foreach (var agent in options.Agents)
        {
            if (!string.IsNullOrWhiteSpace(agent.Runtime.EmitApprovalEventsAs))
            {
                approvalAgentId = agent.Runtime.EmitApprovalEventsAs!.Trim();
                break;
            }

            if (agent.ToolKeys.Any(k =>
                    string.Equals(k, "local.issue_refund",   StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(k, "local.submit_transfer", StringComparison.OrdinalIgnoreCase)))
            {
                approvalAgentId = agent.Id;
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(approvalAgentId) && !byId.ContainsKey(approvalAgentId))
            errors.Add($"AgentMesh approval event agent '{approvalAgentId}' does not exist in AgentMesh:Agents.");

        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid AgentMesh configuration:\n - " + string.Join("\n - ", errors));

        var edges = directional
            .Select(e => e.Split('\u001F'))
            .Select(parts => new HandoffEdge(parts[0], parts[1]))
            .ToList();

        return new AgentMeshRuntime(
            EntryAgentId: entryAgentId!,
            ManagerAgentId: managerAgentId!,
            FallbackSpecialistId: fallbackSpecialistId!,
            EscalationAgentId: escalationAgentId,
            ApprovalAgentId: approvalAgentId,
            HandoffEdges: edges,
            MagenticSpecialistAgentIds: specialistIds);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return null;
    }
}
