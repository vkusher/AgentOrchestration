using AgentHandoff.Engine.Orchestration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentHandoff.Engine.A2A;

/// <summary>
/// Lightweight, in-process emulation of the Agent-to-Agent (A2A) protocol.
///
/// Conceptually identical to wrapping an <see cref="AIAgent"/> obtained from
/// <c>A2AClient.AsAIAgent(...)</c> per
///   https://learn.microsoft.com/agent-framework/agents/providers/agent-to-agent
/// but operates entirely within a single process — useful for demos, tests, and
/// for keeping low-latency hot paths co-located.
///
/// Implementation note: rather than subclassing <see cref="AIAgent"/> we use the
/// official <c>AIAgent.AsBuilder().Use(...)</c> middleware pipeline. This means
/// the wrapped agent is a perfectly normal <see cref="AIAgent"/> and the orchestrator
/// has no idea it's "remote". Swap to a real network transport by replacing the
/// inner agent with one obtained from <c>new A2AClient(uri).AsAIAgent()</c>.
///
/// Type names match Microsoft.Agents.AI 1.0.0-preview.251002.1
/// (AgentThread / AgentRunResponse / AgentRunResponseUpdate).
/// </summary>
public static class InProcessA2A
{
    /// <summary>
    /// Wraps <paramref name="inner"/> in a layer that emits A2A-style telemetry events
    /// (call/response) before and after each invocation, mimicking what would happen
    /// if calls were going over the A2A wire protocol.
    /// </summary>
    public static AIAgent Wrap(AIAgent inner, string remoteName, Action<AgentEvent>? onEvent = null)
        => inner
            .AsBuilder()
            .Use(
                runFunc: async (messages, thread, options, innerAgent, ct) =>
                {
                    EmitBoth(onEvent, remoteName, "request");
                    // Yield once so latency reflects a real protocol hop (and so this is awaitable).
                    await Task.Yield();
                    var response = await innerAgent.RunAsync(messages, thread, options, ct).ConfigureAwait(false);
                    EmitBoth(onEvent, remoteName, "response");
                    return response;
                },
                runStreamingFunc: (messages, thread, options, innerAgent, ct) =>
                    StreamWithTelemetry(messages, thread, options, innerAgent, ct, remoteName, onEvent))
            .Build();

    private static async IAsyncEnumerable<AgentRunResponseUpdate> StreamWithTelemetry(
        IEnumerable<ChatMessage> messages,
        AgentThread? thread,
        AgentRunOptions? options,
        AIAgent innerAgent,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken,
        string remoteName,
        Action<AgentEvent>? onEvent)
    {
        EmitBoth(onEvent, remoteName, "stream-start");
        await foreach (var update in innerAgent.RunStreamingAsync(messages, thread, options, cancellationToken)
                                                .ConfigureAwait(false))
        {
            yield return update;
        }
        EmitBoth(onEvent, remoteName, "stream-end");
    }

    private static void EmitBoth(Action<AgentEvent>? onEvent, string remoteName, string stage)
    {
        var evt = new ToolCallEvent(remoteName, "a2a.invoke", stage, "A2A", DateTimeOffset.UtcNow);
        onEvent?.Invoke(evt);
        TurnEventBus.Publish(evt);   // ensure the per-turn orchestrator stream sees it too
    }
}
