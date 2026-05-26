namespace AgentHandoff.Engine.Metrics;

/// <summary>
/// Thread-safe accumulator for per-turn metrics. The chat-client middleware adds to it on
/// every model call within a turn (handoffs and tool-call follow-ups all increment the same
/// instance), and the orchestrator reads the totals at end-of-turn to emit a metrics event.
/// </summary>
public sealed class TurnMetrics
{
    private long _inputTokens;
    private long _outputTokens;
    private int  _modelCalls;

    public void Add(long inputTokens, long outputTokens)
    {
        if (inputTokens  > 0) Interlocked.Add(ref _inputTokens,  inputTokens);
        if (outputTokens > 0) Interlocked.Add(ref _outputTokens, outputTokens);
        Interlocked.Increment(ref _modelCalls);
    }

    public long InputTokens  => Interlocked.Read(ref _inputTokens);
    public long OutputTokens => Interlocked.Read(ref _outputTokens);
    public int  ModelCalls   => _modelCalls;
    public long TotalTokens  => InputTokens + OutputTokens;
}

/// <summary>
/// Per-turn handle to the active <see cref="TurnMetrics"/>. Mirrors the AsyncLocal+static
/// fallback pattern used by <c>TurnEventBus</c> / <c>ApprovalGate</c> so the metrics flow
/// reliably even when MAF's HandoffAgentExecutor suppresses ExecutionContext between
/// agent transitions.
/// </summary>
public static class TurnMetricsBus
{
    private static readonly AsyncLocal<TurnMetrics?> _current = new();
    private static TurnMetrics? _staticFallback;

    public static TurnMetrics? Current
    {
        get => _current.Value ?? _staticFallback;
        set
        {
            _current.Value = value;
            _staticFallback = value;
        }
    }
}

/// <summary>
/// Best-effort USD cost estimate per (input, output) token counts, per Azure OpenAI
/// list pricing as of mid-2025. Real billing follows the deployment SKU and region; this
/// is for the on-screen badge and is not authoritative.
/// </summary>
public static class TokenCostEstimator
{
    public static decimal EstimateUsd(string? deploymentName, long inputTokens, long outputTokens)
    {
        var (inputPerMillion, outputPerMillion) = RatesFor(deploymentName);
        return (inputTokens  * inputPerMillion  / 1_000_000m)
             + (outputTokens * outputPerMillion / 1_000_000m);
    }

    private static (decimal Input, decimal Output) RatesFor(string? deployment)
    {
        var name = (deployment ?? string.Empty).ToLowerInvariant();
        return name switch
        {
            var n when n.Contains("4o-mini")     => (0.15m,  0.60m),
            var n when n.Contains("4o")          => (2.50m, 10.00m),
            var n when n.Contains("4-turbo")     => (10.00m, 30.00m),
            var n when n.Contains("4")           => (30.00m, 60.00m),
            var n when n.Contains("3.5")         => (0.50m,  1.50m),
            var n when n.Contains("o1-mini")     => (3.00m, 12.00m),
            var n when n.Contains("o1")          => (15.00m, 60.00m),
            _                                    => (0.15m,  0.60m), // default to gpt-4o-mini
        };
    }
}
