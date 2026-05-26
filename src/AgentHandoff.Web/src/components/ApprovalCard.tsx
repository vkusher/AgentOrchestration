import type { PendingApproval } from "../types";

export function ApprovalCard({
  approval,
  onDecide,
}: {
  approval: PendingApproval;
  onDecide: (approvalId: string, approved: boolean) => void;
}) {
  let prettyArgs = approval.argumentsJson;
  try {
    prettyArgs = JSON.stringify(JSON.parse(approval.argumentsJson), null, 2);
  } catch {
    /* leave as-is */
  }

  return (
    <div className="rounded-xl border border-amber-500/40 bg-amber-500/10 p-4 shadow-lg">
      <div className="flex items-start justify-between gap-3">
        <div>
          <div className="flex items-center gap-2">
            <span className="inline-flex h-2 w-2 rounded-full bg-amber-300 animate-pulseDot" />
            <span className="text-xs uppercase tracking-wider font-semibold text-amber-200">
              Supervisor approval required
            </span>
          </div>
          <p className="mt-1 text-sm text-slate-100">
            The Billing agent wants to call <code className="text-amber-200">{approval.toolName}</code>.
          </p>
        </div>
        <span className="text-[11px] text-slate-500">
          {new Date(approval.requestedAt).toLocaleTimeString()}
        </span>
      </div>

      <pre className="mt-3 max-h-40 overflow-auto rounded-md bg-slate-950/60 border border-slate-800 px-3 py-2 text-xs text-slate-200">
        {prettyArgs}
      </pre>

      <div className="mt-3 flex gap-2 justify-end">
        <button
          onClick={() => onDecide(approval.approvalId, false)}
          className="rounded-md border border-red-500/40 bg-red-500/10 px-3 py-1.5 text-xs font-medium text-red-200 hover:bg-red-500/20 transition-colors"
        >
          Deny
        </button>
        <button
          onClick={() => onDecide(approval.approvalId, true)}
          className="rounded-md bg-emerald-500 hover:bg-emerald-400 px-3 py-1.5 text-xs font-semibold text-white transition-colors"
        >
          Approve
        </button>
      </div>
    </div>
  );
}
