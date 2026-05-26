import type { AgentInfo, ChatTurn, Mode } from "../types";
import { AgentRoster } from "./AgentRoster";
import { HandoffTimeline } from "./HandoffTimeline";

export function ActivityPanel({
  agents,
  turns,
  isStreaming,
  modeHighlightAgentId,
  mode,
}: {
  agents: AgentInfo[];
  turns: ChatTurn[];
  isStreaming: boolean;
  modeHighlightAgentId?: string;
  mode: Mode;
}) {
  const lastTurn = turns[turns.length - 1];
  const events = lastTurn?.events ?? [];

  // Active agent = the agent whose latest open response slot is still streaming.
  let activeAgentId: string | undefined;
  if (lastTurn && isStreaming) {
    const last = lastTurn.responses[lastTurn.responses.length - 1];
    if (last && !last.completed) activeAgentId = last.agentId;
  }

  // Compute simple per-turn stats.
  const handoffCount    = events.filter((e) => e.type === "handoff").length;
  const toolCalls       = events.filter((e) => e.type === "tool_call").length;
  const guardEvents     = events.filter((e) => e.type === "guardrail" && e.verdict !== "passed").length;
  const approvalCount   = events.filter((e) => e.type === "approval_requested").length;
  const escalatedCount  = events.filter((e) => e.type === "escalated").length;

  return (
    <aside className="flex h-full flex-col bg-slate-900/60 backdrop-blur rounded-xl border border-slate-800">
      <header className="px-4 py-3 border-b border-slate-800">
        <h2 className="text-sm font-semibold text-slate-200">Live agent activity</h2>
        <p className="text-[11px] text-slate-500">
          Streaming over Server-Sent Events from <code>/api/chat/stream</code>.
        </p>
      </header>

      <div className="flex-1 overflow-y-auto p-4 space-y-5">
        <section>
          <h3 className="text-xs font-semibold uppercase tracking-wider text-slate-400 mb-2">
            Registered agents
          </h3>
          <AgentRoster
            agents={agents}
            activeAgentId={activeAgentId}
            modeHighlightAgentId={modeHighlightAgentId}
            mode={mode}
          />
        </section>

        <section>
          <div className="flex items-center justify-between mb-2">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-slate-400">
              This turn
            </h3>
            <div className="flex gap-2 text-[11px] text-slate-400 flex-wrap">
              <Stat label="handoffs"   value={handoffCount}    accent="text-yellow-300"  />
              <Stat label="tools"      value={toolCalls}       accent="text-cyan-300"    />
              <Stat label="guards"     value={guardEvents}     accent="text-fuchsia-300" />
              <Stat label="approvals"  value={approvalCount}   accent="text-amber-300"   />
              <Stat label="escalated"  value={escalatedCount}  accent="text-rose-300"    />
            </div>
          </div>

          <HandoffTimeline events={events} />
        </section>
      </div>
    </aside>
  );
}

function Stat({ label, value, accent }: { label: string; value: number; accent: string }) {
  return (
    <span className="inline-flex items-center gap-1 rounded-md bg-slate-800/60 px-1.5 py-0.5">
      <span className={accent}>{value}</span>
      <span className="text-slate-500">{label}</span>
    </span>
  );
}
