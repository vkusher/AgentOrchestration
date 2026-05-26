import { useEffect, useRef, useState } from "react";
import clsx from "clsx";
import type { ChatAttachment, ChatTurn, PendingApproval } from "../types";
import { AgentBadge } from "./AgentBadge";
import { ApprovalCard } from "./ApprovalCard";

const MAX_FILE_BYTES = 10 * 1024 * 1024; // 10 MB per file
const ACCEPT_FILES = ".pdf,.png,.jpg,.jpeg,.gif,.bmp,.tiff,.tif,.txt,.md,.markdown,.csv,.tsv,.json,.xml,.yaml,.yml,.log";

async function fileToAttachment(file: File): Promise<ChatAttachment> {
  const buf = await file.arrayBuffer();
  let binary = "";
  const bytes = new Uint8Array(buf);
  const chunk = 0x8000;
  for (let i = 0; i < bytes.length; i += chunk) {
    binary += String.fromCharCode.apply(null, Array.from(bytes.subarray(i, i + chunk)));
  }
  return {
    filename: file.name,
    contentType: file.type || "application/octet-stream",
    base64: btoa(binary),
    size: file.size,
  };
}

function humanSize(n: number): string {
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  return `${(n / 1024 / 1024).toFixed(1)} MB`;
}

export function ChatPanel({
  turns,
  onSend,
  onReset,
  isStreaming,
  pendingApprovals,
  onDecideApproval,
}: {
  turns: ChatTurn[];
  onSend: (text: string, attachments?: ChatAttachment[]) => void;
  onReset: () => void;
  isStreaming: boolean;
  pendingApprovals: PendingApproval[];
  onDecideApproval: (approvalId: string, approved: boolean) => void;
}) {
  const [text, setText] = useState("");
  const [attachments, setAttachments] = useState<ChatAttachment[]>([]);
  const [attachError, setAttachError] = useState<string | null>(null);
  const [isDragOver, setIsDragOver] = useState(false);
  const fileInputRef = useRef<HTMLInputElement | null>(null);
  const scrollRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: "smooth" });
  }, [turns]);

  const addFiles = async (files: FileList | File[]) => {
    setAttachError(null);
    const list = Array.from(files);
    const accepted: ChatAttachment[] = [];
    for (const f of list) {
      if (f.size > MAX_FILE_BYTES) {
        setAttachError(`'${f.name}' is larger than 10 MB and was skipped.`);
        continue;
      }
      try {
        accepted.push(await fileToAttachment(f));
      } catch {
        setAttachError(`Failed to read '${f.name}'.`);
      }
    }
    if (accepted.length > 0) {
      setAttachments((prev) => [...prev, ...accepted]);
    }
  };

  const removeAttachment = (idx: number) =>
    setAttachments((prev) => prev.filter((_, i) => i !== idx));

  const submit = () => {
    const trimmed = text.trim();
    if ((!trimmed && attachments.length === 0) || isStreaming) return;
    onSend(trimmed, attachments.length > 0 ? attachments : undefined);
    setText("");
    setAttachments([]);
    setAttachError(null);
  };

  const onDrop = (e: React.DragEvent<HTMLDivElement>) => {
    e.preventDefault();
    setIsDragOver(false);
    if (e.dataTransfer.files && e.dataTransfer.files.length > 0) {
      void addFiles(e.dataTransfer.files);
    }
  };

  return (
    <section
      className={clsx(
        "flex h-full flex-col bg-slate-900/60 backdrop-blur rounded-xl border transition-colors",
        isDragOver ? "border-indigo-400 ring-2 ring-indigo-500/40" : "border-slate-800",
      )}
      onDragOver={(e) => {
        if (e.dataTransfer.types.includes("Files")) {
          e.preventDefault();
          setIsDragOver(true);
        }
      }}
      onDragLeave={() => setIsDragOver(false)}
      onDrop={onDrop}
    >
      <header className="flex items-center justify-between px-4 py-3 border-b border-slate-800">
        <h2 className="text-sm font-semibold text-slate-200">Conversation</h2>
        <button
          onClick={onReset}
          className="text-xs text-slate-400 hover:text-slate-200 underline-offset-2 hover:underline"
        >
          New session
        </button>
      </header>

      <div ref={scrollRef} className="flex-1 overflow-y-auto px-4 py-4 space-y-4">
        {turns.map((turn) => (
          <TurnView key={turn.id} turn={turn} />
        ))}

        {pendingApprovals.length > 0 && (
          <div className="space-y-3">
            {pendingApprovals.map((a) => (
              <ApprovalCard key={a.approvalId} approval={a} onDecide={onDecideApproval} />
            ))}
          </div>
        )}
      </div>

      <footer className="px-4 py-3 border-t border-slate-800 space-y-2">
        {attachments.length > 0 && (
          <div className="flex flex-wrap gap-1.5">
            {attachments.map((a, i) => (
              <span
                key={`${a.filename}-${i}`}
                className="inline-flex items-center gap-1.5 rounded-md border border-slate-700 bg-slate-800/60 pl-2 pr-1 py-1 text-[11px] text-slate-200"
                title={`${a.contentType} · ${humanSize(a.size)}`}
              >
                <PaperclipIcon className="h-3 w-3 text-slate-400" />
                <span className="font-mono max-w-[18rem] truncate">{a.filename}</span>
                <span className="text-slate-500">{humanSize(a.size)}</span>
                <button
                  onClick={() => removeAttachment(i)}
                  className="ml-0.5 rounded-sm px-1 text-slate-400 hover:text-rose-300 hover:bg-rose-500/10"
                  aria-label={`Remove ${a.filename}`}
                  disabled={isStreaming}
                >
                  ×
                </button>
              </span>
            ))}
          </div>
        )}

        {attachError && (
          <div className="text-[11px] text-rose-300">{attachError}</div>
        )}

        <div className="flex items-end gap-2">
          <input
            ref={fileInputRef}
            type="file"
            multiple
            accept={ACCEPT_FILES}
            className="hidden"
            onChange={(e) => {
              if (e.target.files && e.target.files.length > 0) {
                void addFiles(e.target.files);
                e.target.value = "";
              }
            }}
          />
          <button
            type="button"
            onClick={() => fileInputRef.current?.click()}
            disabled={isStreaming}
            className={clsx(
              "rounded-lg border border-slate-700 bg-slate-800/60 p-2 text-slate-300 transition-colors",
              isStreaming
                ? "opacity-50 cursor-not-allowed"
                : "hover:bg-slate-800 hover:border-slate-600",
            )}
            title="Attach a file (PDF, image, or text)"
            aria-label="Attach a file"
          >
            <PaperclipIcon className="h-4 w-4" />
          </button>
          <textarea
            value={text}
            onChange={(e) => setText(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter" && !e.shiftKey) {
                e.preventDefault();
                submit();
              }
            }}
            rows={2}
            placeholder="Describe your issue, attach a transfer form (PDF/image), or paste an account question…"
            className="flex-1 resize-none rounded-lg bg-slate-800/60 border border-slate-700 focus:border-indigo-500 focus:ring-1 focus:ring-indigo-500 outline-none px-3 py-2 text-sm text-slate-100 placeholder:text-slate-500"
            disabled={isStreaming}
          />
          <button
            onClick={submit}
            disabled={isStreaming || (!text.trim() && attachments.length === 0)}
            className={clsx(
              "rounded-lg px-4 py-2 text-sm font-semibold transition-colors",
              isStreaming || (!text.trim() && attachments.length === 0)
                ? "bg-slate-700 text-slate-400 cursor-not-allowed"
                : "bg-indigo-500 hover:bg-indigo-400 text-white",
            )}
          >
            {isStreaming ? "…" : "Send"}
          </button>
        </div>
        <p className="mt-1 text-[11px] text-slate-500">
          Enter to send · Shift+Enter for newline · Drag &amp; drop or click the paperclip to attach (max 10 MB each)
        </p>
      </footer>
    </section>
  );
}

function PaperclipIcon({ className }: { className?: string }) {
  return (
    <svg
      xmlns="http://www.w3.org/2000/svg"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      className={className}
      aria-hidden="true"
    >
      <path d="m21.44 11.05-9.19 9.19a6 6 0 0 1-8.49-8.49l9.19-9.19a4 4 0 0 1 5.66 5.66L9.42 17.41a2 2 0 1 1-2.83-2.83l8.49-8.49" />
    </svg>
  );
}

function TurnView({ turn }: { turn: ChatTurn }) {
  const escalation = turn.events.find((e) => e.type === "escalated");

  return (
    <div className="space-y-3">
      <div className="flex justify-end">
        <div className="max-w-[80%] rounded-2xl rounded-br-sm bg-indigo-500/20 border border-indigo-500/40 px-3 py-2 text-sm text-slate-100">
          {turn.user && <div className="whitespace-pre-wrap">{turn.user}</div>}
          {turn.attachments && turn.attachments.length > 0 && (
            <div className={clsx("flex flex-wrap gap-1.5", turn.user ? "mt-2" : "")}>
              {turn.attachments.map((a, i) => (
                <span
                  key={`${a.filename}-${i}`}
                  className="inline-flex items-center gap-1.5 rounded-md bg-indigo-500/20 border border-indigo-400/40 px-2 py-0.5 text-[11px] text-indigo-100"
                  title={`${a.contentType} · ${humanSize(a.size)}`}
                >
                  <PaperclipIcon className="h-3 w-3" />
                  <span className="font-mono max-w-[14rem] truncate">{a.filename}</span>
                  <span className="text-indigo-300/80">{humanSize(a.size)}</span>
                </span>
              ))}
            </div>
          )}
        </div>
      </div>

      {escalation && escalation.type === "escalated" && (
        <div className="rounded-lg border border-rose-500/40 bg-rose-500/10 px-3 py-2 text-xs text-rose-200">
          <div className="flex items-center gap-2">
            <span className="inline-flex h-2 w-2 rounded-full bg-rose-400 animate-pulseDot" />
            <span className="font-semibold uppercase tracking-wider">
              Escalated to human supervisor
            </span>
            <span className="ml-auto font-mono text-rose-300">{escalation.caseId}</span>
          </div>
          <div className="mt-1 text-rose-200/80 italic line-clamp-2">{escalation.reason}</div>
        </div>
      )}

      {turn.responses.map((r, i) => (
        <div key={i} className="flex">
          <div className="max-w-[85%] rounded-2xl rounded-bl-sm bg-slate-800/70 border border-slate-700 px-3 py-2 text-sm text-slate-100 whitespace-pre-wrap">
            <div className="mb-1.5">
              <AgentBadge agentId={r.agentId} active={!r.completed} compact />
            </div>
            {r.text}
            {!r.completed && (
              <span className="inline-block ml-1 text-slate-500 animate-pulseDot">▌</span>
            )}
          </div>
        </div>
      ))}

      {turn.metrics && <MetricsBadge metrics={turn.metrics} />}

      {turn.status === "error" && (
        <div className="flex">
          <div className="max-w-[85%] rounded-2xl bg-red-900/40 border border-red-700 px-3 py-2 text-sm text-red-200">
            Something went wrong. See the activity panel for details.
          </div>
        </div>
      )}
    </div>
  );
}

function MetricsBadge({ metrics }: { metrics: NonNullable<ChatTurn["metrics"]> }) {
  const seconds = (metrics.elapsedMs / 1000).toFixed(2);
  const total = metrics.inputTokens + metrics.outputTokens;
  const cost =
    metrics.estimatedCostUsd >= 0.01
      ? `$${metrics.estimatedCostUsd.toFixed(4)}`
      : `$${metrics.estimatedCostUsd.toFixed(6)}`;

  return (
    <div className="flex justify-end">
      <div
        className="inline-flex items-center gap-3 rounded-md border border-slate-700/60 bg-slate-900/40 px-2.5 py-1 text-[11px] text-slate-400 font-mono"
        title={`${metrics.modelCalls} model call${metrics.modelCalls === 1 ? "" : "s"} · ${metrics.inputTokens.toLocaleString()} input tokens · ${metrics.outputTokens.toLocaleString()} output tokens · estimated cost`}
      >
        <span>
          <span className="text-slate-500">latency&nbsp;</span>
          <span className="text-slate-200">{seconds}s</span>
        </span>
        <span className="text-slate-700">•</span>
        <span>
          <span className="text-slate-500">tokens&nbsp;</span>
          <span className="text-slate-200">{total.toLocaleString()}</span>
          <span className="text-slate-500">
            &nbsp;({metrics.inputTokens.toLocaleString()} in / {metrics.outputTokens.toLocaleString()} out)
          </span>
        </span>
        <span className="text-slate-700">•</span>
        <span>
          <span className="text-slate-500">cost&nbsp;</span>
          <span className="text-emerald-300">{cost}</span>
        </span>
      </div>
    </div>
  );
}
