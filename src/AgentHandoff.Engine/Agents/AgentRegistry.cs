using Microsoft.Agents.AI;

namespace AgentHandoff.Engine.Agents;

/// <summary>
/// Holds the constructed agents so callers can inspect their metadata (id, name, description, role).
/// </summary>
public sealed record AgentDescriptor(
    string Id,
    string Name,
    string DisplayName,
    string Role,
    string Description,
    AIAgent Agent);

public sealed class AgentRegistry
{
    private readonly Dictionary<string, AgentDescriptor> _byId = new(StringComparer.OrdinalIgnoreCase);

    public AgentRegistry Register(AgentDescriptor d)
    {
        _byId[d.Id] = d;
        // also index by Name to make AgentResponseUpdateEvent lookups easier
        _byId[d.Name] = d;
        return this;
    }

    public AgentDescriptor? FindById(string id) => _byId.TryGetValue(id, out var d) ? d : null;

    public IEnumerable<AgentDescriptor> All =>
        _byId.Values.GroupBy(d => d.Id).Select(g => g.First());
}
