using System.Collections.Concurrent;

namespace AgentHandoff.Engine.Sessions;

/// <summary>
/// In-memory <see cref="ISessionRegistry"/>. Survives the lifetime of the process only.
/// Thread-safe. Swap for a Cosmos DB / Redis impl when cross-restart durability is needed.
/// </summary>
public sealed class InMemorySessionRegistry : ISessionRegistry
{
    private sealed class SessionState
    {
        public required string SessionId;
        public SessionStatus Status;
        public string? CurrentAgentId;
        public DateTimeOffset CreatedAt;
        public DateTimeOffset LastActivityAt;
        public int TurnCount;
    }

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new();
    private readonly ConcurrentDictionary<string, PendingApproval> _approvals = new();
    private readonly ConcurrentDictionary<string, List<SessionAuditEntry>> _audit = new();
    private readonly object _auditLock = new();

    public void TouchSession(string sessionId, SessionStatus status, string? currentAgentId = null)
    {
        var now = DateTimeOffset.UtcNow;
        _sessions.AddOrUpdate(
            sessionId,
            _ => new SessionState
            {
                SessionId = sessionId,
                Status = status,
                CurrentAgentId = currentAgentId,
                CreatedAt = now,
                LastActivityAt = now,
                TurnCount = 0,
            },
            (_, existing) =>
            {
                existing.Status = status;
                if (currentAgentId is not null) existing.CurrentAgentId = currentAgentId;
                existing.LastActivityAt = now;
                return existing;
            });
    }

    public void IncrementTurn(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var s))
        {
            Interlocked.Increment(ref s.TurnCount);
            s.LastActivityAt = DateTimeOffset.UtcNow;
        }
    }

    public SessionSummary? GetSession(string sessionId)
        => _sessions.TryGetValue(sessionId, out var s) ? ToSummary(s) : null;

    public IReadOnlyList<SessionSummary> ListSessions(SessionStatus? filter = null)
        => _sessions.Values
            .Where(s => filter is null || s.Status == filter)
            .OrderByDescending(s => s.LastActivityAt)
            .Select(ToSummary)
            .ToList();

    private SessionSummary ToSummary(SessionState s) => new(
        SessionId: s.SessionId,
        Status: s.Status,
        CurrentAgentId: s.CurrentAgentId,
        CreatedAt: s.CreatedAt,
        LastActivityAt: s.LastActivityAt,
        PendingApprovalCount: _approvals.Values.Count(a => a.SessionId == s.SessionId && a.Status == ApprovalStatus.Pending),
        TurnCount: s.TurnCount);

    public void EnqueueApproval(PendingApproval approval)
    {
        _approvals[approval.ApprovalId] = approval;
        Append(new SessionAuditEntry(
            approval.SessionId,
            "approval.requested",
            $"{approval.ToolName} (id={approval.ApprovalId}, agent={approval.AgentId})",
            approval.CreatedAt));
    }

    public bool TryResolveApproval(
        string approvalId,
        ApprovalStatus outcome,
        string? decidedBy,
        string? reason,
        out PendingApproval resolved)
    {
        while (_approvals.TryGetValue(approvalId, out var current))
        {
            if (current.Status != ApprovalStatus.Pending)
            {
                resolved = current;
                return false;
            }
            var updated = current with
            {
                Status = outcome,
                DecidedBy = decidedBy,
                DecisionReason = reason,
                DecidedAt = DateTimeOffset.UtcNow,
            };
            if (_approvals.TryUpdate(approvalId, updated, current))
            {
                Append(new SessionAuditEntry(
                    updated.SessionId,
                    $"approval.{outcome.ToString().ToLowerInvariant()}",
                    $"{updated.ToolName} (id={approvalId}) by {decidedBy ?? "unknown"}: {reason ?? "(no reason)"}",
                    updated.DecidedAt!.Value,
                    Actor: decidedBy));
                resolved = updated;
                return true;
            }
        }
        resolved = default!;
        return false;
    }

    public PendingApproval? GetApproval(string approvalId)
        => _approvals.TryGetValue(approvalId, out var a) ? a : null;

    public IReadOnlyList<PendingApproval> ListApprovals(ApprovalStatus? filter = null, string? sessionId = null)
        => _approvals.Values
            .Where(a => (filter is null || a.Status == filter)
                     && (sessionId is null || a.SessionId == sessionId))
            .OrderByDescending(a => a.CreatedAt)
            .ToList();

    public IReadOnlyList<PendingApproval> GetExpiredPending(DateTimeOffset asOf)
        => _approvals.Values
            .Where(a => a.Status == ApprovalStatus.Pending && a.ExpiresAt <= asOf)
            .ToList();

    public void Append(SessionAuditEntry entry)
    {
        var list = _audit.GetOrAdd(entry.SessionId, _ => new List<SessionAuditEntry>());
        lock (_auditLock) list.Add(entry);
    }

    public IReadOnlyList<SessionAuditEntry> GetAudit(string sessionId)
    {
        if (!_audit.TryGetValue(sessionId, out var list)) return Array.Empty<SessionAuditEntry>();
        lock (_auditLock) return list.ToArray();
    }
}
