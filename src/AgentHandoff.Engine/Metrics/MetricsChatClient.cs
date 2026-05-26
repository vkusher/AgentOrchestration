using Microsoft.Extensions.AI;

namespace AgentHandoff.Engine.Metrics;

/// <summary>
/// Builds an <see cref="IChatClient"/> middleware delegate pair that adds every model call's
/// token usage to (a) the active <see cref="TurnMetrics"/> on <see cref="TurnMetricsBus"/>
/// AND (b) the active <see cref="SessionBudget"/> on <see cref="SessionBudgetBus"/>.
///
/// Wire it via:
///   <code>chatClient.AsBuilder().Use(MetricsChatClient.GetResponse, MetricsChatClient.GetStreamingResponse).Build()</code>
/// </summary>
public static class MetricsChatClient
{
    /// <summary>
    /// Set once at engine startup (by AgentFactory). Used by the budget recorder to
    /// translate token counts into USD via <see cref="TokenCostEstimator"/>.
    /// </summary>
    public static string? DeploymentName { get; set; }

    public static async Task<ChatResponse> GetResponse(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IChatClient inner,
        CancellationToken ct)
    {
        var response = await inner.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
        Record(response.Usage);
        return response;
    }

    public static async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponse(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        IChatClient inner,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        UsageDetails? lastUsage = null;

        await foreach (var update in inner.GetStreamingResponseAsync(messages, options, ct).ConfigureAwait(false))
        {
            if (update.Contents is { Count: > 0 })
            {
                foreach (var c in update.Contents)
                {
                    if (c is UsageContent uc && uc.Details is { } details)
                        lastUsage = details;
                }
            }
            yield return update;
        }

        Record(lastUsage);
    }

    private static void Record(UsageDetails? usage)
    {
        if (usage is null) return;
        var input  = usage.InputTokenCount  ?? 0;
        var output = usage.OutputTokenCount ?? 0;

        TurnMetricsBus.Current?.Add(input, output);

        var budget = SessionBudgetBus.Current;
        if (budget is not null)
        {
            var cost = TokenCostEstimator.EstimateUsd(DeploymentName, input, output);
            budget.Add(input, output, cost);
        }
    }
}
