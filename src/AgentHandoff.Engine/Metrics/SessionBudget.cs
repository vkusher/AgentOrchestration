using AgentHandoff.Engine.Configuration;

namespace AgentHandoff.Engine.Metrics;

/// <summary>
/// Per-session running totals. Updated by the chat-client middleware on every model call;
/// consulted by the orchestrator at the start of each turn for block-mode enforcement.
/// </summary>
public sealed class SessionBudget
{
    private long _inputTokens;
    private long _outputTokens;
    private long _costMicroUsd;   // USD × 1_000_000 — fits in a long, supports atomic adds

    public BudgetOptions Options { get; }

    public SessionBudget(BudgetOptions options)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public long    InputTokens  => Interlocked.Read(ref _inputTokens);
    public long    OutputTokens => Interlocked.Read(ref _outputTokens);
    public long    TotalTokens  => InputTokens + OutputTokens;
    public decimal CostUsd      => Interlocked.Read(ref _costMicroUsd) / 1_000_000m;

    public bool IsExceeded =>
        Options.Mode != BudgetMode.Off
        && ((Options.TokensPerSession > 0 && TotalTokens >= Options.TokensPerSession)
         || (Options.UsdPerSession    > 0 && CostUsd      >= Options.UsdPerSession));

    public bool IsWarning =>
        Options.Mode != BudgetMode.Off
        && ((Options.TokensPerSession > 0 && TotalTokens >= Options.TokensPerSession * 0.8)
         || (Options.UsdPerSession    > 0 && CostUsd      >= Options.UsdPerSession    * 0.8m));

    public void Add(long inputTokens, long outputTokens, decimal costUsd)
    {
        if (Options.Mode == BudgetMode.Off) return;
        if (inputTokens  > 0) Interlocked.Add(ref _inputTokens,  inputTokens);
        if (outputTokens > 0) Interlocked.Add(ref _outputTokens, outputTokens);
        if (costUsd      > 0) Interlocked.Add(ref _costMicroUsd, (long)(costUsd * 1_000_000m));
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _inputTokens, 0);
        Interlocked.Exchange(ref _outputTokens, 0);
        Interlocked.Exchange(ref _costMicroUsd, 0);
    }
}

/// <summary>
/// AsyncLocal+static-fallback bus, mirroring <c>TurnMetricsBus</c>. The orchestrator
/// installs the active session's budget here; the chat-client middleware reads it.
/// </summary>
public static class SessionBudgetBus
{
    private static readonly AsyncLocal<SessionBudget?> _current = new();
    private static SessionBudget? _staticFallback;

    public static SessionBudget? Current
    {
        get => _current.Value ?? _staticFallback;
        set
        {
            _current.Value = value;
            _staticFallback = value;
        }
    }
}
