namespace AgentHandoff.Engine.Configuration;

public enum BudgetMode
{
    /// <summary>No tracking or enforcement.</summary>
    Off,

    /// <summary>Track usage; never block. UI surfaces warnings at 80%/100%.</summary>
    Warn,

    /// <summary>Track usage; refuse new turns once the budget is exhausted.</summary>
    Block,
}

/// <summary>
/// Per-session budget — tokens and USD cost. Enforced by the orchestrator at the start
/// of each turn (soft block: a turn that crosses the threshold still completes; the
/// next turn is rejected).
/// </summary>
public sealed class BudgetOptions
{
    /// <summary>0 = unlimited.</summary>
    public long TokensPerSession { get; set; } = 100_000;

    /// <summary>0 = unlimited.</summary>
    public decimal UsdPerSession { get; set; } = 0.10m;

    public BudgetMode Mode { get; set; } = BudgetMode.Warn;
}
