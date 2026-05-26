using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentHandoff.Engine.Orchestration;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentHandoff.Engine.Agents;

/// <summary>
/// Wraps a placeholder <see cref="AIAgent"/> with middleware that forwards each turn to
/// Anthropic's Messages API (<c>https://api.anthropic.com/v1/messages</c>). The local
/// agent definition supplies the system prompt; tools are not forwarded — fraud-detection
/// style agents are expected to produce text-only output.
///
/// We implement this against the public REST API directly to avoid pulling the
/// <c>Microsoft.Agents.AI.Anthropic</c> package, which requires a newer Agents.AI preview
/// than the rest of the codebase pins to.
/// </summary>
internal static class AnthropicAgentAdapter
{
    private static readonly HttpClient SharedClient = new()
    {
        Timeout = TimeSpan.FromSeconds(120),
    };

    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string ApiVersion = "2023-06-01";

    public static AIAgent Wrap(
        AIAgent placeholder,
        string apiKey,
        string model,
        string logicalAgentName,
        string? systemPrompt,
        Action<AgentEvent>? onEvent = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("Anthropic API key is required.", nameof(apiKey));
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Anthropic model is required.", nameof(model));

        return placeholder
            .AsBuilder()
            .Use(
                runFunc: async (messages, thread, options, _, ct) =>
                {
                    EmitEvent(onEvent, logicalAgentName, "request");
                    var text = await InvokeAsync(apiKey, model, systemPrompt, messages, ct).ConfigureAwait(false);
                    EmitEvent(onEvent, logicalAgentName, "response");
                    return new AgentRunResponse(new ChatMessage(ChatRole.Assistant, text));
                },
                runStreamingFunc: (messages, thread, options, _, ct) =>
                    StreamAsync(apiKey, model, logicalAgentName, systemPrompt, messages, onEvent, ct))
            .Build();
    }

    private static async IAsyncEnumerable<AgentRunResponseUpdate> StreamAsync(
        string apiKey,
        string model,
        string logicalAgentName,
        string? systemPrompt,
        IEnumerable<ChatMessage> messages,
        Action<AgentEvent>? onEvent,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        EmitEvent(onEvent, logicalAgentName, "stream-start");
        var text = await InvokeAsync(apiKey, model, systemPrompt, messages, ct).ConfigureAwait(false);
        yield return new AgentRunResponseUpdate
        {
            Role = ChatRole.Assistant,
            Contents = new List<AIContent> { new TextContent(text) },
        };
        EmitEvent(onEvent, logicalAgentName, "stream-end");
    }

    private static async Task<string> InvokeAsync(
        string apiKey,
        string model,
        string? systemPrompt,
        IEnumerable<ChatMessage> messages,
        CancellationToken ct)
    {
        var anthropicMessages = new List<AnthropicMessage>();
        foreach (var message in messages)
        {
            var text = message.Text;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            // Anthropic only accepts user / assistant in the messages array; system goes in
            // a top-level field. Coerce anything that isn't assistant to user.
            var role = message.Role == ChatRole.Assistant ? "assistant" : "user";
            anthropicMessages.Add(new AnthropicMessage(role, text));
        }

        // Anthropic requires at least one user message in the conversation.
        if (anthropicMessages.Count == 0 || anthropicMessages.All(m => m.Role != "user"))
        {
            anthropicMessages.Add(new AnthropicMessage("user", "."));
        }

        var request = new AnthropicRequest(
            model,
            MaxTokens: 1024,
            System: string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
            Messages: anthropicMessages);

        using var http = new HttpRequestMessage(HttpMethod.Post, ApiUrl)
        {
            Content = JsonContent.Create(request, options: SerializerOptions),
        };
        http.Headers.Add("x-api-key", apiKey);
        http.Headers.Add("anthropic-version", ApiVersion);

        using var response = await SharedClient.SendAsync(http, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var raw = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Anthropic call failed with HTTP {(int)response.StatusCode}: {raw}");
        }

        var payload = JsonSerializer.Deserialize<AnthropicResponse>(raw, SerializerOptions)
                      ?? throw new InvalidOperationException("Anthropic returned an empty body.");

        var combined = string.Concat(
            payload.Content
                .Where(c => string.Equals(c.Type, "text", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Text));
        return combined;
    }

    private static void EmitEvent(Action<AgentEvent>? onEvent, string remoteName, string stage)
    {
        var evt = new ToolCallEvent(remoteName, "anthropic.invoke", stage, "Anthropic", DateTimeOffset.UtcNow);
        onEvent?.Invoke(evt);
        TurnEventBus.Publish(evt);
    }

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record AnthropicRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string? System,
        [property: JsonPropertyName("messages")] List<AnthropicMessage> Messages);

    private sealed record AnthropicMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record AnthropicResponse(
        [property: JsonPropertyName("content")] List<AnthropicResponseContent> Content);

    private sealed record AnthropicResponseContent(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string Text);
}
