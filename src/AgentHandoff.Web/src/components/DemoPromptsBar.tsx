import type { DemoPrompt } from "../hooks/demoPrompts";

export function DemoPromptsBar({
  prompts,
  onRun,
  disabled,
}: {
  prompts: DemoPrompt[];
  onRun: (text: string) => void;
  disabled: boolean;
}) {
  if (prompts.length === 0) return null;

  return (
    <div className="border-b border-slate-800 bg-slate-950/40 px-4 py-2 flex items-center gap-2">
      <span className="shrink-0 text-[11px] font-semibold uppercase tracking-wider text-slate-400">
        Demo prompts
      </span>
      <div className="flex-1 min-w-0 overflow-x-auto demo-prompts-scroll">
        <div className="flex gap-1.5 flex-nowrap w-max pb-1">
          {prompts.map((p, idx) => (
            <button
              key={`${p.label}-${idx}`}
              onClick={() => onRun(p.prompt)}
              disabled={disabled}
              title={p.prompt}
              className="shrink-0 rounded-md border border-slate-700 bg-slate-800/40 px-2.5 py-1 text-[11px] text-slate-200 hover:border-indigo-400/60 hover:bg-slate-800 disabled:opacity-50 disabled:cursor-not-allowed transition-colors whitespace-nowrap"
            >
              {p.label}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
