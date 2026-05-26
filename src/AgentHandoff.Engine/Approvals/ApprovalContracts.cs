namespace AgentHandoff.Engine.Approvals;

/// <summary>Wire schema for an outbound approval request (CloudEvents-compatible payload).</summary>
public sealed record ApprovalRequestEnvelope(
    string ApprovalId,
    string SessionId,
    string AgentId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>Wire schema for an inbound approval decision.</summary>
public sealed record ApprovalDecisionEnvelope(
    string ApprovalId,
    bool Approved,
    string? DecidedBy,
    string? Reason);

/// <summary>
/// Publishes outbound approval requests. Both orchestrators call this from their
/// <c>ApprovalGate</c> provider; the default <c>NullApprovalPublisher</c> is a no-op.
/// </summary>
public interface IApprovalPublisher
{
    Task PublishRequestAsync(ApprovalRequestEnvelope envelope, CancellationToken ct = default);
}

/// <summary>No-op publisher used when Event Grid is disabled.</summary>
public sealed class NullApprovalPublisher : IApprovalPublisher
{
    public Task PublishRequestAsync(ApprovalRequestEnvelope envelope, CancellationToken ct = default)
        => Task.CompletedTask;
}

/// <summary>
/// Resolves an approval against whatever orchestrator owns its pending TCS. Implemented in the
/// API layer so it can walk the in-process <c>SessionStore</c>; the engine only sees the interface
/// (which lets the EventGrid listener call into it without taking a hard dep on the API).
/// </summary>
public interface IApprovalDispatcher
{
    /// <summary>
    /// Returns true if the approval id matched a pending request on this host.
    /// Returns false if it was unknown (already resolved or owned by another replica).
    /// </summary>
    bool Resolve(string approvalId, bool approved, string? decidedBy, string? reason);
}
