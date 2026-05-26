import { useEffect, useState } from "react";
import { useAgentStream } from "./hooks/useAgentStream";
import { ChatPanel } from "./components/ChatPanel";
import { ActivityPanel } from "./components/ActivityPanel";
import { ModeToggle } from "./components/ModeToggle";
import { BudgetBar } from "./components/BudgetBar";
import { DemoPromptsBar } from "./components/DemoPromptsBar";
import { loadDemoPrompts, type DemoPrompt } from "./hooks/demoPrompts";
import type { SessionBudget } from "./types";

function App() {
  const {
    agents,
    turns,
    sendMessage,
    reset,
    isStreaming,
    pendingApprovals,
    decideApproval,
    mode,
    setMode,
    budget,
  } = useAgentStream();

  const [demoPrompts, setDemoPrompts] = useState<DemoPrompt[]>([]);
  useEffect(() => {
    let cancelled = false;
    loadDemoPrompts().then((p) => {
      if (!cancelled) setDemoPrompts(p);
    });
    return () => {
      cancelled = true;
    };
  }, []);

  // Mode-based agent highlight: handoff → entry router (triage), magentic → manager.
  // We pick the agent by id if present, falling back to a sensible role match.
  const modeHighlightAgentId =
    mode === "magentic"
      ? agents.find((a) => a.id === "manager")?.id ?? agents.find((a) => (a.role as string) === "planner")?.id
      : agents.find((a) => a.id === "triage")?.id ?? agents.find((a) => a.role === "router")?.id;

  return (
    <div className="flex h-full flex-col">
      <Header mode={mode} onModeChange={setMode} disabled={isStreaming} budget={budget} />
      <DemoPromptsBar
        prompts={demoPrompts}
        onRun={(text) => sendMessage(text)}
        disabled={isStreaming}
      />
      <main className="flex-1 min-h-0 px-4 py-4 lg:px-8 lg:py-6">
        <div className="grid h-full grid-cols-1 lg:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)] gap-4">
          <ChatPanel
            turns={turns}
            onSend={sendMessage}
            onReset={reset}
            isStreaming={isStreaming}
            pendingApprovals={pendingApprovals}
            onDecideApproval={decideApproval}
          />
          <ActivityPanel
            agents={agents}
            turns={turns}
            isStreaming={isStreaming}
            modeHighlightAgentId={modeHighlightAgentId}
            mode={mode}
          />
        </div>
      </main>
      <Footer />
    </div>
  );
}

function Header({
  mode,
  onModeChange,
  disabled,
  budget,
}: {
  mode: "handoff" | "magentic";
  onModeChange: (m: "handoff" | "magentic") => void;
  disabled: boolean;
  budget: SessionBudget | null;
}) {
  return (
    <header className="px-4 lg:px-8 py-4 border-b border-slate-800 bg-slate-950/80 backdrop-blur">
      <div className="flex items-center justify-between gap-3 flex-wrap">
        <div className="flex items-baseline gap-3">
          <h1 className="text-xl font-bold text-slate-100">Bank Customer Support Hub</h1>
          <span className="text-xs text-slate-500">
            Microsoft Agent Framework · MCP · A2A · guardrails · budget
          </span>
        </div>
        <div className="flex items-center gap-5">
          <BudgetBar budget={budget} />
          <ModeToggle mode={mode} onChange={onModeChange} disabled={disabled} />
        </div>
      </div>
    </header>
  );
}

function Footer() {
  return (
    <footer className="px-4 lg:px-8 py-2 border-t border-slate-800 text-[11px] text-slate-500 flex items-center justify-between">
      <span>UI: Vite + React + Tailwind · Engine: .NET 8 + Microsoft.Agents.AI</span>
      <span>
        Reference:{" "}
        <a
          className="text-slate-400 hover:text-slate-200 underline-offset-2 hover:underline"
          href="https://learn.microsoft.com/en-us/agent-framework/workflows/orchestrations/handoff?pivots=programming-language-csharp"
          target="_blank"
          rel="noreferrer"
        >
          Handoff orchestration docs
        </a>
      </span>
    </footer>
  );
}

export default App;
