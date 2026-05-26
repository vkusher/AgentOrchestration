import clsx from "clsx";

const COLORS: Record<string, { bg: string; ring: string; dot: string; label: string }> = {
  triage:         { bg: "bg-indigo-500/15",  ring: "ring-indigo-400/40",  dot: "bg-indigo-400",  label: "text-indigo-200"  },
  tech_support:   { bg: "bg-sky-500/15",     ring: "ring-sky-400/40",     dot: "bg-sky-400",     label: "text-sky-200"     },
  order_shipping: { bg: "bg-emerald-500/15", ring: "ring-emerald-400/40", dot: "bg-emerald-400", label: "text-emerald-200" },
  billing:        { bg: "bg-amber-500/15",   ring: "ring-amber-400/40",   dot: "bg-amber-400",   label: "text-amber-200"   },
  human_queue:    { bg: "bg-rose-500/20",    ring: "ring-rose-400/50",    dot: "bg-rose-400",    label: "text-rose-200"    },
  manager:        { bg: "bg-fuchsia-500/15", ring: "ring-fuchsia-400/40", dot: "bg-fuchsia-400", label: "text-fuchsia-200" },
};

function paletteFor(id: string) {
  return COLORS[id] ?? {
    bg: "bg-slate-500/15",
    ring: "ring-slate-400/40",
    dot: "bg-slate-400",
    label: "text-slate-200",
  };
}

export function AgentBadge({
  agentId,
  displayName,
  active = false,
  compact = false,
}: {
  agentId: string;
  displayName?: string;
  active?: boolean;
  compact?: boolean;
}) {
  const p = paletteFor(agentId);
  return (
    <span
      className={clsx(
        "inline-flex items-center gap-2 rounded-full px-2.5 py-1 text-xs font-medium ring-1",
        p.bg,
        p.ring,
        p.label,
        active && "shadow-[0_0_0_4px_rgba(99,102,241,0.15)]",
        compact && "px-2 py-0.5 text-[11px]",
      )}
    >
      <span
        className={clsx(
          "h-2 w-2 rounded-full",
          p.dot,
          active && "animate-pulseDot",
        )}
      />
      <span className="font-mono uppercase tracking-wide">{displayName ?? agentId}</span>
    </span>
  );
}
