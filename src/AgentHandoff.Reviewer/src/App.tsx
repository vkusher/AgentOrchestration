import { useCallback, useEffect, useState } from "react";

type Approval = {
  approvalId: string;
  sessionId: string;
  agentId: string;
  toolName: string;
  arguments: Record<string, unknown>;
  createdAt: string;
  expiresAt: string;
  status: string;
};

type Toast = { kind: "ok" | "error"; text: string } | null;

const POLL_MS = 2000;

export function App() {
  const [items, setItems] = useState<Approval[]>([]);
  const [autoRefresh, setAutoRefresh] = useState(true);
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [reasons, setReasons] = useState<Record<string, string>>({});
  const [toast, setToast] = useState<Toast>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      const r = await fetch("/api/approvals?status=Pending");
      if (!r.ok) throw new Error(`${r.status} ${r.statusText}`);
      setItems(await r.json());
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }, []);

  useEffect(() => {
    load();
    if (!autoRefresh) return;
    const id = setInterval(load, POLL_MS);
    return () => clearInterval(id);
  }, [load, autoRefresh]);

  const decide = async (id: string, approved: boolean) => {
    setBusy((b) => ({ ...b, [id]: true }));
    try {
      const r = await fetch(`/api/approvals/${id}/decision`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          approved,
          decidedBy: "reviewer-ui",
          reason: reasons[id] || (approved ? "Approved by reviewer." : "Denied by reviewer."),
        }),
      });
      if (!r.ok) throw new Error(`${r.status} ${r.statusText}`);
      setToast({ kind: "ok", text: `${approved ? "Approved" : "Denied"} ${id.slice(0, 8)}…` });
      setItems((xs) => xs.filter((x) => x.approvalId !== id));
    } catch (e) {
      setToast({ kind: "error", text: e instanceof Error ? e.message : String(e) });
    } finally {
      setBusy((b) => ({ ...b, [id]: false }));
      setTimeout(() => setToast(null), 2500);
    }
  };

  return (
    <div className="app">
      <div className="header">
        <h1>Approvals queue</h1>
        <div className="muted">{items.length} pending {error && <span style={{ color: "#fca5a5" }}>· {error}</span>}</div>
      </div>

      <div className="controls">
        <label>
          <input type="checkbox" checked={autoRefresh} onChange={(e) => setAutoRefresh(e.target.checked)} />{" "}
          Auto-refresh ({POLL_MS / 1000}s)
        </label>
        <button className="btn" style={{ background: "#334155" }} onClick={load}>Refresh now</button>
      </div>

      {items.length === 0 ? (
        <div className="empty">No pending approvals.</div>
      ) : (
        items.map((a) => (
          <div className="card" key={a.approvalId}>
            <div className="card-head">
              <div>
                <div className="tool">{a.toolName}</div>
                <div className="session">session {a.sessionId} · agent {a.agentId}</div>
              </div>
              <div className="session" title={a.approvalId}>{a.approvalId.slice(0, 12)}…</div>
            </div>

            <pre className="args">{JSON.stringify(a.arguments, null, 2)}</pre>

            <div className="meta">
              <span>created {new Date(a.createdAt).toLocaleTimeString()}</span>
              <span>expires {new Date(a.expiresAt).toLocaleString()}</span>
            </div>

            <div className="actions">
              <input
                placeholder="Reason (optional)"
                value={reasons[a.approvalId] || ""}
                onChange={(e) => setReasons((r) => ({ ...r, [a.approvalId]: e.target.value }))}
              />
              <button
                className="btn btn-approve"
                disabled={busy[a.approvalId]}
                onClick={() => decide(a.approvalId, true)}
              >
                Approve
              </button>
              <button
                className="btn btn-deny"
                disabled={busy[a.approvalId]}
                onClick={() => decide(a.approvalId, false)}
              >
                Deny
              </button>
            </div>
          </div>
        ))
      )}

      {toast && <div className={`toast ${toast.kind === "error" ? "error" : ""}`}>{toast.text}</div>}
    </div>
  );
}
