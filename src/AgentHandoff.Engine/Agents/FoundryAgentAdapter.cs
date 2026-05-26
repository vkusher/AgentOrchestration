using AgentHandoff.Engine.Configuration;
using AgentHandoff.Engine.Orchestration;
using Azure.AI.Agents.Persistent;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentHandoff.Engine.Agents;

/// <summary>
/// Wraps an existing Microsoft AI Foundry persistent (hosted) agent so it can participate in
/// the local handoff / Magentic mesh as a normal <see cref="AIAgent"/>.
///
/// Per turn we create a fresh Foundry thread, replay the incoming message history onto it, kick
/// off a run, poll until terminal, then return the assistant's reply. Threads are deleted after
/// each turn to avoid leaking server-side state — the orchestrator already passes the full
/// conversation history on every call, so per-turn threads are sufficient.
///
/// Tools, instructions and models are configured on the Foundry side. The local
/// <c>ToolKeys</c> / <c>Instructions</c> values are ignored for this transport.
/// </summary>
internal static class FoundryAgentAdapter
{
    public static AIAgent Wrap(
        AIAgent placeholder,
        string projectEndpoint,
        string foundryAgentId,
        string logicalAgentName,
        FoundryAuthOptions? auth = null,
        Action<AgentEvent>? onEvent = null)
    {
        if (string.IsNullOrWhiteSpace(projectEndpoint))
            throw new ArgumentException("Foundry project endpoint is required.", nameof(projectEndpoint));
        if (string.IsNullOrWhiteSpace(foundryAgentId))
            throw new ArgumentException("Foundry agent id is required.", nameof(foundryAgentId));

        TokenCredential credential = auth is { HasClientSecret: true }
            ? new ClientSecretCredential(auth.TenantId, auth.ClientId, auth.ClientSecret)
            : new DefaultAzureCredential();

        var client = new PersistentAgentsClient(projectEndpoint, credential);

        return placeholder
            .AsBuilder()
            .Use(
                runFunc: async (messages, thread, options, _, ct) =>
                {
                    EmitEvent(onEvent, logicalAgentName, "request");
                    var text = await InvokeAsync(client, foundryAgentId, messages, ct).ConfigureAwait(false);
                    EmitEvent(onEvent, logicalAgentName, "response");
                    return new AgentRunResponse(new ChatMessage(ChatRole.Assistant, text));
                },
                runStreamingFunc: (messages, thread, options, _, ct) =>
                    StreamAsync(client, foundryAgentId, logicalAgentName, messages, onEvent, ct))
            .Build();
    }

    private static async IAsyncEnumerable<AgentRunResponseUpdate> StreamAsync(
        PersistentAgentsClient client,
        string foundryAgentId,
        string logicalAgentName,
        IEnumerable<ChatMessage> messages,
        Action<AgentEvent>? onEvent,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        EmitEvent(onEvent, logicalAgentName, "stream-start");
        var text = await InvokeAsync(client, foundryAgentId, messages, ct).ConfigureAwait(false);
        yield return new AgentRunResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = new List<AIContent> { new TextContent(text) },
        };
        EmitEvent(onEvent, logicalAgentName, "stream-end");
    }

    private static async Task<string> InvokeAsync(
        PersistentAgentsClient client,
        string foundryAgentId,
        IEnumerable<ChatMessage> messages,
        CancellationToken ct)
    {
        PersistentAgentThread thread = await client.Threads.CreateThreadAsync(cancellationToken: ct).ConfigureAwait(false);

        try
        {
            foreach (var message in messages)
            {
                var text = message.Text;
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // Foundry threads only accept user/assistant roles for replay; coerce everything else to user.
                var role = message.Role == ChatRole.Assistant ? MessageRole.Agent : MessageRole.User;
                await client.Messages.CreateMessageAsync(thread.Id, role, text, cancellationToken: ct).ConfigureAwait(false);
            }

            ThreadRun run = await client.Runs.CreateRunAsync(thread.Id, foundryAgentId, cancellationToken: ct).ConfigureAwait(false);

            while (run.Status == RunStatus.Queued
                || run.Status == RunStatus.InProgress
                || run.Status == RunStatus.RequiresAction)
            {
                await Task.Delay(500, ct).ConfigureAwait(false);
                run = await client.Runs.GetRunAsync(thread.Id, run.Id, ct).ConfigureAwait(false);
            }

            if (run.Status != RunStatus.Completed)
            {
                var detail = run.LastError?.Message ?? run.Status.ToString();
                throw new InvalidOperationException(
                    $"Foundry agent '{foundryAgentId}' run ended with status '{run.Status}': {detail}");
            }

            await foreach (var msg in client.Messages.GetMessagesAsync(
                                          threadId: thread.Id,
                                          order: ListSortOrder.Descending,
                                          cancellationToken: ct).ConfigureAwait(false))
            {
                if (msg.Role != MessageRole.Agent)
                    continue;

                foreach (var content in msg.ContentItems)
                {
                    if (content is MessageTextContent textContent && !string.IsNullOrEmpty(textContent.Text))
                        return textContent.Text;
                }
            }

            return string.Empty;
        }
        finally
        {
            try
            {
                await client.Threads.DeleteThreadAsync(thread.Id, ct).ConfigureAwait(false);
            }
            catch
            {
                // Best-effort cleanup; never fail a turn because cleanup failed.
            }
        }
    }

    private static void EmitEvent(Action<AgentEvent>? onEvent, string remoteName, string stage)
    {
        var evt = new ToolCallEvent(remoteName, "foundry.invoke", stage, "Foundry", DateTimeOffset.UtcNow);
        onEvent?.Invoke(evt);
        TurnEventBus.Publish(evt);
    }
}
