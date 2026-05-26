import clsx from "clsx";
import type { SessionBudget } from "../types";

/**
 * Compact session-budget readout for the page header. Hidden until the first
 * turn completes (when the engine emits its first BudgetSnapshotEvent).
 */
export function BudgetBar({ budget }: { budget: SessionBudget | null }) {
  if (!budget || budget.mode === "off") return null;

  const tokenPct =
    budget.tokenLimit > 0 ? Math.min(100, (budget.tokensUsed / budget.tokenLimit) * 100) : 0;
  const costPct =
    budget.costLimit > 0 ? Math.min(100, Number(budget.costUsd) / Number(budget.costLimit) * 100) : 0;
  const pct = Math.max(tokenPct, costPct);

  const state: "ok" | "warn" | "exceeded" = budget.isExceeded
    ? "exceeded"
    : budget.isWarning
      ? "warn"
      : "ok";

  const fillColor =
    state === "exceeded"
      ? "bg-rose-500"
      : state === "warn"
        ? "bg-amber-400"
        : "bg-emerald-400";

  const labelColor =
    state === "exceeded"
      ? "text-rose-300"
      : state === "warn"
        ? "text-amber-300"
        : "text-slate-300";

  const cost = Number(budget.costUsd);
  const limit = Number(budget.costLimit);

  return (
    <div className="inline-flex items-center gap-2 text-xs">
      <span className="uppercase tracking-wider font-mono text-slate-500">budget</span>
      <div className="relative h-2 w-32 rounded-full bg-slate-800 overflow-hidden">
        <div
          className={clsx("absolute inset-y-0 left-0 transition-all", fillColor)}
          style={{ width: `${pct}%` }}
        />
      </div>
      <span className={clsx("font-mono", labelColor)}>
        ${cost.toFixed(4)} <span className="text-slate-600">/</span> ${limit.toFixed(2)}
      </span>
      <span className="text-slate-500 font-mono">
        ({budget.tokensUsed.toLocaleString()} tok)
      </span>
      {budget.mode === "block" && state === "exceeded" && (
        <span className="rounded-full bg-rose-500/20 ring-1 ring-rose-400/40 px-2 py-0.5 text-rose-200 font-semibold uppercase tracking-wider text-[10px]">
          blocked
        </span>
      )}
      {budget.mode !== "block" && state === "warn" && (
        <span className="rounded-full bg-amber-400/20 ring-1 ring-amber-300/40 px-2 py-0.5 text-amber-200 font-semibold uppercase tracking-wider text-[10px]">
          warn
        </span>
      )}
    </div>
  );
}
