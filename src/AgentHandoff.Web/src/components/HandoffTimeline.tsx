import type { AgentEvent } from "../types";
import { AgentBadge } from "./AgentBadge";

/**
 * Renders the per-turn timeline of agent activity: which agent handled what,
 * tool calls, A2A hops, MCP invocations, and guardrail decisions.
 */
export function HandoffTimeline({ events }: { events: AgentEvent[] }) {
  if (events.length === 0) {
    return (
      <p className="text-xs text-slate-500 italic px-3 py-2">
        Activity will appear here once a message is sent.
      </p>
    );
  }

  return (
    <ol className="relative pl-5 space-y-2">
      <span className="absolute left-1.5 top-1 bottom-1 w-px bg-slate-700/60" />
      {events.map((evt, idx) => (
        <TimelineItem key={idx} evt={evt} />
      ))}
    </ol>
  );
}

function TimelineItem({ evt }: { evt: AgentEvent }) {
  const ts = new Date(evt.timestamp).toLocaleTimeString();

  switch (evt.type) {
    case "agent_switched":
      return (
        <li className="relative">
          <Dot color="bg-indigo-400" />
          <div className="text-xs text-slate-400">
            <span className="text-slate-500 mr-2">{ts}</span>
            <span className="text-slate-300">Routing to&nbsp;</span>
            <AgentBadge agentId={evt.agentId} displayName={evt.agentDisplayName} compact active />
          </div>
        </li>
      );

    case "handoff":
      return (
        <li className="relative">
          <Dot color="bg-yellow-400" />
          <div className="text-xs text-slate-400">
            <span className="text-slate-500 mr-2">{ts}</span>
            <span className="text-yellow-300 font-semibold">HANDOFF</span>
            <span className="mx-1.5 text-slate-500">·</span>
            <AgentBadge agentId={evt.fromAgentId} compact />
            <span className="mx-1.5 text-slate-500">→</span>
            <AgentBadge agentId={evt.toAgentId} compact />
            <span className="ml-2 text-slate-500 italic">{evt.reason}</span>
          </div>
        </li>
      );

    case "tool_call":
      return (
        <li className="relative">
          <Dot color="bg-cyan-400" />
          <div className="text-xs">
            <span className="text-slate-500 mr-2">{ts}</span>
            <span className="text-cyan-300 font-semibold">{evt.source}</span>
            <span className="mx-1.5 text-slate-500">·</span>
            <AgentBadge agentId={evt.agentId} compact />
            <span className="mx-1.5 text-slate-500">→</span>
            <code className="text-slate-300">{evt.toolName}</code>
            {evt.argumentsJson && (
              <span className="ml-2 text-slate-500 italic truncate">{evt.argumentsJson}</span>
            )}
          </div>
        </li>
      );

    case "tool_result":
      return (
        <li className="relative">
          <Dot color="bg-cyan-200" />
          <div className="text-xs text-slate-400">
            <span className="text-slate-500 mr-2">{ts}</span>
            <span className="text-cyan-200">↩ {evt.toolName}</span>
            <span className="ml-2 text-slate-500 italic line-clamp-1">{evt.resultPreview}</span>
          </div>
        </li>
      );

    case "guardrail":
      return (
        <li className="relative">
          <Dot color={
            evt.verdict === "blocked"   ? "bg-red-500" :
            evt.verdict === "redacted"  ? "bg-fuchsia-400" :
            evt.verdict === "truncated" ? "bg-orange-400" : "bg-emerald-400"
          } />
          <div className="text-xs">
            <span className="text-slate-500 mr-2">{ts}</span>
            <span className={
              evt.verdict === "blocked"
                ? "text-red-300 font-semibold"
                : evt.verdict === "passed"
                  ? "text-emerald-300"
                  : "text-fuchsia-300 font-semibold"
            }>
              guardrail/{evt.stage} · {evt.verdict}
            </span>
            <span className="ml-2 text-slate-400 italic">{evt.reason}</span>
          </div>
        </li>
      );

    case "message_completed":
      return (
        <li className="relative">
          <Dot color="bg-slate-500" />
          <div className="text-xs text-slate-500">
            <span className="mr-2">{ts}</span>
            <AgentBadge agentId={evt.agentId} compact />
            <span className="ml-2">message ready</span>
          </div>
        </li>
      );

    case "turn_completed":
      return (
        <li className="relative">
          <Dot color="bg-slate-500" />
          <div className="text-xs text-slate-500">
            <span className="mr-2">{ts}</span>turn complete
          </div>
        </li>
      );

    case "plan_created":
      return (
        <li className="relative">
          <Dot color="bg-fuchsia-400" />
          <div className="text-xs">
            <span className="text-slate-500 mr-2">{ts}</span>
            <span className="text-fuchsia-300 font-semibold">PLAN CREATED</span>
            <span className="ml-2 text-slate-300">{evt.steps.length} step(s)</span>
            <span className="ml-2 text-slate-500 italic line-clamp-1">{evt.summary}</span>
          </div>
          <ol className="mt-1 ml-4 space-y-0.5">
            {evt.steps.map((s) => (
              <li key={s.id} className="text-[11px] text-slate-400">
                <span className="font-mono text-fuchsia-400">#{s.id}</span>
                <span className="mx-1.5 text-slate-600">→</span>
                <AgentBadge agentId={s.agent} compact />
                <span className="ml-2 text-slate-300 italic line-clamp-1">{s.subtask}</span>
              </li>
            ))}
          </ol>
        </li>
      );

    case "subtask_assigned":
      return (
        <li className="relative">
          <Dot color="bg-fuchsia-300" />
          <div className="text-xs text-slate-400">
            <span className="text-slate-500 mr-2">{ts}</span>
            <span className="text-fuchsia-300 font-semibold">▶ SUBTASK</span>
            <span className="ml-2 font-mono text-fuchsia-400">#{evt.stepId}</span>
            <span className="mx-1.5 text-slate-600">→</span>
            <AgentBadge agentId={evt.targetAgent} compact />
            <span className="ml-2 text-slate-500 italic line-clamp-1">{evt.subtask}</span>
          </div>
        </li>
      );

    case "subtask_completed":
      return (
        <li className="relative">
          <Dot color="bg-fuchsia-200" />
          <div className="text-xs text-slate-400">
            <span className="text-slate-500 mr-2">{ts}</span>
            <span className="text-fuchsia-200">↩ SUBTASK</span>
            <span className="ml-2 font-mono text-fuchsia-400">#{evt.stepId}</span>
            <span className="ml-2 text-slate-500 italic line-clamp-1">{evt.resultPreview}</span>
          </div>
        </li>
      );

    case "sentiment_scored":
      return (
        <li className="relative">
          <Dot color={evt.shouldEscalate ? "bg-rose-400" : "bg-slate-500"} />
          <div className="text-xs">
            <span className="text-slate-500 mr-2">{ts}</span>
            <span className={evt.shouldEscalate ? "text-rose-300 font-semibold" : "text-slate-400"}>
              sentiment
            </span>
            <span className="ml-2 text-slate-300">
              frustration <span className="font-mono">{evt.frustration}</span>
              <span className="mx-1.5 text-slate-600">·</span>
              urgency <span className="font-mono">{evt.urgency}</span>
            </span>
            <span className="ml-2 text-slate-500 italic line-clamp-1">{evt.reason}</span>
          </div>
        </li>
      );

    case "escalated":
      return (
        <li className="relative">
          <Dot color="bg-rose-500" />
          <div className="text-xs">
            <span className="text-slate-500 mr-2">{ts}</span>
            <span className="text-rose-300 font-semibold">ESCALATED TO HUMAN SUPERVISOR</span>
            <span className="ml-2 text-slate-300 font-mono">{evt.caseId}</span>
            <span className="ml-2 text-slate-500 italic line-clamp-1">{evt.reason}</span>
          </div>
        </li>
      );

    case "approval_requested":
      return (
        <li className="relative">
          <Dot color="bg-amber-300" />
          <div className="text-xs">
            <span className="text-slate-500 mr-2">{ts}</span>
            <span className="text-amber-300 font-semibold">APPROVAL REQUESTED</span>
            <span className="ml-2 text-slate-300">
              <code>{evt.toolName}</code>
            </span>
            <span className="ml-2 text-slate-500 italic line-clamp-1">{evt.argumentsJson}</span>
          </div>
        </li>
      );

    case "approval_decided":
      return (
        <li className="relative">
          <Dot color={evt.approved ? "bg-emerald-400" : "bg-red-500"} />
          <div className="text-xs">
            <span className="text-slate-500 mr-2">{ts}</span>
            <span className={evt.approved ? "text-emerald-300 font-semibold" : "text-red-300 font-semibold"}>
              APPROVAL {evt.approved ? "GRANTED" : "DENIED"}
            </span>
            <span className="ml-2 text-slate-500 font-mono">{evt.approvalId.slice(0, 8)}…</span>
          </div>
        </li>
      );

    case "error":
      return (
        <li className="relative">
          <Dot color="bg-red-500" />
          <div className="text-xs text-red-300">
            <span className="text-slate-500 mr-2">{ts}</span>
            error: {evt.message}
          </div>
        </li>
      );

    case "token":
    default:
      return null;
  }
}

function Dot({ color }: { color: string }) {
  return (
    <span
      className={`absolute -left-[3px] top-1.5 inline-block h-2 w-2 rounded-full ring-2 ring-slate-950 ${color}`}
    />
  );
}
