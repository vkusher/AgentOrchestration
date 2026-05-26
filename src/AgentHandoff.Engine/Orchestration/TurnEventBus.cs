namespace AgentHandoff.Engine.Orchestration;

/// <summary>
/// Per-turn event sink used by middleware (guardrails, A2A wrapper, tool functions) to
/// publish into the orchestrator's outbound stream.
///
/// Implementation note: we want <see cref="AsyncLocal{T}"/> semantics so concurrent sessions
/// don't clobber each other, BUT the Microsoft.Agents.AI workflow (specifically
/// HandoffAgentExecutor) appears to reset/suppress ExecutionContext between handoff
/// transitions in this preview, so AsyncLocal values don't reach the second/third agent's
/// invocation. To work around that we also keep a process-wide static fallback that the
/// orchestrator updates on each turn — the AsyncLocal is preferred, the static is a last-resort
/// catch. With a single active session (the demo's typical case) this is safe; for genuine
/// multi-tenant production you'd replace this with explicit handler injection per agent bundle.
/// </summary>
public static class TurnEventBus
{
    private static readonly AsyncLocal<Action<AgentEvent>?> _sink = new();
    private static Action<AgentEvent>? _staticFallback;

    public static Action<AgentEvent>? Current
    {
        get => _sink.Value ?? _staticFallback;
        set
        {
            _sink.Value = value;
            _staticFallback = value;
        }
    }

    public static void Publish(AgentEvent evt) => Current?.Invoke(evt);
}
