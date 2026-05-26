export interface DemoPrompt {
  label: string;
  prompt: string;
}

/**
 * Minimal parser for the small `demo-prompts.yaml` schema used by the UI:
 *
 * prompts:
 *   - label: "..."
 *     prompt: "..."
 *
 * Implemented inline to avoid pulling in a yaml dependency for a few buttons.
 * Supports double-quoted, single-quoted, and bare string values for both keys.
 */
export function parseDemoPrompts(yamlText: string): DemoPrompt[] {
  const lines = yamlText.split(/\r?\n/);
  const out: DemoPrompt[] = [];
  let cur: Partial<DemoPrompt> | null = null;

  const stripValue = (raw: string): string => {
    let v = raw.trim();
    if ((v.startsWith('"') && v.endsWith('"')) || (v.startsWith("'") && v.endsWith("'"))) {
      v = v.slice(1, -1);
    }
    return v
      .replace(/\\n/g, "\n")
      .replace(/\\"/g, '"')
      .replace(/\\'/g, "'");
  };

  const commit = () => {
    if (cur && cur.label && cur.prompt) {
      out.push({ label: cur.label, prompt: cur.prompt });
    }
    cur = null;
  };

  for (const line of lines) {
    if (!line.trim() || line.trim().startsWith("#")) continue;

    const dashMatch = line.match(/^\s*-\s+label\s*:\s*(.+?)\s*$/);
    if (dashMatch) {
      commit();
      cur = { label: stripValue(dashMatch[1]) };
      continue;
    }

    const dashOnly = line.match(/^\s*-\s*$/);
    if (dashOnly) {
      commit();
      cur = {};
      continue;
    }

    const kv = line.match(/^\s+(label|prompt)\s*:\s*(.+?)\s*$/);
    if (kv && cur) {
      const key = kv[1] as "label" | "prompt";
      cur[key] = stripValue(kv[2]);
    }
  }
  commit();
  return out;
}

export async function loadDemoPrompts(url = "/demo-prompts.yaml"): Promise<DemoPrompt[]> {
  try {
    const r = await fetch(url, { cache: "no-cache" });
    if (!r.ok) return [];
    const text = await r.text();
    return parseDemoPrompts(text);
  } catch {
    return [];
  }
}
