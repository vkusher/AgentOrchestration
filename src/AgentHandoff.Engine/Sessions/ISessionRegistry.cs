namespace AgentHandoff.Engine.Sessions;

/// <summary>
/// Registry tracking session status, pending approvals, and audit log.
///
/// Implementations:
///  - <see cref="InMemorySessionRegistry"/> — survives the process lifetime only.
///  - (future) Cosmos DB / Redis impl for cross-restart durability.
///
/// The orchestrator updates this registry whenever a session moves between states or
/// when an approval is requested / decided / expired. Reviewer-facing endpoints query
/// this registry without touching the orchestrator at all.
/// </summary>
public interface ISessionRegistry
{
    // ── Session lifecycle ────────────────────────────────────────────────────
    void TouchSession(string sessionId, SessionStatus status, string? currentAgentId = null);
    void IncrementTurn(string sessionId);
    SessionSummary? GetSession(string sessionId);
    IReadOnlyList<SessionSummary> ListSessions(SessionStatus? filter = null);

    // ── Approval queue ───────────────────────────────────────────────────────
    void EnqueueApproval(PendingApproval approval);
    bool TryResolveApproval(string approvalId, ApprovalStatus outcome, string? decidedBy, string? reason, out PendingApproval resolved);
    PendingApproval? GetApproval(string approvalId);
    IReadOnlyList<PendingApproval> ListApprovals(ApprovalStatus? filter = null, string? sessionId = null);
    IReadOnlyList<PendingApproval> GetExpiredPending(DateTimeOffset asOf);

    // ── Audit ────────────────────────────────────────────────────────────────
    void Append(SessionAuditEntry entry);
    IReadOnlyList<SessionAuditEntry> GetAudit(string sessionId);
}
