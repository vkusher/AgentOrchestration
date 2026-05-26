// ----------------------------------------------------------------------------
// Server-streamed event types — must mirror AgentHandoff.Engine.Orchestration.AgentEvent
// ----------------------------------------------------------------------------

export type AgentRole = "router" | "specialist" | "unknown";

export interface AgentInfo {
  id: string;
  displayName: string;
  role: AgentRole;
  description: string;
}

interface BaseEvent {
  agentId: string;
  timestamp: string;
}

export interface TokenEvent extends BaseEvent {
  type: "token";
  token: string;
}

export interface AgentSwitchedEvent extends BaseEvent {
  type: "agent_switched";
  agentDisplayName: string;
  role: AgentRole;
}

export interface HandoffEvent extends BaseEvent {
  type: "handoff";
  fromAgentId: string;
  toAgentId: string;
  reason: string;
}

export interface ToolCallEvent extends BaseEvent {
  type: "tool_call";
  toolName: string;
  argumentsJson: string;
  source: string; // "MCP" | "local" | "A2A"
}

export interface ToolResultEvent extends BaseEvent {
  type: "tool_result";
  toolName: string;
  resultPreview: string;
}

export interface GuardrailEvent extends BaseEvent {
  type: "guardrail";
  stage: "input" | "output";
  verdict: "passed" | "blocked" | "redacted" | "truncated";
  reason: string;
}

export interface MessageCompletedEvent extends BaseEvent {
  type: "message_completed";
  text: string;
}

export interface TurnCompletedEvent extends BaseEvent {
  type: "turn_completed";
}

export interface ErrorEvent extends BaseEvent {
  type: "error";
  message: string;
}

export interface ApprovalRequestedEvent extends BaseEvent {
  type: "approval_requested";
  approvalId: string;
  toolName: string;
  argumentsJson: string;
}

export interface ApprovalDecidedEvent extends BaseEvent {
  type: "approval_decided";
  approvalId: string;
  approved: boolean;
}

export interface TurnMetricsEvent extends BaseEvent {
  type: "turn_metrics";
  inputTokens: number;
  outputTokens: number;
  modelCalls: number;
  elapsedMs: number;
  estimatedCostUsd: number;
}

export interface SentimentScoredEvent extends BaseEvent {
  type: "sentiment_scored";
  frustration: number;
  urgency: number;
  shouldEscalate: boolean;
  reason: string;
}

export interface EscalatedEvent extends BaseEvent {
  type: "escalated";
  reason: string;
  caseId: string;
}

// ── Magentic-mode planning events ──────────────────────────────────────
export interface PlanStep {
  id: number;
  agent: string;
  subtask: string;
}

export interface PlanCreatedEvent extends BaseEvent {
  type: "plan_created";
  summary: string;
  steps: PlanStep[];
}

export interface SubtaskAssignedEvent extends BaseEvent {
  type: "subtask_assigned";
  stepId: number;
  targetAgent: string;
  subtask: string;
}

export interface SubtaskCompletedEvent extends BaseEvent {
  type: "subtask_completed";
  stepId: number;
  targetAgent: string;
  resultPreview: string;
}

export interface BudgetSnapshotEvent extends BaseEvent {
  type: "budget_snapshot";
  tokensUsed: number;
  tokenLimit: number;
  costUsd: number;
  costLimit: number;
  mode: string;       // "off" | "warn" | "block"
  isWarning: boolean;
  isExceeded: boolean;
}

export interface BudgetExceededEvent extends BaseEvent {
  type: "budget_exceeded";
  costUsd: number;
  costLimit: number;
  tokensUsed: number;
  tokenLimit: number;
}

export interface SessionBudget {
  tokensUsed: number;
  tokenLimit: number;
  costUsd: number;
  costLimit: number;
  mode: string;
  isWarning: boolean;
  isExceeded: boolean;
}

export type Mode = "handoff" | "magentic";

export interface TurnMetrics {
  inputTokens: number;
  outputTokens: number;
  modelCalls: number;
  elapsedMs: number;
  estimatedCostUsd: number;
}

export type AgentEvent =
  | TokenEvent
  | AgentSwitchedEvent
  | HandoffEvent
  | ToolCallEvent
  | ToolResultEvent
  | GuardrailEvent
  | MessageCompletedEvent
  | TurnCompletedEvent
  | ApprovalRequestedEvent
  | ApprovalDecidedEvent
  | TurnMetricsEvent
  | SentimentScoredEvent
  | EscalatedEvent
  | PlanCreatedEvent
  | SubtaskAssignedEvent
  | SubtaskCompletedEvent
  | BudgetSnapshotEvent
  | BudgetExceededEvent
  | ErrorEvent;

export interface PendingApproval {
  approvalId: string;
  toolName: string;
  argumentsJson: string;
  requestedAt: string;
}

export interface ChatAttachment {
  filename: string;
  contentType: string;
  base64: string;
  size: number;
}

// ----------------------------------------------------------------------------
// UI-side state
// ----------------------------------------------------------------------------
export interface ChatTurn {
  id: string;
  user: string;
  attachments?: Array<{ filename: string; contentType: string; size: number }>;
  responses: Array<{
    agentId: string;
    text: string;
    completed: boolean;
  }>;
  events: AgentEvent[];
  status: "running" | "done" | "error";
  metrics?: TurnMetrics;
}
