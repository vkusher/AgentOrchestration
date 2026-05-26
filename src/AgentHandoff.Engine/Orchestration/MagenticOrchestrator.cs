using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AgentHandoff.Engine.Agents;
using AgentHandoff.Engine.Approvals;
using AgentHandoff.Engine.Configuration;
using AgentHandoff.Engine.Metrics;
using AgentHandoff.Engine.Sentiment;
using AgentHandoff.Engine.Sessions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.Engine.Orchestration;

/// <summary>
/// Magentic-style orchestrator: a Manager agent decomposes the request into a plan of
/// sub-tasks, dispatches each to a specialist, and synthesises a final answer from the
/// collected results.
///
/// This is a hand-rolled implementation — it doesn't use MAF's built-in
/// <c>AgentWorkflowBuilder.CreateMagenticBuilderWith</c> because that builder isn't
/// available in <c>Microsoft.Agents.AI.Workflows 1.0.0-preview.251002.1</c>. The semantics
/// match: Manager plans + assigns + synthesises; specialists execute. When you upgrade
/// to a build that ships Magentic, swap the body of <see cref="ChatCoreAsync"/> for a
/// builder-based implementation — the public surface is identical.
///
/// Cost characteristic: typically 5-15 model calls per turn vs 1-3 for Handoff. The
/// per-turn metrics badge surfaces the difference.
/// </summary>
public sealed class MagenticOrchestrator
{
    private readonly AgentBundle _bundle;
    private readonly string _managerId;
    private readonly string _fallbackSpecialistId;
    private readonly string _escalationAgentId;
    private readonly AIAgent _manager;
    private readonly Dictionary<string, AIAgent> _specialists;
    private readonly string? _deploymentName;
    private readonly SentimentAnalyzer _sentiment;
    private readonly SessionBudget _budget;
    private readonly ILogger<MagenticOrchestrator>? _log;
    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new();
    private readonly ISessionRegistry? _registry;
    private readonly ApprovalOptions _approvalOptions;
    private readonly string _sessionId;
    private readonly IApprovalPublisher _approvalPublisher;

    public MagenticOrchestrator(
        AgentBundle bundle,
        ILogger<MagenticOrchestrator>? log = null,
        string? deploymentName = null,
        SentimentAnalyzer? sentiment = null,
        BudgetOptions? budgetOptions = null,
        ISessionRegistry? registry = null,
        ApprovalOptions? approvalOptions = null,
        string? sessionId = null,
        IApprovalPublisher? approvalPublisher = null)
    {
        _bundle = bundle ?? throw new ArgumentNullException(nameof(bundle));
        _log = log;
        _deploymentName = deploymentName;
        _sentiment = sentiment ?? new SentimentAnalyzer();
        _budget = new SessionBudget(budgetOptions ?? new BudgetOptions());
        _registry = registry;
        _approvalOptions = approvalOptions ?? new ApprovalOptions();
        _sessionId = sessionId ?? Guid.NewGuid().ToString("N");
        _approvalPublisher = approvalPublisher ?? new NullApprovalPublisher();

        _managerId = _bundle.Runtime.ManagerAgentId;
        _fallbackSpecialistId = _bundle.Runtime.FallbackSpecialistId;
        _escalationAgentId = _bundle.Runtime.EscalationAgentId;
        _manager = Required(_managerId);
        _specialists = _bundle.Runtime.MagenticSpecialistAgentIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(id => id, Required, StringComparer.OrdinalIgnoreCase);

        _registry?.TouchSession(_sessionId, SessionStatus.Idle, _managerId);
    }

    public string SessionId => _sessionId;

    public IEnumerable<AgentDescriptor> Agents => _bundle.Registry.All;

    public async IAsyncEnumerable<AgentEvent> ChatAsync(
        string userMessage,
        Action<AgentEvent>? sideEffect = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _registry?.TouchSession(_sessionId, SessionStatus.Active, _managerId);
            await foreach (var evt in ChatCoreAsync(userMessage, sideEffect, cancellationToken)
                                          .ConfigureAwait(false))
            {
                yield return evt;
            }
        }
        finally
        {
            _registry?.IncrementTurn(_sessionId);
            _registry?.TouchSession(_sessionId, SessionStatus.Idle);
            _turnLock.Release();
        }
    }

    private async IAsyncEnumerable<AgentEvent> ChatCoreAsync(
        string userMessage,
        Action<AgentEvent>? sideEffect,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _history.Add(new ChatMessage(ChatRole.User, userMessage));

        // ── BUDGET GATE (block-mode only) ──────────────────────────────────
        if (_budget.Options.Mode == BudgetMode.Block && _budget.IsExceeded)
        {
            yield return Emit(new BudgetExceededEvent(
                _managerId, _budget.CostUsd, _budget.Options.UsdPerSession,
                _budget.TotalTokens, _budget.Options.TokensPerSession,
                DateTimeOffset.UtcNow), sideEffect);

            var blockText =
                $"Session budget exhausted (${_budget.CostUsd:F4} of ${_budget.Options.UsdPerSession:F4}). " +
                $"Click \"New session\" to continue.";

            var manager = _bundle.Registry.FindById(_managerId);
            yield return Emit(new AgentSwitchedEvent(_managerId, manager?.DisplayName ?? _managerId, manager?.Role ?? "planner", DateTimeOffset.UtcNow), sideEffect);
            yield return Emit(new AgentTokenEvent(_managerId, blockText, DateTimeOffset.UtcNow), sideEffect);
            yield return Emit(new MessageCompletedEvent(_managerId, blockText, DateTimeOffset.UtcNow), sideEffect);
            yield return Emit(SnapshotEvent(_managerId), sideEffect);
            yield return Emit(new TurnCompletedEvent(_managerId, DateTimeOffset.UtcNow), sideEffect);
            yield break;
        }

        var prevBudget = SessionBudgetBus.Current;
        SessionBudgetBus.Current = _budget;

        // ── Sentiment / escalation gate (same as handoff) ──────────────────
        var verdict = _sentiment.Analyze(userMessage);
        yield return Emit(new SentimentScoredEvent(
            _managerId, verdict.Frustration, verdict.Urgency, verdict.ShouldEscalate,
            verdict.Reason, DateTimeOffset.UtcNow), sideEffect);

        if (verdict.ShouldEscalate)
        {
            await foreach (var ae in EscalateAsync(verdict, sideEffect, cancellationToken)
                                          .ConfigureAwait(false))
            {
                yield return ae;
            }
            yield break;
        }

        // ── Per-turn metrics + side-event channel ──────────────────────────
        var prevSink = TurnEventBus.Current;
        var sideEvents = new System.Collections.Concurrent.ConcurrentQueue<AgentEvent>();
        TurnEventBus.Current = sideEvents.Enqueue;

        var prevMetrics = TurnMetricsBus.Current;
        var metrics = new TurnMetrics();
        TurnMetricsBus.Current = metrics;
        var turnTimer = System.Diagnostics.Stopwatch.StartNew();

        // ── Approval gate (human-in-the-loop) — same wiring as Handoff ──────
        var prevApproval = ApprovalGate.Current;
        ApprovalGate.Current = new ApprovalContext
        {
            Provider = async (req, ct) =>
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingApprovals[req.Id] = tcs;

                var now = DateTimeOffset.UtcNow;
                var expiresAt = now + _approvalOptions.Timeout;

                _registry?.EnqueueApproval(new PendingApproval(
                    ApprovalId: req.Id,
                    SessionId:  _sessionId,
                    AgentId:    _managerId,
                    ToolName:   req.ToolName,
                    Arguments:  req.Arguments,
                    CreatedAt:  now,
                    ExpiresAt:  expiresAt,
                    Status:     ApprovalStatus.Pending));
                _registry?.TouchSession(_sessionId, SessionStatus.AwaitingApproval, _managerId);

                var argsJson = JsonSerializer.Serialize(req.Arguments);
                TurnEventBus.Publish(new ApprovalRequestedEvent(
                    AgentId:       _managerId,
                    ApprovalId:    req.Id,
                    ToolName:      req.ToolName,
                    ArgumentsJson: argsJson,
                    Timestamp:     now));

                // Best-effort fan-out to Event Grid (no-op when disabled).
                _ = _approvalPublisher.PublishRequestAsync(new ApprovalRequestEnvelope(
                    ApprovalId: req.Id,
                    SessionId:  _sessionId,
                    AgentId:    _managerId,
                    ToolName:   req.ToolName,
                    Arguments:  req.Arguments,
                    CreatedAt:  now,
                    ExpiresAt:  expiresAt), ct);

                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(_approvalOptions.Timeout);
                    try
                    {
                        var result = await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
                        _registry?.TouchSession(_sessionId, SessionStatus.Active, _managerId);
                        return result;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        var autoOutcome = _approvalOptions.AutoDenyOnTimeout;
                        _registry?.TryResolveApproval(req.Id, ApprovalStatus.Expired, decidedBy: "system",
                            reason: $"Auto-{(autoOutcome ? "denied" : "approved")} after {_approvalOptions.Timeout} timeout.",
                            out _);
                        _registry?.TouchSession(_sessionId, SessionStatus.Expired, _managerId);
                        return !autoOutcome;
                    }
                }
                finally
                {
                    _pendingApprovals.TryRemove(req.Id, out _);
                }
            },
        };

        try
        {
            // ── PHASE 1: Manager plans ─────────────────────────────────────
            yield return Emit(new AgentSwitchedEvent(
                _managerId, AgentDisplayName(_managerId), AgentRole(_managerId), DateTimeOffset.UtcNow), sideEffect);

            var managerCall = Task.Run(() => CallManagerAsync(
                $"Plan sub-tasks for this customer request:\n\n{userMessage}",
                cancellationToken));

            var managerSideEvents = new List<AgentEvent>();
            await foreach (var live in DrainWhileRunningAsync(managerCall, sideEvents, cancellationToken).ConfigureAwait(false))
            {
                managerSideEvents.Add(live);
                yield return Emit(live, sideEffect);
            }
            var planJson = await managerCall.ConfigureAwait(false);

            // If the input guardrail blocked the planning prompt, the manager's "response"
            // is just the block message — bail out instead of synthesising over it.
            if (TryFindGuardrailBlock(managerSideEvents, out var managerBlockReason))
            {
                var blockText = string.IsNullOrWhiteSpace(planJson)
                    ? $"I cannot help with that: {managerBlockReason}. Please rephrase without sensitive information."
                    : planJson;

                yield return Emit(new AgentTokenEvent(_managerId, blockText, DateTimeOffset.UtcNow), sideEffect);
                yield return Emit(new MessageCompletedEvent(_managerId, blockText, DateTimeOffset.UtcNow), sideEffect);
                _history.Add(new ChatMessage(ChatRole.Assistant, blockText));
                turnTimer.Stop();
                yield return Emit(new TurnMetricsEvent(
                    _managerId, metrics.InputTokens, metrics.OutputTokens, metrics.ModelCalls,
                    turnTimer.ElapsedMilliseconds,
                    TokenCostEstimator.EstimateUsd(_deploymentName, metrics.InputTokens, metrics.OutputTokens),
                    DateTimeOffset.UtcNow), sideEffect);
                yield return Emit(SnapshotEvent(_managerId), sideEffect);
                yield return Emit(new TurnCompletedEvent(_managerId, DateTimeOffset.UtcNow), sideEffect);
                yield break;
            }

            var plan = TryParsePlan(planJson);
            if (plan is null || plan.Steps.Count == 0)
            {
                _log?.LogWarning("Manager produced an unparseable plan; falling back to a single configured specialist step.");
                plan = new MagenticPlan("Direct response", new List<PlanStep>
                {
                    new(1, _fallbackSpecialistId, userMessage),
                });
            }

            yield return Emit(new PlanCreatedEvent(
                _managerId, plan.Summary, plan.Steps, DateTimeOffset.UtcNow), sideEffect);

            // ── PHASE 2: Dispatch each sub-task to its specialist ──────────
            var stepResults = new List<(PlanStep Step, string Result)>();

            foreach (var step in plan.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_specialists.TryGetValue(step.Agent, out var specialist))
                {
                    _log?.LogWarning("Plan referenced unknown agent '{Agent}'; skipping step #{Id}.",
                        step.Agent, step.Id);
                    continue;
                }

                // For agents that need the customer's full original message (attachments,
                // inlined <<<ATTACHMENT_BEGIN>>>...<<<ATTACHMENT_END>>> OCR blocks), preserve
                // it verbatim — the manager's rewritten subtask often drops attachments.
                var preserveUserMessage =
                    step.Agent.Equals("transfer_intake",    StringComparison.OrdinalIgnoreCase) ||
                    step.Agent.Equals("mortgage_validator", StringComparison.OrdinalIgnoreCase);
                var subtaskForSpecialist = preserveUserMessage ? userMessage : step.Subtask;

                yield return Emit(new SubtaskAssignedEvent(
                    _managerId, step.Id, step.Agent, subtaskForSpecialist, DateTimeOffset.UtcNow), sideEffect);

                yield return Emit(new AgentSwitchedEvent(
                    step.Agent, AgentDisplayName(step.Agent), "specialist", DateTimeOffset.UtcNow), sideEffect);

                // Stream the specialist's response so we can surface every function call
                // (MCP, local tools) on the activity timeline — exactly what the Handoff
                // orchestrator does. Without this the model still calls the tools internally
                // (FunctionInvokingChatClient handles that), but the events would be invisible.
                var specialistCall = Task.Run(() => CallSpecialistStreamingAsync(
                    step.Agent, specialist, subtaskForSpecialist, cancellationToken));

                var specialistSideEvents = new List<AgentEvent>();
                await foreach (var live in DrainWhileRunningAsync(specialistCall, sideEvents, cancellationToken).ConfigureAwait(false))
                {
                    specialistSideEvents.Add(live);
                    yield return Emit(live, sideEffect);
                }
                var (result, toolEvents) = await specialistCall.ConfigureAwait(false);

                foreach (var te in toolEvents) yield return Emit(te, sideEffect);

                // Surface specialist-level guardrail blocks instead of pretending the result is real.
                if (TryFindGuardrailBlock(specialistSideEvents, out var specBlockReason))
                {
                    var blockText = string.IsNullOrWhiteSpace(result)
                        ? $"I cannot help with that: {specBlockReason}. Please rephrase without sensitive information."
                        : result;

                    yield return Emit(new AgentTokenEvent(step.Agent, blockText, DateTimeOffset.UtcNow), sideEffect);
                    yield return Emit(new MessageCompletedEvent(step.Agent, blockText, DateTimeOffset.UtcNow), sideEffect);
                    _history.Add(new ChatMessage(ChatRole.Assistant, blockText));
                    turnTimer.Stop();
                    yield return Emit(new TurnMetricsEvent(
                        step.Agent, metrics.InputTokens, metrics.OutputTokens, metrics.ModelCalls,
                        turnTimer.ElapsedMilliseconds,
                        TokenCostEstimator.EstimateUsd(_deploymentName, metrics.InputTokens, metrics.OutputTokens),
                        DateTimeOffset.UtcNow), sideEffect);
                    yield return Emit(SnapshotEvent(step.Agent), sideEffect);
                    yield return Emit(new TurnCompletedEvent(step.Agent, DateTimeOffset.UtcNow), sideEffect);
                    yield break;
                }

                stepResults.Add((step, result));

                var preview = result.Length > 240 ? result[..240] + "…" : result;
                yield return Emit(new SubtaskCompletedEvent(
                    _managerId, step.Id, step.Agent, preview, DateTimeOffset.UtcNow), sideEffect);

                // Short-circuit: if transfer_intake returned the structured transfer JSON,
                // that JSON IS the final answer the client wants — skip remaining steps and
                // synthesis (which would otherwise paraphrase or mishandle it).
                if (step.Agent.Equals("transfer_intake", StringComparison.OrdinalIgnoreCase)
                    && IsTransferIntakeJson(result))
                {
                    yield return Emit(new AgentSwitchedEvent(
                        _managerId, AgentDisplayName(_managerId), AgentRole(_managerId), DateTimeOffset.UtcNow), sideEffect);
                    yield return Emit(new AgentTokenEvent(_managerId, result, DateTimeOffset.UtcNow), sideEffect);
                    yield return Emit(new MessageCompletedEvent(_managerId, result, DateTimeOffset.UtcNow), sideEffect);
                    _history.Add(new ChatMessage(ChatRole.Assistant, result));

                    yield return Emit(new GuardrailEvent(
                        _managerId, "output", "passed", "ok", DateTimeOffset.UtcNow), sideEffect);

                    turnTimer.Stop();
                    yield return Emit(new TurnMetricsEvent(
                        _managerId, metrics.InputTokens, metrics.OutputTokens, metrics.ModelCalls,
                        turnTimer.ElapsedMilliseconds,
                        TokenCostEstimator.EstimateUsd(_deploymentName, metrics.InputTokens, metrics.OutputTokens),
                        DateTimeOffset.UtcNow), sideEffect);
                    yield return Emit(SnapshotEvent(_managerId), sideEffect);
                    yield return Emit(new TurnCompletedEvent(_managerId, DateTimeOffset.UtcNow), sideEffect);
                    yield break;
                }
            }

            // ── PHASE 3: Manager synthesises the final answer ──────────────
            yield return Emit(new AgentSwitchedEvent(
                _managerId, AgentDisplayName(_managerId), AgentRole(_managerId), DateTimeOffset.UtcNow), sideEffect);

            var resultsJson = JsonSerializer.Serialize(
                stepResults.Select(r => new { id = r.Step.Id, agent = r.Step.Agent, result = r.Result }));

            var synthesisPrompt =
                $"Synthesise a final answer for the customer.\n\n" +
                $"Original request: {userMessage}\n\n" +
                $"Sub-task results: {resultsJson}";

            var finalAnswer = await CallManagerAsync(synthesisPrompt, cancellationToken).ConfigureAwait(false);

            foreach (var side in DrainSide(sideEvents)) yield return Emit(side, sideEffect);

            // Stream the final answer as a single token (we already have the full text).
            yield return Emit(new AgentTokenEvent(
                _managerId, finalAnswer, DateTimeOffset.UtcNow), sideEffect);
            yield return Emit(new MessageCompletedEvent(
                _managerId, finalAnswer, DateTimeOffset.UtcNow), sideEffect);

            _history.Add(new ChatMessage(ChatRole.Assistant, finalAnswer));

            // ── End-of-turn events ─────────────────────────────────────────
            yield return Emit(new GuardrailEvent(
                _managerId, "output", "passed", "ok", DateTimeOffset.UtcNow), sideEffect);

            turnTimer.Stop();
            yield return Emit(new TurnMetricsEvent(
                AgentId:          _managerId,
                InputTokens:      metrics.InputTokens,
                OutputTokens:     metrics.OutputTokens,
                ModelCalls:       metrics.ModelCalls,
                ElapsedMs:        turnTimer.ElapsedMilliseconds,
                EstimatedCostUsd: TokenCostEstimator.EstimateUsd(_deploymentName, metrics.InputTokens, metrics.OutputTokens),
                Timestamp:        DateTimeOffset.UtcNow), sideEffect);

            yield return Emit(SnapshotEvent(_managerId), sideEffect);

            yield return Emit(new TurnCompletedEvent(_managerId, DateTimeOffset.UtcNow), sideEffect);
        }
        finally
        {
            TurnEventBus.Current = prevSink;
            TurnMetricsBus.Current = prevMetrics;
            SessionBudgetBus.Current = prevBudget;
            ApprovalGate.Current = prevApproval;
        }
    }

    public void Reset()
    {
        _history.Clear();
        _budget.Reset();
        _registry?.TouchSession(_sessionId, SessionStatus.Idle, _managerId);
        _registry?.Append(new SessionAuditEntry(_sessionId, "session.reset", "history cleared", DateTimeOffset.UtcNow));
    }

    /// <summary>
    /// Approve / deny an in-flight tool call. Returns true if the id was outstanding.
    /// </summary>
    public bool ProvideApproval(string approvalId, bool approved, string? decidedBy = null, string? reason = null)
    {
        if (_pendingApprovals.TryRemove(approvalId, out var tcs))
        {
            _registry?.TryResolveApproval(
                approvalId,
                approved ? ApprovalStatus.Approved : ApprovalStatus.Denied,
                decidedBy,
                reason,
                out _);
            TurnEventBus.Publish(new ApprovalDecidedEvent(
                _managerId, approvalId, approved, DateTimeOffset.UtcNow));
            tcs.TrySetResult(approved);
            return true;
        }
        return false;
    }

    /// <summary>Force-resolve a pending approval (used by the timeout sweeper).</summary>
    public bool ForceExpireApproval(string approvalId)
    {
        if (_pendingApprovals.TryRemove(approvalId, out var tcs))
        {
            var outcome = !_approvalOptions.AutoDenyOnTimeout;
            TurnEventBus.Publish(new ApprovalDecidedEvent(
                _managerId, approvalId, outcome, DateTimeOffset.UtcNow));
            tcs.TrySetResult(outcome);
            return true;
        }
        return false;
    }

    private BudgetSnapshotEvent SnapshotEvent(string agentId) =>
        new(AgentId:    agentId,
            TokensUsed: _budget.TotalTokens,
            TokenLimit: _budget.Options.TokensPerSession,
            CostUsd:    _budget.CostUsd,
            CostLimit:  _budget.Options.UsdPerSession,
            Mode:       _budget.Options.Mode.ToString().ToLowerInvariant(),
            IsWarning:  _budget.IsWarning,
            IsExceeded: _budget.IsExceeded,
            Timestamp:  DateTimeOffset.UtcNow);

    // ─────────────────────────── helpers ───────────────────────────

    private async Task<string> CallManagerAsync(string prompt, CancellationToken ct)
    {
        var response = await _manager.RunAsync(
            new ChatMessage(ChatRole.User, prompt), thread: null, options: null, ct).ConfigureAwait(false);
        return response.Text ?? string.Empty;
    }

    /// <summary>
    /// Streams the specialist's response so function-call activity (MCP, local tools) surfaces
    /// as <see cref="ToolCallEvent"/> / <see cref="ToolResultEvent"/> on the activity timeline.
    /// </summary>
    private async Task<(string Text, List<AgentEvent> ToolEvents)> CallSpecialistStreamingAsync(
        string agentId,
        AIAgent specialist,
        string prompt,
        CancellationToken ct)
    {
        var text = new StringBuilder();
        var toolEvents = new List<AgentEvent>();
        var callIdToName = new Dictionary<string, string>(StringComparer.Ordinal);

        await foreach (var update in specialist.RunStreamingAsync(
            new[] { new ChatMessage(ChatRole.User, prompt) },
            thread: null, options: null, ct).ConfigureAwait(false))
        {
            if (!string.IsNullOrEmpty(update.Text))
                text.Append(update.Text);

            ExtractToolEvents(agentId, update, callIdToName, toolEvents);
        }

        return (text.ToString(), toolEvents);
    }

    /// <summary>
    /// Walks an <see cref="AgentRunResponseUpdate"/>'s contents looking for function-call
    /// activity. Mirrors <c>CustomerSupportOrchestrator.ExtractToolEvents</c> so MCP and local
    /// tools surface identically in both orchestration modes.
    /// </summary>
    private static void ExtractToolEvents(
        string executorId,
        AgentRunResponseUpdate update,
        Dictionary<string, string> callIdToName,
        List<AgentEvent> sink)
    {
        if (update.Contents is null) return;

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent call when !string.IsNullOrEmpty(call.Name):
                {
                    var argsJson = SafeSerialiseArgs(call.Arguments);
                    var source   = ClassifySource(call.Name);
                    if (!string.IsNullOrEmpty(call.CallId))
                        callIdToName[call.CallId] = call.Name;

                    sink.Add(new ToolCallEvent(
                        executorId, call.Name, argsJson, source, DateTimeOffset.UtcNow));
                    break;
                }

                case FunctionResultContent result:
                {
                    var key = result.CallId ?? string.Empty;
                    if (!callIdToName.TryGetValue(key, out var name)) break;

                    var preview = result.Result?.ToString() ?? string.Empty;
                    if (preview.Length > 240) preview = preview[..240] + "…";
                    sink.Add(new ToolResultEvent(executorId, name, preview, DateTimeOffset.UtcNow));
                    break;
                }
            }
        }
    }

    private static string SafeSerialiseArgs(IDictionary<string, object?>? args)
    {
        if (args is null || args.Count == 0) return "{}";
        try { return JsonSerializer.Serialize(args); }
        catch { return "{}"; }
    }

    private static string ClassifySource(string toolName) =>
        toolName switch
        {
            "SearchKnowledgeBase" or "ListTopics" => "MCP",
            _ => "local",
        };

    private static bool IsTransferIntakeJson(string? result)
    {
        if (string.IsNullOrWhiteSpace(result)) return false;
        var cleaned = result.Trim();
        if (cleaned.StartsWith("```"))
        {
            var nl = cleaned.IndexOf('\n');
            if (nl > 0) cleaned = cleaned[(nl + 1)..];
            if (cleaned.EndsWith("```")) cleaned = cleaned[..^3];
            cleaned = cleaned.Trim();
        }
        if (!cleaned.StartsWith("{")) return false;
        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            return doc.RootElement.TryGetProperty("fromAccount", out _)
                && doc.RootElement.TryGetProperty("toAccount", out _)
                && doc.RootElement.TryGetProperty("toBank", out _)
                && doc.RootElement.TryGetProperty("amount", out _);
        }
        catch { return false; }
    }

    private static MagenticPlan? TryParsePlan(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        // The model occasionally wraps JSON in ``` fences — strip them.
        var cleaned = json.Trim();
        if (cleaned.StartsWith("```"))
        {
            var firstNewline = cleaned.IndexOf('\n');
            if (firstNewline > 0) cleaned = cleaned[(firstNewline + 1)..];
            if (cleaned.EndsWith("```")) cleaned = cleaned[..^3];
            cleaned = cleaned.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;
            var summary = root.TryGetProperty("summary", out var s) ? s.GetString() ?? "" : "";
            var steps = new List<PlanStep>();
            if (root.TryGetProperty("steps", out var stepsArr) && stepsArr.ValueKind == JsonValueKind.Array)
            {
                var i = 1;
                foreach (var item in stepsArr.EnumerateArray())
                {
                    var id      = item.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                                    ? idEl.GetInt32() : i;
                    var agent   = item.TryGetProperty("agent", out var aEl) ? aEl.GetString() ?? "" : "";
                    var subtask = item.TryGetProperty("subtask", out var tEl) ? tEl.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(agent) && !string.IsNullOrWhiteSpace(subtask))
                        steps.Add(new PlanStep(id, agent, subtask));
                    i++;
                }
            }
            return new MagenticPlan(summary, steps);
        }
        catch (JsonException) { return null; }
    }

    private string AgentDisplayName(string id) =>
        _bundle.Registry.FindById(id)?.DisplayName ?? id;

    private string AgentRole(string id) =>
        _bundle.Registry.FindById(id)?.Role ?? "unknown";

    private async IAsyncEnumerable<AgentEvent> EscalateAsync(
        SentimentVerdict verdict,
        Action<AgentEvent>? sideEffect,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var lastUser = _history.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var hebrew   = lastUser.Any(c => c >= 0x0590 && c <= 0x05FF);
        var caseId = $"ESC-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var humanText = hebrew
            ? $"שלום, אני מעבירה את הפנייה שלך מיידית למפקח אנושי. בנקאי בכיר ייצור איתך קשר תוך כ-5 דקות. " +
              $"מספר אסמכתא: {caseId}."
            : $"I'm escalating your case to a human supervisor right now. A senior banker will be with you " +
              $"within 5 minutes. Your reference is {caseId}.";

        yield return Emit(new AgentSwitchedEvent(
            _escalationAgentId,
            AgentDisplayName(_escalationAgentId),
            AgentRole(_escalationAgentId),
            DateTimeOffset.UtcNow), sideEffect);
        yield return Emit(new EscalationEvent(_escalationAgentId, verdict.Reason, caseId, DateTimeOffset.UtcNow), sideEffect);
        yield return Emit(new AgentTokenEvent(_escalationAgentId, humanText, DateTimeOffset.UtcNow), sideEffect);
        yield return Emit(new MessageCompletedEvent(_escalationAgentId, humanText, DateTimeOffset.UtcNow), sideEffect);

        _history.Add(new ChatMessage(ChatRole.Assistant, humanText));

        yield return Emit(new TurnMetricsEvent(
            _escalationAgentId, 0, 0, 0, 0, 0m, DateTimeOffset.UtcNow), sideEffect);
        yield return Emit(new TurnCompletedEvent(_escalationAgentId, DateTimeOffset.UtcNow), sideEffect);
        await Task.CompletedTask;
    }

    private static IEnumerable<AgentEvent> DrainSide(System.Collections.Concurrent.ConcurrentQueue<AgentEvent> q)
    {
        while (q.TryDequeue(out var e)) yield return e;
    }

    /// <summary>
    /// Polls <paramref name="queue"/> while <paramref name="work"/> is running so events that
    /// the underlying agent publishes mid-call (notably <see cref="ApprovalRequestedEvent"/>
    /// raised by <see cref="ApprovalGate"/> while the tool blocks for a human) reach the
    /// SSE consumer instead of being trapped behind the awaited call.
    /// </summary>
    private static async IAsyncEnumerable<AgentEvent> DrainWhileRunningAsync(
        Task work,
        System.Collections.Concurrent.ConcurrentQueue<AgentEvent> queue,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (!work.IsCompleted)
        {
            while (queue.TryDequeue(out var evt)) yield return evt;
            var delay = Task.Delay(50, ct);
            await Task.WhenAny(work, delay).ConfigureAwait(false);
        }
        while (queue.TryDequeue(out var tail)) yield return tail;
    }

    private static bool TryFindGuardrailBlock(IEnumerable<AgentEvent> events, out string reason)
    {
        foreach (var e in events)
        {
            if (e is GuardrailEvent g && string.Equals(g.Verdict, "blocked", StringComparison.OrdinalIgnoreCase))
            {
                reason = g.Reason;
                return true;
            }
        }
        reason = string.Empty;
        return false;
    }

    private AIAgent Required(string id) =>
        _bundle.Registry.FindById(id)?.Agent
            ?? throw new InvalidOperationException($"Agent '{id}' not registered.");

    private static AgentEvent Emit(AgentEvent e, Action<AgentEvent>? side)
    {
        side?.Invoke(e);
        return e;
    }

    private sealed record MagenticPlan(string Summary, IReadOnlyList<PlanStep> Steps);
}
