using AgentHandoff.Engine.Approvals;

namespace AgentHandoff.Api.Services;

/// <summary>
/// Routes a decided approval to whichever in-process orchestrator owns its pending TCS.
/// Used by both <c>/api/approvals/{id}/decision</c> and the Event Grid listener so there's
/// one source of truth for resolution.
/// </summary>
public sealed class SessionStoreApprovalDispatcher : IApprovalDispatcher
{
    private readonly SessionStore _store;

    public SessionStoreApprovalDispatcher(SessionStore store)
    {
        _store = store;
    }

    public bool Resolve(string approvalId, bool approved, string? decidedBy, string? reason)
    {
        // Try Handoff first, then Magentic. Each ProvideApproval returns true if the id matched.
        foreach (var orch in _store.AllHandoff())
        {
            if (orch.ProvideApproval(approvalId, approved, decidedBy, reason))
                return true;
        }
        foreach (var orch in _store.AllMagentic())
        {
            if (orch.ProvideApproval(approvalId, approved, decidedBy, reason))
                return true;
        }
        return false;
    }
}
