import clsx from "clsx";
import type { Mode } from "../types";

export function ModeToggle({
  mode,
  onChange,
  disabled,
}: {
  mode: Mode;
  onChange: (m: Mode) => void;
  disabled?: boolean;
}) {
  return (
    <div className="inline-flex items-center gap-2 text-xs text-slate-400">
      <span className="uppercase tracking-wider font-mono">Mode</span>
      <div
        className={clsx(
          "inline-flex rounded-full border border-slate-700 bg-slate-900/60 p-0.5",
          disabled && "opacity-60",
        )}
      >
        <Pill
          label="Handoff"
          active={mode === "handoff"}
          activeColor="bg-indigo-500/20 text-indigo-200 ring-1 ring-indigo-400/40"
          onClick={() => !disabled && onChange("handoff")}
          title="Mesh of specialists; one agent at a time. Low cost, low latency."
        />
        <Pill
          label="Magentic"
          active={mode === "magentic"}
          activeColor="bg-fuchsia-500/20 text-fuchsia-200 ring-1 ring-fuchsia-400/40"
          onClick={() => !disabled && onChange("magentic")}
          title="Manager plans sub-tasks, dispatches to specialists, synthesises final answer. Higher cost, richer behaviour."
        />
      </div>
    </div>
  );
}

function Pill({
  label,
  active,
  activeColor,
  onClick,
  title,
}: {
  label: string;
  active: boolean;
  activeColor: string;
  onClick: () => void;
  title: string;
}) {
  return (
    <button
      type="button"
      onClick={onClick}
      title={title}
      className={clsx(
        "px-3 py-1 rounded-full text-xs font-semibold transition-colors",
        active ? activeColor : "text-slate-400 hover:text-slate-200",
      )}
    >
      {label}
    </button>
  );
}
