using AgentHandoff.Engine.Configuration;
using AgentHandoff.Engine.Sessions;
using Microsoft.Extensions.Options;

namespace AgentHandoff.Api.Services;

/// <summary>
/// Periodically scans the session registry for approvals whose ExpiresAt has passed and
/// resolves them. Acts as a safety net for the orchestrator's own WaitAsync timeout
/// (covers cases where the orchestrator task was lost, or where a registry entry was
/// created but never wired to an in-flight TCS — e.g. after a future restart with a
/// persistent store).
/// </summary>
public sealed class ApprovalSweeper : BackgroundService
{
    private readonly ISessionRegistry _registry;
    private readonly SessionStore _store;
    private readonly ApprovalOptions _options;
    private readonly ILogger<ApprovalSweeper> _log;

    public ApprovalSweeper(ISessionRegistry registry,
                           SessionStore store,
                           IOptions<ApprovalOptions> options,
                           ILogger<ApprovalSweeper> log)
    {
        _registry = registry;
        _store    = store;
        _options  = options.Value;
        _log      = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = _options.SweepInterval > TimeSpan.Zero ? _options.SweepInterval : TimeSpan.FromMinutes(1);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var expired = _registry.GetExpiredPending(now);
                foreach (var a in expired)
                {
                    if (_store.TryGetHandoff(a.SessionId, out var orch))
                    {
                        orch.ForceExpireApproval(a.ApprovalId);
                    }
                    else if (_store.TryGetMagentic(a.SessionId, out var magentic))
                    {
                        magentic.ForceExpireApproval(a.ApprovalId);
                    }
                    _registry.TryResolveApproval(
                        a.ApprovalId,
                        ApprovalStatus.Expired,
                        decidedBy: "system",
                        reason: $"Auto-expired by sweeper at {now:O}.",
                        out _);
                }

                if (expired.Count > 0)
                    _log.LogInformation("ApprovalSweeper expired {Count} approvals.", expired.Count);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ApprovalSweeper iteration failed.");
            }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { /* shutdown */ }
        }
    }
}
