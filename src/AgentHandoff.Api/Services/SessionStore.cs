using System.Collections.Concurrent;
using AgentHandoff.Engine.Orchestration;

namespace AgentHandoff.Api.Services;

/// <summary>
/// Holds one orchestrator per (sessionId, mode) so conversation history is preserved
/// across SSE calls AND each orchestration mode (Handoff vs Magentic) keeps its own
/// independent history.
/// </summary>
public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, CustomerSupportOrchestrator> _handoff = new();
    private readonly ConcurrentDictionary<string, MagenticOrchestrator>        _magentic = new();

    public CustomerSupportOrchestrator GetOrAddHandoff(string sessionId, Func<CustomerSupportOrchestrator> factory)
        => _handoff.GetOrAdd(sessionId, _ => factory());

    public CustomerSupportOrchestrator GetOrAddHandoff(string sessionId, Func<string, CustomerSupportOrchestrator> factory)
        => _handoff.GetOrAdd(sessionId, factory);

    public IEnumerable<CustomerSupportOrchestrator> AllHandoff() => _handoff.Values;

    public MagenticOrchestrator GetOrAddMagentic(string sessionId, Func<MagenticOrchestrator> factory)
        => _magentic.GetOrAdd(sessionId, _ => factory());

    public MagenticOrchestrator GetOrAddMagentic(string sessionId, Func<string, MagenticOrchestrator> factory)
        => _magentic.GetOrAdd(sessionId, factory);

    public bool TryGetMagentic(string sessionId, out MagenticOrchestrator orchestrator)
    {
        if (_magentic.TryGetValue(sessionId, out var s)) { orchestrator = s; return true; }
        orchestrator = null!; return false;
    }

    public IEnumerable<MagenticOrchestrator> AllMagentic() => _magentic.Values;

    public bool TryGetHandoff(string sessionId, out CustomerSupportOrchestrator orchestrator)
    {
        if (_handoff.TryGetValue(sessionId, out var s)) { orchestrator = s; return true; }
        orchestrator = null!; return false;
    }

    public void Reset(string sessionId)
    {
        if (_handoff.TryGetValue(sessionId, out var h))  h.Reset();
        if (_magentic.TryGetValue(sessionId, out var m)) m.Reset();
    }
}
