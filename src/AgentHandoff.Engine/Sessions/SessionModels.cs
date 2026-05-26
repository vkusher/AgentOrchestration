namespace AgentHandoff.Engine.Sessions;

/// <summary>
/// Lifecycle status of a long-running conversation session.
/// </summary>
public enum SessionStatus
{
    Idle,
    Active,
    AwaitingApproval,
    Completed,
    Failed,
    Expired,
}

/// <summary>
/// Outcome of an approval request.
/// </summary>
public enum ApprovalStatus
{
    Pending,
    Approved,
    Denied,
    Expired,
    Cancelled,
}

/// <summary>
/// Durable view of one pending or decided approval request. This is what reviewers see
/// in the approval queue and what the audit log records.
/// </summary>
public sealed record PendingApproval(
    string ApprovalId,
    string SessionId,
    string AgentId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    ApprovalStatus Status,
    string? DecidedBy = null,
    string? DecisionReason = null,
    DateTimeOffset? DecidedAt = null);

/// <summary>
/// Summary of a session for the sessions list endpoint.
/// </summary>
public sealed record SessionSummary(
    string SessionId,
    SessionStatus Status,
    string? CurrentAgentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int PendingApprovalCount,
    int TurnCount);

/// <summary>
/// Audit log entry — append-only record of significant decisions in a session.
/// </summary>
public sealed record SessionAuditEntry(
    string SessionId,
    string Kind,
    string Detail,
    DateTimeOffset Timestamp,
    string? Actor = null);
