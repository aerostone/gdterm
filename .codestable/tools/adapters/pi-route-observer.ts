// Managed by codestable; edit the source template instead of the installed copy.
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { randomUUID } from "node:crypto";
import { appendFileSync, mkdirSync } from "node:fs";
import { dirname, isAbsolute, resolve } from "node:path";

type RouteEvent = Record<string, unknown>;

const seen = new Set<string>();
const pending: string[] = [];
let activeRequest = "";
let latestRequest = "";
let skillsByPath = new Map<string, string>();

function eventPath(cwd: string): string {
  return resolve(cwd, ".codestable", "telemetry", "routes.jsonl");
}

function appendEvent(cwd: string, event: RouteEvent): void {
  const path = eventPath(cwd);
  mkdirSync(dirname(path), { recursive: true });
  appendFileSync(path, `${JSON.stringify({
    ...event,
    schema: 1,
    timestamp: new Date().toISOString(),
  })}\n`, { encoding: "utf8", mode: 0o600 });
}

function recordInvocation(cwd: string, requestId: string, sessionId: string,
                          skill: string, source: "explicit" | "implicit"): void {
  if (!requestId || !/^cs(?:-|$)/.test(skill)) return;
  const key = `${requestId}\0${skill}\0${source}`;
  if (seen.has(key)) return;
  seen.add(key);
  appendEvent(cwd, {
    event: "invocation",
    request_id: requestId,
    session_id: sessionId,
    platform: "pi",
    skill,
    source,
  });
}

type LoadedSkill = { name: string; filePath: string };

function mapSkills(cwd: string, skills: LoadedSkill[] | undefined): Map<string, string> {
  const result = new Map<string, string>();
  for (const skill of skills ?? []) {
    if (!/^cs(?:-|$)/.test(skill.name)) continue;
    const path = isAbsolute(skill.filePath) ? skill.filePath : resolve(cwd, skill.filePath);
    result.set(resolve(path), skill.name);
  }
  return result;
}

export default function routeObserver(pi: ExtensionAPI) {
  pi.on("input", async (event, ctx) => {
    const requestId = `req-${randomUUID().replaceAll("-", "")}`;
    const sessionId = ctx.sessionManager.getSessionId();
    pending.push(requestId);
    latestRequest = requestId;
    appendEvent(ctx.cwd, {
      event: "request",
      request_id: requestId,
      session_id: sessionId,
      platform: "pi",
      source: event.source,
    });
    const explicit = event.text.match(/^\/skill:(cs(?:-[a-z0-9]+)*)\b/i);
    if (explicit) recordInvocation(ctx.cwd, requestId, sessionId, explicit[1].toLowerCase(), "explicit");
    return { action: "continue" };
  });

  pi.on("before_agent_start", async (event, ctx) => {
    activeRequest = pending.shift() ?? latestRequest;
    skillsByPath = mapSkills(ctx.cwd, event.systemPromptOptions.skills);
  });

  pi.on("tool_call", async (event, ctx) => {
    if (event.toolName !== "read") return;
    const rawPath = (event.input as { path?: unknown }).path;
    if (typeof rawPath !== "string") return;
    const path = resolve(ctx.cwd, rawPath);
    const skill = skillsByPath.get(path);
    if (skill) {
      recordInvocation(ctx.cwd, activeRequest, ctx.sessionManager.getSessionId(), skill, "implicit");
    }
  });

  pi.on("agent_end", async () => {
    activeRequest = "";
  });

  pi.registerCommand("route-correct", {
    description: "Label the latest codestable routing result",
    handler: async (args, ctx) => {
      const [expected, original = "", reason = "manual"] = args.trim().split(/\s+/, 3);
      if (!latestRequest || !expected || !(expected === "none" || /^cs(?:-|$)/.test(expected))) {
        ctx.ui.notify("Usage: /route-correct <cs-expected|none> [cs-original] [reason-code]", "warning");
        return;
      }
      appendEvent(ctx.cwd, {
        event: "correction",
        request_id: latestRequest,
        platform: "pi",
        expected_skill: expected,
        original_skill: original,
        reason_code: reason,
      });
      ctx.ui.notify(`Route label recorded for ${latestRequest}`, "info");
    },
  });
}
