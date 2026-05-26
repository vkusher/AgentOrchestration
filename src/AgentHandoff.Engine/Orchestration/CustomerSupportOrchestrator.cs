using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AgentHandoff.Engine.Agents;
using AgentHandoff.Engine.Approvals;
using AgentHandoff.Engine.Configuration;
using AgentHandoff.Engine.Metrics;
using AgentHandoff.Engine.Sentiment;
using AgentHandoff.Engine.Sessions;
using AgentHandoff.Engine;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentHandoff.Engine.Orchestration;

/// <summary>
/// Drives the multi-agent workflow built with <c>AgentWorkflowBuilder.CreateHandoffBuilderWith</c>.
/// Streams structured <see cref="AgentEvent"/>s describing each token, handoff and tool call so a
/// UI can render the flow in real time.
///
/// Mesh:
///     triage  ↔  banking_info
///     triage  ↔  accounts_and_cards
///     triage  ↔  billing
///     accounts_and_cards ↔ billing
///     banking_info → accounts_and_cards
///
/// Reference: https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff?pivots=programming-language-csharp
///
/// Implementation note — event merging:
///   The workflow's WatchStreamAsync only produces events when the workflow advances. When a tool
///   function (e.g. IssueRefund) is awaiting human approval, the workflow is paused inside that
///   function and produces nothing, so any "side-channel" events (approval requests, guardrail
///   verdicts, A2A telemetry) need a path to the consumer that doesn't depend on the workflow
///   making forward progress. We pump everything into a shared <see cref="Channel{T}"/> from a
///   background task; the consumer reads from that channel and yields. This way an approval
///   request published from inside a paused tool reaches the UI immediately.
/// </summary>
public sealed class CustomerSupportOrchestrator
{
    private readonly AgentBundle _bundle;
    private readonly AIAgent _entryAgent;
    private readonly string _entryAgentId;
    private readonly string _escalationAgentId;
    private readonly string _approvalAgentId;
    private readonly string? _deploymentName;
    private readonly SentimentAnalyzer _sentiment;
    private readonly SessionBudget _budget;
    private readonly ILogger<CustomerSupportOrchestrator>? _log;
    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _turnLock = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pendingApprovals = new();
    private readonly ISessionRegistry? _registry;
    private readonly ApprovalOptions _approvalOptions;
    private readonly string _sessionId;
    private readonly IApprovalPublisher _approvalPublisher;

    public CustomerSupportOrchestrator(
        AgentBundle bundle,
        ILogger<CustomerSupportOrchestrator>? log = null,
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

        _entryAgentId = _bundle.Runtime.EntryAgentId;
        _escalationAgentId = _bundle.Runtime.EscalationAgentId;
        _approvalAgentId = string.IsNullOrWhiteSpace(_bundle.Runtime.ApprovalAgentId)
            ? _entryAgentId
            : _bundle.Runtime.ApprovalAgentId;
        _entryAgent = Required(_entryAgentId);

        _registry?.TouchSession(_sessionId, SessionStatus.Idle, _entryAgentId);
    }

    public string SessionId => _sessionId;

    public IEnumerable<AgentDescriptor> Agents => _bundle.Registry.All;

    /// <summary>
    /// Process one user turn and stream <see cref="AgentEvent"/>s as the workflow emits them.
    /// </summary>
    public async IAsyncEnumerable<AgentEvent> ChatAsync(
        string userMessage,
        Action<AgentEvent>? sideEffect = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await _turnLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _registry?.TouchSession(_sessionId, SessionStatus.Active, _entryAgentId);
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

        // ── BUDGET GATE ─────────────────────────────────────────────────────
        // Block-mode: refuse to start a turn if the session budget is already exhausted.
        if (_budget.Options.Mode == BudgetMode.Block && _budget.IsExceeded)
        {
            yield return Emit(new BudgetExceededEvent(
                _entryAgentId, _budget.CostUsd, _budget.Options.UsdPerSession,
                _budget.TotalTokens, _budget.Options.TokensPerSession,
                DateTimeOffset.UtcNow), sideEffect);

            var blockText =
                $"You've reached this session's budget limit (${_budget.CostUsd:F4} of " +
                $"${_budget.Options.UsdPerSession:F4} / {_budget.TotalTokens:N0} of " +
                $"{_budget.Options.TokensPerSession:N0} tokens). Click \"New session\" to continue.";

            var entry = _bundle.Registry.FindById(_entryAgentId);
            yield return Emit(new AgentSwitchedEvent(_entryAgentId, entry?.DisplayName ?? _entryAgentId, entry?.Role ?? "router", DateTimeOffset.UtcNow), sideEffect);
            yield return Emit(new AgentTokenEvent(_entryAgentId, blockText, DateTimeOffset.UtcNow), sideEffect);
            yield return Emit(new MessageCompletedEvent(_entryAgentId, blockText, DateTimeOffset.UtcNow), sideEffect);
            yield return Emit(SnapshotEvent(_entryAgentId), sideEffect);
            yield return Emit(new TurnCompletedEvent(_entryAgentId, DateTimeOffset.UtcNow), sideEffect);
            yield break;
        }

        // Install the budget on the bus so MetricsChatClient updates it on every model call.
        var prevBudget = SessionBudgetBus.Current;
        SessionBudgetBus.Current = _budget;

        // ── SENTIMENT / ESCALATION GATE ─────────────────────────────────────
        // Score the user's message before doing anything else. If frustration / urgency /
        // explicit-handoff signals exceed thresholds, short-circuit the workflow and route
        // straight to a synthetic "Human Supervisor" response.
        var verdict = _sentiment.Analyze(userMessage);
        yield return Emit(new SentimentScoredEvent(
            AgentId:        _entryAgentId,
            Frustration:    verdict.Frustration,
            Urgency:        verdict.Urgency,
            ShouldEscalate: verdict.ShouldEscalate,
            Reason:         verdict.Reason,
            Timestamp:      DateTimeOffset.UtcNow), sideEffect);

        if (verdict.ShouldEscalate)
        {
            await foreach (var ae in EscalateAsync(verdict, sideEffect, cancellationToken)
                                          .ConfigureAwait(false))
            {
                yield return ae;
            }
            yield break;
        }

        // Build a fresh workflow per turn.
        var handoffBuilder = AgentWorkflowBuilder.CreateHandoffBuilderWith(_entryAgent);
        foreach (var edge in _bundle.Runtime.HandoffEdges)
        {
            handoffBuilder = handoffBuilder.WithHandoffs(Required(edge.FromAgentId), new[] { Required(edge.ToAgentId) });
        }
        var workflow = handoffBuilder.Build();

        // ── Single output channel ────────────────────────────────────────────
        // Both the workflow-event translator AND any side-channel publisher (guardrails,
        // A2A telemetry, ApprovalGate) write here. The consumer reads & yields. Critically,
        // a side event published from inside a paused tool will be yielded *immediately*
        // even though the workflow is making no forward progress.
        var output = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var prevSink = TurnEventBus.Current;
        TurnEventBus.Current = e => output.Writer.TryWrite(e);

        // ── Per-turn metrics — populated by MetricsChatClient on every model call.
        var prevMetrics = TurnMetricsBus.Current;
        var metrics = new TurnMetrics();
        TurnMetricsBus.Current = metrics;
        var turnTimer = Stopwatch.StartNew();

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
                    AgentId:    _approvalAgentId,
                    ToolName:   req.ToolName,
                    Arguments:  req.Arguments,
                    CreatedAt:  now,
                    ExpiresAt:  expiresAt,
                    Status:     ApprovalStatus.Pending));
                _registry?.TouchSession(_sessionId, SessionStatus.AwaitingApproval, _approvalAgentId);

                var argsJson = JsonSerializer.Serialize(req.Arguments);
                output.Writer.TryWrite(new ApprovalRequestedEvent(
                    AgentId:       _approvalAgentId,
                    ApprovalId:    req.Id,
                    ToolName:      req.ToolName,
                    ArgumentsJson: argsJson,
                    Timestamp:     now));

                // Best-effort fan-out to Event Grid (no-op when disabled).
                _ = _approvalPublisher.PublishRequestAsync(new ApprovalRequestEnvelope(
                    ApprovalId: req.Id,
                    SessionId:  _sessionId,
                    AgentId:    _approvalAgentId,
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
                        _registry?.TouchSession(_sessionId, SessionStatus.Active, _approvalAgentId);
                        return result;
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // Timed out waiting for human reviewer.
                        var autoOutcome = _approvalOptions.AutoDenyOnTimeout;
                        _registry?.TryResolveApproval(req.Id, ApprovalStatus.Expired, decidedBy: "system",
                            reason: $"Auto-{(autoOutcome ? "denied" : "approved")} after {_approvalOptions.Timeout} timeout.",
                            out _);
                        _registry?.TouchSession(_sessionId, SessionStatus.Expired, _approvalAgentId);
                        return !autoOutcome;
                    }
                }
                finally
                {
                    _pendingApprovals.TryRemove(req.Id, out _);
                }
            },
        };

        // Pump workflow events into the channel on a background task. We do this rather than
        // iterate WatchStreamAsync directly so that the consumer below can read side events
        // even when the workflow is paused.
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                var run = await InProcessExecution
                    .StreamAsync(workflow, _history, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await run.TrySendMessageAsync(new TurnToken(emitEvents: true)).ConfigureAwait(false);

                string? lastExecutorId = null;
                var pendingText = new StringBuilder();
                var callIdToName = new Dictionary<string, string>(StringComparer.Ordinal);
                List<ChatMessage>? finalMessages = null;

                await foreach (var evt in run.WatchStreamAsync().WithCancellation(cancellationToken))
                {
                    if (evt is AgentRunUpdateEvent token)
                    {
                        if (!string.Equals(token.ExecutorId, lastExecutorId, StringComparison.Ordinal))
                        {
                            if (lastExecutorId is not null && pendingText.Length > 0)
                            {
                                output.Writer.TryWrite(new MessageCompletedEvent(
                                    lastExecutorId, pendingText.ToString(), DateTimeOffset.UtcNow));
                                pendingText.Clear();
                            }

                            if (lastExecutorId is not null)
                            {
                                output.Writer.TryWrite(new HandoffEvent(
                                    lastExecutorId, token.ExecutorId, "workflow re-routed", DateTimeOffset.UtcNow));
                            }

                            var d = _bundle.Registry.FindById(token.ExecutorId);
                            output.Writer.TryWrite(new AgentSwitchedEvent(
                                token.ExecutorId,
                                d?.DisplayName ?? token.ExecutorId,
                                d?.Role ?? "unknown",
                                DateTimeOffset.UtcNow));

                            lastExecutorId = token.ExecutorId;
                        }

                        var chunk = token.Update.Text;
                        if (!string.IsNullOrEmpty(chunk))
                        {
                            pendingText.Append(chunk);
                            output.Writer.TryWrite(new AgentTokenEvent(
                                token.ExecutorId, chunk, DateTimeOffset.UtcNow));
                        }

                        foreach (var toolEvt in ExtractToolEvents(token.ExecutorId, token.Update, callIdToName))
                            output.Writer.TryWrite(toolEvt);
                    }
                    else if (evt is WorkflowOutputEvent wfOut)
                    {
                        finalMessages = wfOut.As<List<ChatMessage>>();
                    }
                }

                if (lastExecutorId is not null && pendingText.Length > 0)
                {
                    output.Writer.TryWrite(new MessageCompletedEvent(
                        lastExecutorId, pendingText.ToString(), DateTimeOffset.UtcNow));
                }

                if (finalMessages is not null)
                {
                    for (var i = _history.Count; i < finalMessages.Count; i++)
                    {
                        _history.Add(finalMessages[i]);
                    }
                }

                // Per-turn output guardrail — appears at the very end of the timeline.
                output.Writer.TryWrite(new GuardrailEvent(
                    lastExecutorId ?? "turn", "output", "passed", "ok", DateTimeOffset.UtcNow));

                // Per-turn metrics — latency, token totals, USD cost estimate.
                turnTimer.Stop();
                output.Writer.TryWrite(new TurnMetricsEvent(
                    AgentId:         lastExecutorId ?? "turn",
                    InputTokens:     metrics.InputTokens,
                    OutputTokens:    metrics.OutputTokens,
                    ModelCalls:      metrics.ModelCalls,
                    ElapsedMs:       turnTimer.ElapsedMilliseconds,
                    EstimatedCostUsd: TokenCostEstimator.EstimateUsd(_deploymentName, metrics.InputTokens, metrics.OutputTokens),
                    Timestamp:       DateTimeOffset.UtcNow));

                // Per-turn budget snapshot — flows to the React budget bar.
                output.Writer.TryWrite(SnapshotEvent(lastExecutorId ?? "turn"));

                output.Writer.TryWrite(new TurnCompletedEvent(
                    lastExecutorId ?? _entryAgentId, DateTimeOffset.UtcNow));
            }
            catch (OperationCanceledException) { /* expected on disconnect */ }
            catch (Exception ex)
            {
                _log?.LogError(ex, "Workflow pump failed");
                output.Writer.TryWrite(new ErrorEvent("turn", ex.Message, DateTimeOffset.UtcNow));
            }
            finally
            {
                output.Writer.Complete();
            }
        }, cancellationToken);

        // ── Drain & yield ────────────────────────────────────────────────────
        try
        {
            await foreach (var ae in output.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                sideEffect?.Invoke(ae);
                yield return ae;
            }
        }
        finally
        {
            TurnEventBus.Current = prevSink;
            ApprovalGate.Current = prevApproval;
            TurnMetricsBus.Current = prevMetrics;
            SessionBudgetBus.Current = prevBudget;

            // Best-effort wait for the pump task to finish so cancellation propagates cleanly.
            try { await pumpTask.ConfigureAwait(false); }
            catch { /* already surfaced as ErrorEvent */ }
        }
    }

    public void Reset()
    {
        _history.Clear();
        _budget.Reset();
        _registry?.TouchSession(_sessionId, SessionStatus.Idle, _entryAgentId);
        _registry?.Append(new SessionAuditEntry(_sessionId, "session.reset", "history cleared", DateTimeOffset.UtcNow));
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

    /// <summary>
    /// Synthesises a "human supervisor" response when the sentiment classifier triggers.
    /// Emits the events the UI needs to render the conversation as if a special agent
    /// (human_queue) had handled the turn, plus an <see cref="EscalationEvent"/> banner.
    /// </summary>
    private async IAsyncEnumerable<AgentEvent> EscalateAsync(
        SentimentVerdict verdict,
        Action<AgentEvent>? sideEffect,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Detect approximate language from the latest user message so we respond appropriately.
        var lastUser = _history.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var hebrew   = lastUser.Any(c => c >= 0x0590 && c <= 0x05FF);

        var caseId = $"ESC-{Guid.NewGuid().ToString("N")[..6].ToUpperInvariant()}";
        var humanText = hebrew
            ? $"שלום, אני מעבירה את הפנייה שלך מיידית למפקח אנושי. " +
              $"בנקאי בכיר ייצור איתך קשר תוך כ-5 דקות. " +
              $"מספר אסמכתא: {caseId}. אני מתנצלת על אי-הנוחות ומודה לך על הסבלנות."
            : $"I'm escalating your case to a human supervisor right now. " +
              $"A senior banker will be with you within 5 minutes. " +
              $"Your reference is {caseId}. I'm sorry for the trouble — thank you for your patience.";

        var agentId = _escalationAgentId;
        var escalationDescriptor = _bundle.Registry.FindById(agentId);

        yield return Emit(new AgentSwitchedEvent(
            agentId,
            escalationDescriptor?.DisplayName ?? "Human Supervisor",
            escalationDescriptor?.Role ?? "escalation",
            DateTimeOffset.UtcNow), sideEffect);

        yield return Emit(new EscalationEvent(
            agentId, verdict.Reason, caseId, DateTimeOffset.UtcNow), sideEffect);

        // Emit the message text as a single token so the chat panel renders it cleanly.
        yield return Emit(new AgentTokenEvent(
            agentId, humanText, DateTimeOffset.UtcNow), sideEffect);

        yield return Emit(new MessageCompletedEvent(
            agentId, humanText, DateTimeOffset.UtcNow), sideEffect);

        // Append to history so the next turn (if any) preserves context.
        _history.Add(new ChatMessage(ChatRole.Assistant, humanText));

        // No model calls were made — emit zero metrics so the per-turn badge still appears
        // and the UI can show the cost saving.
        yield return Emit(new TurnMetricsEvent(
            AgentId:          agentId,
            InputTokens:      0,
            OutputTokens:     0,
            ModelCalls:       0,
            ElapsedMs:        0,
            EstimatedCostUsd: 0m,
            Timestamp:        DateTimeOffset.UtcNow), sideEffect);

        yield return Emit(new TurnCompletedEvent(agentId, DateTimeOffset.UtcNow), sideEffect);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Called by the API when the React UI sends Approve / Deny for an in-flight tool call.
    /// Returns true if the approval id was outstanding, false if it was already resolved or unknown.
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
                _approvalAgentId, approvalId, approved, DateTimeOffset.UtcNow));
            tcs.TrySetResult(approved);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Called by the background sweeper or the registry when an approval times out and
    /// must be force-resolved (default: deny). Returns whether the approval was outstanding.
    /// </summary>
    public bool ForceExpireApproval(string approvalId)
    {
        if (_pendingApprovals.TryRemove(approvalId, out var tcs))
        {
            var outcome = !_approvalOptions.AutoDenyOnTimeout;
            TurnEventBus.Publish(new ApprovalDecidedEvent(
                _approvalAgentId, approvalId, outcome, DateTimeOffset.UtcNow));
            tcs.TrySetResult(outcome);
            return true;
        }
        return false;
    }

    private AIAgent Required(string id) =>
        _bundle.Registry.FindById(id)?.Agent
            ?? throw new InvalidOperationException($"Agent '{id}' not registered.");

    /// <summary>
    /// Walks an <see cref="AgentRunResponseUpdate"/>'s contents looking for function-call
    /// activity so the UI can render which tools each agent is using (MCP, local function tools, etc.).
    /// Handoff plumbing tools (handoff_to_*) are filtered out — the dedicated HANDOFF timeline
    /// entry already represents that flow.
    /// </summary>
    private static IEnumerable<AgentEvent> ExtractToolEvents(
        string executorId,
        AgentRunResponseUpdate update,
        Dictionary<string, string> callIdToName)
    {
        if (update.Contents is null) yield break;

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case FunctionCallContent call when !string.IsNullOrEmpty(call.Name):
                {
                    if (IsHandoffPlumbing(call.Name)) break;

                    var argsJson = SafeSerialiseArgs(call.Arguments);
                    var source   = ClassifySource(call.Name);
                    if (!string.IsNullOrEmpty(call.CallId))
                        callIdToName[call.CallId] = call.Name;

                    yield return new ToolCallEvent(
                        executorId, call.Name, argsJson, source, DateTimeOffset.UtcNow);
                    break;
                }

                case FunctionResultContent result:
                {
                    var key = result.CallId ?? string.Empty;
                    if (!callIdToName.TryGetValue(key, out var name))
                        break;

                    var preview = result.Result?.ToString() ?? string.Empty;
                    if (preview.Length > 240) preview = preview[..240] + "…";
                    yield return new ToolResultEvent(executorId, name, preview, DateTimeOffset.UtcNow);
                    break;
                }
            }
        }
    }

    private static bool IsHandoffPlumbing(string toolName) =>
        toolName.StartsWith("handoff_to_", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Convenience helper used at every yield site: invokes the side-effect callback for
    /// console / log handlers and returns the event so it can also be yielded to the SSE stream.
    /// </summary>
    private static AgentEvent Emit(AgentEvent e, Action<AgentEvent>? side)
    {
        side?.Invoke(e);
        return e;
    }
}
