import { useCallback, useEffect, useRef, useState } from "react";
import type { AgentEvent, AgentInfo, ChatAttachment, ChatTurn, Mode, PendingApproval, SessionBudget } from "../types";

const API_BASE = import.meta.env.VITE_API_BASE_URL ?? "";

const newId = () => Math.random().toString(36).slice(2, 10);

/**
 * Orchestrates the SSE stream from POST /api/chat/stream.
 *
 * Returns:
 *   turns                – the running chat history (with agent events per turn)
 *   agents               – list of registered agents (loaded once from /api/agents)
 *   sessionId            – stable session id used for history continuity
 *   sendMessage(text)    – submits a new user message and streams the response
 *   reset()              – clears the local turns and the server-side history
 *   isStreaming          – true while a turn is in flight
 */
export function useAgentStream() {
  const [agents, setAgents] = useState<AgentInfo[]>([]);
  const [turns, setTurns] = useState<ChatTurn[]>([]);
  const [isStreaming, setIsStreaming] = useState(false);
  const [pendingApprovals, setPendingApprovals] = useState<PendingApproval[]>([]);
  const [mode, setMode] = useState<Mode>("handoff");
  const [budget, setBudget] = useState<SessionBudget | null>(null);
  const sessionIdRef = useRef<string>(`s-${newId()}`);

  // ---- load agent registry on mount -----------------------------------------
  useEffect(() => {
    let cancelled = false;
    fetch(`${API_BASE}/api/agents`)
      .then((r) => (r.ok ? r.json() : Promise.reject(r.statusText)))
      .then((data: { agents: AgentInfo[] }) => {
        if (!cancelled) setAgents(data.agents);
      })
      .catch(() => { /* surfaced on first send */ });
    return () => {
      cancelled = true;
    };
  }, []);

  const updateLastTurn = useCallback(
    (mut: (turn: ChatTurn) => ChatTurn) => {
      setTurns((prev) => {
        if (prev.length === 0) return prev;
        const next = prev.slice();
        next[next.length - 1] = mut(next[next.length - 1]);
        return next;
      });
    },
    [],
  );

  const applyEvent = useCallback(
    (evt: AgentEvent) => {
      // Approval lifecycle is global to the session — track outside the per-turn state.
      if (evt.type === "approval_requested") {
        setPendingApprovals((prev) => [
          ...prev,
          {
            approvalId: evt.approvalId,
            toolName: evt.toolName,
            argumentsJson: evt.argumentsJson,
            requestedAt: evt.timestamp,
          },
        ]);
      } else if (evt.type === "approval_decided") {
        setPendingApprovals((prev) => prev.filter((a) => a.approvalId !== evt.approvalId));
      } else if (evt.type === "budget_snapshot") {
        setBudget({
          tokensUsed: evt.tokensUsed,
          tokenLimit: evt.tokenLimit,
          costUsd: evt.costUsd,
          costLimit: evt.costLimit,
          mode: evt.mode,
          isWarning: evt.isWarning,
          isExceeded: evt.isExceeded,
        });
      }

      updateLastTurn((turn) => {
        const events = [...turn.events, evt];
        let responses = turn.responses;

        switch (evt.type) {
          case "agent_switched": {
            // Start a new response slot for this agent if we don't already have an open one.
            const lastOpen = responses[responses.length - 1];
            if (!lastOpen || lastOpen.completed || lastOpen.agentId !== evt.agentId) {
              responses = [...responses, { agentId: evt.agentId, text: "", completed: false }];
            }
            break;
          }
          case "token": {
            const last = responses[responses.length - 1];
            if (last && !last.completed && last.agentId === evt.agentId) {
              responses = responses.slice(0, -1).concat({
                ...last,
                text: last.text + evt.token,
              });
            } else {
              // No prior switch event - start an implicit slot.
              responses = [
                ...responses,
                { agentId: evt.agentId, text: evt.token, completed: false },
              ];
            }
            break;
          }
          case "message_completed": {
            const last = responses[responses.length - 1];
            if (last && last.agentId === evt.agentId) {
              responses = responses.slice(0, -1).concat({
                ...last,
                text: evt.text || last.text,
                completed: true,
              });
            }
            break;
          }
          case "turn_completed": {
            // Finalize any open slot.
            if (responses.length > 0 && !responses[responses.length - 1].completed) {
              responses = responses.slice(0, -1).concat({
                ...responses[responses.length - 1],
                completed: true,
              });
            }
            break;
          }
        }

        const updatedTurn = { ...turn, events, responses } as typeof turn;
        if (evt.type === "turn_metrics") {
          updatedTurn.metrics = {
            inputTokens: evt.inputTokens,
            outputTokens: evt.outputTokens,
            modelCalls: evt.modelCalls,
            elapsedMs: evt.elapsedMs,
            estimatedCostUsd: evt.estimatedCostUsd,
          };
        }
        return updatedTurn;
      });
    },
    [updateLastTurn],
  );

  // ---- SSE parser ------------------------------------------------------------
  const consumeStream = useCallback(
    async (reader: ReadableStreamDefaultReader<Uint8Array>) => {
      const decoder = new TextDecoder();
      let buffer = "";
      while (true) {
        const { value, done } = await reader.read();
        if (done) break;
        buffer += decoder.decode(value, { stream: true });

        // SSE messages are separated by \n\n
        let idx;
        while ((idx = buffer.indexOf("\n\n")) >= 0) {
          const raw = buffer.slice(0, idx);
          buffer = buffer.slice(idx + 2);

          let evtName = "message";
          let dataStr = "";
          for (const line of raw.split("\n")) {
            if (line.startsWith("event:")) evtName = line.slice(6).trim();
            else if (line.startsWith("data:")) dataStr += line.slice(5).trim();
          }
          if (!dataStr) continue;

          try {
            const data = JSON.parse(dataStr);
            if (evtName === "agent") applyEvent(data as AgentEvent);
            else if (evtName === "error") {
              applyEvent({
                type: "error",
                agentId: "system",
                timestamp: new Date().toISOString(),
                message: String(data.message ?? data),
              });
            }
          } catch {
            // ignore parse errors
          }
        }
      }
    },
    [applyEvent],
  );

  const sendMessage = useCallback(
    async (text: string, attachments?: ChatAttachment[]) => {
      const userText = text.trim();
      const atts = (attachments ?? []).filter((a) => a.base64);
      if ((!userText && atts.length === 0) || isStreaming) return;

      setIsStreaming(true);
      setTurns((prev) => [
        ...prev,
        {
          id: newId(),
          user: userText,
          attachments: atts.length > 0
            ? atts.map((a) => ({ filename: a.filename, contentType: a.contentType, size: a.size }))
            : undefined,
          responses: [],
          events: [],
          status: "running",
        },
      ]);

      try {
        const resp = await fetch(`${API_BASE}/api/chat/stream`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({
            message: userText,
            sessionId: sessionIdRef.current,
            mode,
            attachments: atts.length > 0 ? atts : undefined,
          }),
        });

        if (!resp.ok || !resp.body) {
          updateLastTurn((t) => ({
            ...t,
            status: "error",
            events: [
              ...t.events,
              {
                type: "error",
                agentId: "system",
                timestamp: new Date().toISOString(),
                message: `HTTP ${resp.status} ${resp.statusText}`,
              },
            ],
          }));
        } else {
          const reader = resp.body.getReader();
          await consumeStream(reader);
          updateLastTurn((t) => ({ ...t, status: "done" }));
        }
      } catch (err) {
        updateLastTurn((t) => ({
          ...t,
          status: "error",
          events: [
            ...t.events,
            {
              type: "error",
              agentId: "system",
              timestamp: new Date().toISOString(),
              message: err instanceof Error ? err.message : String(err),
            },
          ],
        }));
      } finally {
        setIsStreaming(false);
      }
    },
    [consumeStream, isStreaming, mode, updateLastTurn],
  );

  const reset = useCallback(async () => {
    await fetch(`${API_BASE}/api/chat/reset/${sessionIdRef.current}`, { method: "POST" }).catch(
      () => {},
    );
    sessionIdRef.current = `s-${newId()}`;
    setTurns([]);
    setPendingApprovals([]);
    setBudget(null);
  }, []);

  const decideApproval = useCallback(
    async (approvalId: string, approved: boolean) => {
      // Optimistically remove the card so the UI stops looking pending; the server
      // will publish an approval_decided event that confirms it.
      setPendingApprovals((prev) => prev.filter((a) => a.approvalId !== approvalId));

      await fetch(`${API_BASE}/api/chat/approve`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          sessionId: sessionIdRef.current,
          approvalId,
          approved,
        }),
      }).catch(() => {});
    },
    [],
  );

  return {
    agents,
    turns,
    sessionId: sessionIdRef.current,
    sendMessage,
    reset,
    isStreaming,
    pendingApprovals,
    decideApproval,
    mode,
    setMode,
    budget,
  };
}
