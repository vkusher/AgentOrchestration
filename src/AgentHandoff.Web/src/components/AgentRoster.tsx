import clsx from "clsx";
import type { AgentInfo, Mode } from "../types";
import { AgentBadge } from "./AgentBadge";

export function AgentRoster({
  agents,
  activeAgentId,
  modeHighlightAgentId,
  mode,
}: {
  agents: AgentInfo[];
  activeAgentId?: string;
  modeHighlightAgentId?: string;
  mode?: Mode;
}) {
  if (agents.length === 0) {
    return (
      <p className="text-xs text-slate-500 italic px-3 py-2">
        Waiting for the engine to register its agents…
      </p>
    );
  }

  return (
    <ul className="space-y-2">
      {agents.map((a) => {
        const highlighted = a.id === modeHighlightAgentId;
        return (
          <li
            key={a.id}
            className={clsx(
              "flex items-start gap-3 rounded-lg border px-3 py-2 transition-colors",
              highlighted
                ? "border-indigo-400/60 bg-indigo-500/10 ring-1 ring-indigo-400/30"
                : "border-slate-800 bg-slate-900/40",
            )}
          >
            <AgentBadge agentId={a.id} displayName={a.displayName} active={a.id === activeAgentId} />
            <div className="flex-1 text-xs text-slate-400 leading-snug">
              {a.description}
              {highlighted && (
                <span className="ml-2 inline-block rounded bg-indigo-500/20 px-1.5 py-0.5 text-[10px] font-semibold uppercase tracking-wide text-indigo-200">
                  {mode === "magentic" ? "Magentic entry" : "Handoff entry"}
                </span>
              )}
            </div>
          </li>
        );
      })}
    </ul>
  );
}
