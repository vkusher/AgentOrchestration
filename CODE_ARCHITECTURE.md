# AgentHandoff Code Architecture Guide

## Purpose
This document explains how the AgentHandoff codebase is structured and how a user request flows through sessions, agents, workflows, tools, and streaming.

It is intended for developers who need to:
- understand current runtime behavior
- debug orchestration and session issues
- extend the system with new agents or tools

## 1. Solution Structure

### Core projects
- src/AgentHandoff.Engine
  - Shared runtime library.
  - Builds agents, starts MCP connectivity, and provides orchestrators.
- src/AgentHandoff.Api
  - ASP.NET Core host.
  - Exposes chat streaming and session endpoints.
- src/AgentHandoff.Web
  - React UI.
  - Consumes SSE-style events and renders timeline/activity.
- src/AgentHandoff.Console
  - CLI host for local debugging.
  - Uses the same engine and orchestrator logic as API.
- src/AgentHandoff.McpServer
  - Stdio MCP server for embedded mode.
  - Hosts KB tools backed by Azure AI Search.
- src/AgentHandoff.McpServerWeb
  - HTTP wrapper around MCP tools for remote mode.

## 2. Runtime Architecture

### High-level request path
1. Web client sends POST /api/chat/stream with message, sessionId, and mode.
2. API resolves or creates a per-session orchestrator from SessionStore.
3. Orchestrator runs one turn in selected mode:
   - handoff: workflow handoffs among specialists
   - magentic: manager plans sub-tasks then synthesizes
4. Orchestrator emits structured AgentEvent objects.
5. API writes each event to the streaming response.
6. Web hook parses events and updates UI state incrementally.

### Entry points
- API host startup and endpoints: src/AgentHandoff.Api/Program.cs
- Console host startup and interactive loop: src/AgentHandoff.Console/Program.cs
- Engine facade and initialization: src/AgentHandoff.Engine/CustomerSupportEngine.cs

## 3. Session Model

### What a session means
A session is conversation continuity plus cumulative budget state for one user thread.

### Where sessions are stored
- In memory only, in API process.
- SessionStore keeps separate maps per mode:
  - handoff orchestrators
  - magentic orchestrators

This means the same sessionId can hold independent histories for handoff and magentic.

### Session lifecycle
- Created lazily on first request for a given sessionId + mode.
- Reused across subsequent turns.
- Reset endpoint clears history and budget via orchestrator Reset.
- No distributed backing store is used in this implementation.

Key code:
- src/AgentHandoff.Api/Services/SessionStore.cs
- src/AgentHandoff.Api/Program.cs

## 4. Agent System

### Agent construction
AgentFactory builds an AgentBundle with an AgentRegistry and creates:
- triage: routing/front desk
- banking_info: KB and policy specialist
- accounts_and_cards: account/card operations specialist
- billing: refunds and balances specialist
- manager: planning/synthesis agent for magentic mode

Each agent is a ChatClientAgent over Azure OpenAI chat client.

### Agent metadata
AgentRegistry stores AgentDescriptor records (id, display name, role, description, agent instance) and is used by:
- API agent listing endpoint
- orchestrators for display metadata
- workflow and timeline mapping

Key code:
- src/AgentHandoff.Engine/Agents/AgentFactory.cs
- src/AgentHandoff.Engine/Agents/AgentRegistry.cs

## 5. Workflow Modes

## 5.1 Handoff Mode

### Builder topology
CustomerSupportOrchestrator builds a fresh handoff workflow per turn using AgentWorkflowBuilder.
Configured links include:
- triage <-> banking_info
- triage <-> accounts_and_cards
- triage <-> billing
- accounts_and_cards <-> billing
- banking_info -> accounts_and_cards

### Turn execution behavior
For each user message:
1. append user message to history
2. enforce budget gate when block mode is active
3. run sentiment analysis and optionally escalate to human_queue
4. execute workflow stream and translate updates to AgentEvent
5. append final assistant messages to history
6. emit metrics, budget snapshot, and turn completion

### Event pumping model
The orchestrator writes both:
- workflow events
- side-channel events (approvals, guardrails, A2A telemetry)
into a shared channel.

This avoids UI stalls when workflow is paused inside a tool call.

Key code:
- src/AgentHandoff.Engine/Orchestration/CustomerSupportOrchestrator.cs

## 5.2 Magentic Mode

MagenticOrchestrator uses a manager-driven plan/dispatch/synthesize flow:
1. manager creates plan JSON steps
2. each step assigned to specialist
3. specialist responses gathered (with tool events)
4. manager synthesizes final user answer

It is an explicit implementation (not framework magentic builder) due preview API constraints in this repo version.

Key code:
- src/AgentHandoff.Engine/Orchestration/MagenticOrchestrator.cs

## 6. Event Contract and Streaming

### Unified event model
All runtime signals use AgentEvent polymorphic records with type discriminators, including:
- token and message completion
- agent switched and handoff
- tool call and tool result
- guardrail verdicts
- approval requested and approval decided
- sentiment scored and escalation
- plan and subtask events (magentic)
- budget snapshot and budget exceeded
- turn metrics and turn completed
- error

Key code:
- src/AgentHandoff.Engine/Orchestration/AgentEvent.cs

### API streaming protocol
API endpoint POST /api/chat/stream writes streaming frames:
- ready
- agent (payload is AgentEvent)
- done
- error

This is consumed as incremental text stream by the web hook.

Key code:
- src/AgentHandoff.Api/Program.cs
- src/AgentHandoff.Web/src/hooks/useAgentStream.ts

## 7. Tools and Integrations

## 7.1 Local function tools
- BillingTools includes LookupBalance and IssueRefund.
- IssueRefund supports human approval gate for higher refund amounts.

Key code:
- src/AgentHandoff.Engine/Tools/BillingTools.cs

## 7.2 A2A wrapper
Billing is wrapped by InProcessA2A so the orchestrator can treat it as normal agent while still emitting A2A-style telemetry events.

Key code:
- src/AgentHandoff.Engine/A2A/InProcessA2AAgent.cs

## 7.3 MCP integration
CustomerSupportEngine supports two MCP modes configured via McpOptions:
- Embedded mode:
  - uses KnowledgeBaseMcpClient
  - spawns AgentHandoff.McpServer via stdio
- Remote mode:
  - uses RemoteMcpClient
  - discovers and invokes tools over HTTP from AgentHandoff.McpServerWeb

Engine startup attempts MCP init and can continue in degraded mode when MCP fails.

Key code:
- src/AgentHandoff.Engine/CustomerSupportEngine.cs
- src/AgentHandoff.Engine/Mcp/KnowledgeBaseMcpClient.cs
- src/AgentHandoff.Engine/Mcp/RemoteMcpClient.cs
- src/AgentHandoff.McpServer/Program.cs
- src/AgentHandoff.McpServerWeb/Program.cs

## 8. Guardrails, Sentiment, and HITL

### Guardrails
Guardrail middleware wraps all agents through agent builder middleware.
Events are emitted for guardrail decisions so UI timeline reflects pass/block/truncate behavior.

### Sentiment escalation
SentimentAnalyzer computes frustration and urgency with keyword and signal heuristics.
If escalation threshold is met, orchestrator short-circuits normal workflow and emits a synthetic human_queue response.

### Human approval gate
ApprovalGate is set by orchestrator for current turn.
Tools can request approval, which triggers ApprovalRequestedEvent.
UI calls API approve endpoint; orchestrator resolves pending TaskCompletionSource and emits ApprovalDecidedEvent.

Key code:
- src/AgentHandoff.Engine/Guardrails/GuardrailMiddleware.cs
- src/AgentHandoff.Engine/Sentiment/SentimentAnalyzer.cs
- src/AgentHandoff.Engine/Orchestration/ApprovalGate.cs
- src/AgentHandoff.Api/Program.cs

## 9. Metrics and Budgeting

### Per-turn metrics
MetricsChatClient middleware records token usage on each model call and updates:
- TurnMetricsBus for per-turn view
- SessionBudgetBus for cumulative session budget

Turn end emits TurnMetricsEvent with latency, model calls, token counts, and estimated cost.

### Session budget
SessionBudget tracks tokens and estimated USD over session lifetime.
Modes control behavior:
- off
- warn
- block

Block mode prevents new turn processing when limits are exceeded.

Key code:
- src/AgentHandoff.Engine/Metrics/MetricsChatClient.cs
- src/AgentHandoff.Engine/Metrics/SessionBudget.cs

## 10. Concurrency and State Notes

- Orchestrators enforce one active turn at a time with a semaphore.
- SessionStore is process-local and uses concurrent dictionaries.
- TurnEventBus and ApprovalGate use AsyncLocal plus static fallback.
  - This supports current preview workflow behavior where execution context may not flow consistently.
  - The static fallback is acceptable for demo-style single active session assumptions.
  - For strict multi-tenant production isolation, replace fallback with explicit per-turn dependency propagation.

## 11. How to Extend

### Add a new specialist agent
1. Create tools or MCP wrappers needed by the specialist.
2. Add ChatClientAgent in AgentFactory with clear instructions and description.
3. Register descriptor in AgentRegistry.
4. Update handoff mesh in CustomerSupportOrchestrator if needed.
5. Update manager planning instructions for magentic mode.
6. Validate UI behavior from AgentEvent timeline.

### Add a new event type
1. Add new AgentEvent derived record and discriminator.
2. Emit it from orchestrator or middleware.
3. Extend UI event handling in useAgentStream and rendering components.

### Add external persistence
1. Replace SessionStore maps with durable session repository.
2. Serialize/restore conversation history and budget state per mode.
3. Keep orchestration mode separation if desired.

## 12. Suggested Read Order

1. src/AgentHandoff.Api/Program.cs
2. src/AgentHandoff.Api/Services/SessionStore.cs
3. src/AgentHandoff.Engine/CustomerSupportEngine.cs
4. src/AgentHandoff.Engine/Agents/AgentFactory.cs
5. src/AgentHandoff.Engine/Orchestration/CustomerSupportOrchestrator.cs
6. src/AgentHandoff.Engine/Orchestration/MagenticOrchestrator.cs
7. src/AgentHandoff.Engine/Orchestration/AgentEvent.cs
8. src/AgentHandoff.Web/src/hooks/useAgentStream.ts
9. src/AgentHandoff.Engine/Mcp/KnowledgeBaseMcpClient.cs
10. src/AgentHandoff.Engine/Mcp/RemoteMcpClient.cs

This order follows the same direction as live traffic: API entry, session routing, engine setup, orchestration, then UI consumption and MCP details.
