namespace AgentHandoff.Engine.Configuration;

/// <summary>
/// Configuration for long-running session and human-in-the-loop approval behavior.
/// </summary>
public sealed class ApprovalOptions
{
    public const string SectionName = "Approval";

    /// <summary>How long a requested approval remains pending before auto-deny. Default 24h.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often the background sweeper checks for expired approvals. Default 60s.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Outcome when an approval times out. Default Deny (safer).</summary>
    public bool AutoDenyOnTimeout { get; set; } = true;
}
