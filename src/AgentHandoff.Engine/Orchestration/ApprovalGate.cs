namespace AgentHandoff.Engine.Orchestration;

/// <summary>One pending approval — the data the UI needs to show "Approve / Deny" buttons.</summary>
public sealed record ApprovalRequest(
    string Id,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments,
    DateTimeOffset CreatedAt);

/// <summary>
/// The async-local context the orchestrator installs before running the workflow.
/// </summary>
public sealed class ApprovalContext
{
    public required Func<ApprovalRequest, CancellationToken, Task<bool>> Provider { get; init; }
}

/// <summary>
/// Human-in-the-loop tool approval. Tool implementations (e.g. <c>IssueRefund</c>) call
/// <see cref="RequestAsync"/> before performing destructive work.
///
/// Like <see cref="TurnEventBus"/>, this prefers <see cref="AsyncLocal{T}"/> but falls back
/// to a process-wide static when the workflow's executor suppresses ExecutionContext flow
/// between handoff transitions. Safe for the single-active-session demo case.
/// </summary>
public static class ApprovalGate
{
    private static readonly AsyncLocal<ApprovalContext?> _context = new();
    private static ApprovalContext? _staticFallback;

    public static ApprovalContext? Current
    {
        get => _context.Value ?? _staticFallback;
        set
        {
            _context.Value = value;
            _staticFallback = value;
        }
    }

    public static async Task<bool> RequestAsync(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct = default)
    {
        var ctx = Current;
        if (ctx is null) return true; // headless / no UI attached

        var req = new ApprovalRequest(
            Id:        Guid.NewGuid().ToString("N"),
            ToolName:  toolName,
            Arguments: arguments,
            CreatedAt: DateTimeOffset.UtcNow);

        return await ctx.Provider(req, ct).ConfigureAwait(false);
    }
}
