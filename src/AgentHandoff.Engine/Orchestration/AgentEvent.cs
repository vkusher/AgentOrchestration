using System.Text.Json.Serialization;

namespace AgentHandoff.Engine.Orchestration;

/// <summary>
/// Discriminated event union streamed from the engine to any UI (SSE, console, etc.).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(AgentTokenEvent),       typeDiscriminator: "token")]
[JsonDerivedType(typeof(AgentSwitchedEvent),    typeDiscriminator: "agent_switched")]
[JsonDerivedType(typeof(HandoffEvent),          typeDiscriminator: "handoff")]
[JsonDerivedType(typeof(ToolCallEvent),         typeDiscriminator: "tool_call")]
[JsonDerivedType(typeof(ToolResultEvent),       typeDiscriminator: "tool_result")]
[JsonDerivedType(typeof(GuardrailEvent),        typeDiscriminator: "guardrail")]
[JsonDerivedType(typeof(MessageCompletedEvent), typeDiscriminator: "message_completed")]
[JsonDerivedType(typeof(TurnCompletedEvent),    typeDiscriminator: "turn_completed")]
[JsonDerivedType(typeof(ApprovalRequestedEvent),typeDiscriminator: "approval_requested")]
[JsonDerivedType(typeof(ApprovalDecidedEvent),  typeDiscriminator: "approval_decided")]
[JsonDerivedType(typeof(TurnMetricsEvent),      typeDiscriminator: "turn_metrics")]
[JsonDerivedType(typeof(SentimentScoredEvent),  typeDiscriminator: "sentiment_scored")]
[JsonDerivedType(typeof(EscalationEvent),       typeDiscriminator: "escalated")]
[JsonDerivedType(typeof(PlanCreatedEvent),      typeDiscriminator: "plan_created")]
[JsonDerivedType(typeof(SubtaskAssignedEvent),  typeDiscriminator: "subtask_assigned")]
[JsonDerivedType(typeof(SubtaskCompletedEvent), typeDiscriminator: "subtask_completed")]
[JsonDerivedType(typeof(BudgetSnapshotEvent),   typeDiscriminator: "budget_snapshot")]
[JsonDerivedType(typeof(BudgetExceededEvent),   typeDiscriminator: "budget_exceeded")]
[JsonDerivedType(typeof(ErrorEvent),            typeDiscriminator: "error")]
public abstract record AgentEvent(string AgentId, DateTimeOffset Timestamp);

public sealed record AgentTokenEvent(string AgentId, string Token, DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record AgentSwitchedEvent(string AgentId, string AgentDisplayName, string Role, DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record HandoffEvent(string FromAgentId, string ToAgentId, string Reason, DateTimeOffset Timestamp)
    : AgentEvent(FromAgentId, Timestamp);

public sealed record ToolCallEvent(string AgentId, string ToolName, string ArgumentsJson, string Source, DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record ToolResultEvent(string AgentId, string ToolName, string ResultPreview, DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record GuardrailEvent(string AgentId, string Stage, string Verdict, string Reason, DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record MessageCompletedEvent(string AgentId, string Text, DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record TurnCompletedEvent(string AgentId, DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record ErrorEvent(string AgentId, string Message, DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record ApprovalRequestedEvent(
        string AgentId,
        string ApprovalId,
        string ToolName,
        string ArgumentsJson,
        DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record ApprovalDecidedEvent(
        string AgentId,
        string ApprovalId,
        bool Approved,
        DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record TurnMetricsEvent(
        string AgentId,
        long InputTokens,
        long OutputTokens,
        int ModelCalls,
        long ElapsedMs,
        decimal EstimatedCostUsd,
        DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record SentimentScoredEvent(
        string AgentId,
        int Frustration,
        int Urgency,
        bool ShouldEscalate,
        string Reason,
        DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record EscalationEvent(
        string AgentId,
        string Reason,
        string CaseId,
        DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

// ── Magentic-mode planning events ─────────────────────────────────────
public sealed record PlanStep(int Id, string Agent, string Subtask);

public sealed record PlanCreatedEvent(
        string AgentId,
        string Summary,
        IReadOnlyList<PlanStep> Steps,
        DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record SubtaskAssignedEvent(
        string AgentId,
        int StepId,
        string TargetAgent,
        string Subtask,
        DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record SubtaskCompletedEvent(
        string AgentId,
        int StepId,
        string TargetAgent,
        string ResultPreview,
        DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record BudgetSnapshotEvent(
        string AgentId,
        long TokensUsed,
        long TokenLimit,
        decimal CostUsd,
        decimal CostLimit,
        string Mode,
        bool IsWarning,
        bool IsExceeded,
        DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);

public sealed record BudgetExceededEvent(
        string AgentId,
        decimal CostUsd,
        decimal CostLimit,
        long TokensUsed,
        long TokenLimit,
        DateTimeOffset Timestamp)
    : AgentEvent(AgentId, Timestamp);
