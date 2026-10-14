// Research spike for docs/runtime-alternatives.md: drive `codex app-server` over
// stdio with the Aperture Excel MCP and record the event stream. Not used by the
// add-in; it changes no default behaviour. A local mock of the Responses API
// stands in for the model, so no credentials are read or needed, and the exact
// instructions and tool list Codex sends to a provider can be inspected.
//
// node tools/CodexSpike/spike.mjs --codex <codex.exe> [--python <python.exe>]
//   [--context <target.json>] [--out <dir>] [--model <slug>] [--scenario tool|write|race|defaults|error|interrupt|resume|all]
//
// Without --context a placeholder target is used: the MCP server starts and lists
// its tools, but every Excel call fails its window/PID check before any COM access.
import { spawn } from "node:child_process";
import http from "node:http";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const args = parseArgs(process.argv.slice(2));
const codex = args.codex ?? "codex";
const python = path.resolve(args.python ?? path.join(root, ".venv", "Scripts", "python.exe"));
const out = path.resolve(args.out ?? path.join(root, ".local", "codex-spike", new Date().toISOString().replace(/[:.]/g, "-")));
const scenarios = args.scenario && args.scenario !== "all" ? [args.scenario] : ["tool", "write", "race", "defaults", "error", "interrupt", "resume"];
const model = args.model ?? "aperture-mock";
const baseInstructions = "APERTURE-SPIKE-BASE: You are Aperture inside Microsoft Excel. Use only the aperture_excel MCP tools.";

// Built-in tool and prompt sources switched off for the Aperture profile. Every key
// is accepted by `codex app-server --strict-config` of the version under test; the
// spike records what still reaches the provider afterwards.
const DISABLED_FEATURES = ["shell_tool", "unified_exec", "view_image", "multi_agent", "apps", "plugins",
  "image_generation", "browser_use", "browser_use_external", "computer_use", "in_app_browser", "skill_search",
  "sleep_tool", "tool_suggest", "goals", "memories", "hooks", "skill_mcp_dependency_install", "code_mode_host",
  "workspace_dependencies", "shell_snapshot", "remote_plugin", "realtime_conversation", "worktrees"];
const MINIMAL_CONFIG = `
project_doc_max_bytes = 0
web_search = "disabled"
include_environment_context = false
include_permissions_instructions = false
include_apps_instructions = false
include_collaboration_mode_instructions = false

[skills]
include_instructions = false

[skills.bundled]
enabled = false

[tools.experimental_request_user_input]
enabled = false

[features]
${DISABLED_FEATURES.map(name => name + " = false").join("\n")}
`;

fs.mkdirSync(out, { recursive: true });
let contextPath = args.context && path.resolve(args.context);
if (!contextPath) {
  contextPath = path.join(out, "placeholder-target.json");
  writeJson(contextPath, { excelInstanceId: "spike-instance", workbookId: "spike-workbook", excelProcessId: 1,
    excelHwnd: 1, workbookName: "Spike.xlsx", workbookFullName: "C:\\spike\\Spike.xlsx", workbookHwnd: 1 });
}
const target = JSON.parse(fs.readFileSync(contextPath, "utf8").replace(/^\uFEFF/, ""));
const mock = await startMockProvider();

const SCENARIOS = {
  // Reasoning summary, progress text and one MCP call with streamed arguments, then the final answer.
  async tool(dir) {
    mock.use(call => call === 1 ? toolCallResponse("excel_target", {}) : textResponse("Инструмент вернул результат; итог записан."));
    const session = await startCodex(dir, { minimal: true });
    try {
      const threadId = await startThread(session);
      await mcpReady(session);
      const mcp = await session.rpc.request("mcpServerStatus/list", { threadId, serverName: "aperture_excel" });
      const completed = await runTurn(session, threadId, "Проверь привязку книги и кратко ответь.");
      return { ...describe(session, completed), mcpTools: mcp.data.map(s => ({ name: s.name, tools: Object.keys(s.tools ?? {}) })),
        ...describeProviderRequest(mock.requests(dir)[0]) };
    } finally { await session.close(); }
  },
  // A non-read-only MCP tool (readOnlyHint=false) under several approval settings:
  // does Codex ask the client before running it? With the placeholder target an
  // executed call still fails its window check, so "executed" is visible in the result.
  async write(dir) {
    // Never write into a real bound workbook from a protocol experiment.
    if (args.context) return { skipped: "The write scenario runs only with the placeholder target." };
    const variants = [
      { name: "never", approvalPolicy: "never" },
      { name: "on-request-decline", approvalPolicy: "on-request", accept: false },
      { name: "on-request-accept", approvalPolicy: "on-request", accept: true },
      { name: "never-approve-mode", approvalPolicy: "never", toolsApproval: "approve" }
    ];
    const result = {};
    for (const variant of variants) {
      const sub = path.join(dir, variant.name);
      mock.use(call => call === 1
        ? toolCallResponse("excel_write_values", { sheet: "Лист1", startCell: "A1", values: [[1]] })
        : textResponse("Готово."));
      const session = await startCodex(sub, { minimal: true, toolsApproval: variant.toolsApproval });
      session.acceptElicitation = variant.accept === true;
      try {
        const threadId = await startThread(session, { approvalPolicy: variant.approvalPolicy });
        await mcpReady(session);
        const completed = await runTurn(session, threadId, "Запиши 1 в A1.");
        const { turnStatus, mcpToolCallItems, serverRequests } = describe(session, completed);
        result[variant.name] = { turnStatus, serverRequests, finalToolItem: mcpToolCallItems.at(-1) ?? null };
      } finally { await session.close(); }
    }
    return result;
  },
  // turn/start immediately after thread/start, without waiting for MCP readiness:
  // which tools reach the first model request, with and without `required = true`?
  async race(dir) {
    const result = {};
    for (const requiredMcp of [false, true]) {
      const sub = path.join(dir, requiredMcp ? "required" : "optional");
      mock.use(() => textResponse("ok"));
      const session = await startCodex(sub, { minimal: true, requiredMcp });
      try {
        const threadId = await startThread(session);
        const completed = await runTurn(session, threadId, "ping");
        const tools = describeProviderRequest(mock.requests(sub)[0]).toolsSentToModel;
        result[requiredMcp ? "required" : "optional"] = { turnStatus: completed.params.turn.status,
          excelToolsInFirstRequest: tools.filter(name => name.includes("aperture_excel")).length,
          notificationSequence: describe(session, completed).notificationSequence };
      } finally { await session.close(); }
    }
    return result;
  },
  // The same profile without the minimizing config and without baseInstructions.
  async defaults(dir) {
    mock.use(() => textResponse("ok"));
    const session = await startCodex(dir, { minimal: false });
    try {
      const threadId = await startThread(session, { baseInstructions: undefined });
      const completed = await runTurn(session, threadId, "ping");
      const request = describeProviderRequest(mock.requests(dir)[0]);
      return { turnStatus: completed.params.turn.status, ...request };
    } finally { await session.close(); }
  },
  // Provider failure: one HTTP retry is allowed so retry notifications become visible.
  async error(dir) {
    mock.use(() => ({ status: 500, body: { error: { message: "mock provider overload", type: "server_error" } } }));
    const session = await startCodex(dir, { minimal: true, retries: 1 });
    try {
      const threadId = await startThread(session);
      const completed = await runTurn(session, threadId, "ping");
      return { ...describe(session, completed), providerCalls: mock.requests(dir).length };
    } finally { await session.close(); }
  },
  // turn/interrupt after the first streamed text delta.
  async interrupt(dir) {
    mock.use(() => textResponse("Длинный ответ, который будет прерван пользователем на середине потока.", 400));
    const session = await startCodex(dir, { minimal: true });
    try {
      const threadId = await startThread(session);
      const turn = await session.rpc.request("turn/start", { threadId, input: [{ type: "text", text: "ping" }] });
      await session.rpc.waitFor(m => m.method === "item/agentMessage/delta", 60_000);
      const interruptAcceptedAt = Date.now();
      await session.rpc.request("turn/interrupt", { threadId, turnId: turn.turn.id });
      const completed = await session.rpc.waitFor(m => m.method === "turn/completed" && m.params.turn.id === turn.turn.id, 60_000);
      return { ...describe(session, completed), msFromInterruptToCompleted: session.events.find(r => r.message === completed).at - interruptAcceptedAt };
    } finally { await session.close(); }
  },
  // History survives a process restart: resume by thread id in a new app-server process.
  async resume(dir) {
    mock.use(() => textResponse("Первый ответ."));
    const first = await startCodex(dir, { minimal: true, log: "events-1.jsonl" });
    let threadId, threadPath;
    try {
      threadId = await startThread(first);
      threadPath = first.thread.path ?? null;
      await runTurn(first, threadId, "Первый вопрос.");
    } finally { await first.close(); }
    mock.use(() => textResponse("Второй ответ."));
    const second = await startCodex(dir, { minimal: true, log: "events-2.jsonl" });
    try {
      const resumed = await second.rpc.request("thread/resume", { threadId, excludeTurns: true });
      const completed = await runTurn(second, threadId, "Второй вопрос.");
      const last = mock.requests(dir).at(-1)?.body?.input ?? [];
      const replayed = last.filter(item => item.type === "message").map(item => `${item.role}: ${textOf(item).slice(0, 60)}`);
      return { threadPath, resumedThreadId: resumed.thread.id, turnStatus: completed.params.turn.status, providerInputAfterResume: replayed,
        rolloutFiles: listFiles(path.join(dir, "codex-home", "sessions")).map(file => path.relative(dir, file)) };
    } finally { await second.close(); }
  }
};

const results = {};
try {
  for (const name of scenarios) {
    if (!SCENARIOS[name]) throw new Error("Unknown scenario " + name);
    console.error(`== ${name}`);
    try { results[name] = await SCENARIOS[name](path.join(out, name)); }
    catch (error) { results[name] = { failed: String(error.stack ?? error) }; process.exitCode = 1; }
  }
} finally { mock.server.close(); }
writeJson(path.join(out, "summary.json"), results);
console.log(JSON.stringify(results, null, 2));
console.log("\nArtifacts: " + out);

async function startCodex(dir, { minimal, retries = 0, requiredMcp = false, toolsApproval, log = "events.jsonl" }) {
  const home = path.join(dir, "codex-home");
  const work = path.join(dir, "work");
  fs.mkdirSync(home, { recursive: true });
  fs.mkdirSync(work, { recursive: true });
  mock.scope(dir);
  writeConfig(home, { minimal, retries, requiredMcp, toolsApproval });
  const child = spawn(codex, ["app-server", "--strict-config", "--listen", "stdio://"], { cwd: work, env: isolatedEnv(home), stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
  child.stderr.pipe(fs.createWriteStream(path.join(dir, log.replace(".jsonl", "-stderr.log"))));
  const session = { dir, work, child, events: [], log: fs.createWriteStream(path.join(dir, log)) };
  session.rpc = jsonRpc(session);
  session.init = await session.rpc.request("initialize", {
    clientInfo: { name: "aperture_codex_spike", title: "Aperture2 Codex spike", version: "0.0.1" },
    capabilities: { experimentalApi: false }
  });
  session.rpc.notify("initialized");
  session.close = async () => {
    child.stdin.end();
    const exited = await Promise.race([new Promise(r => child.once("exit", () => r(true))), delay(5000).then(() => false)]);
    if (!exited) { child.kill(); console.error("codex app-server did not exit after stdin closed; killed it."); }
    session.log.end();
  };
  return session;
}

async function startThread(session, overrides = {}) {
  const params = { cwd: session.work, approvalPolicy: "never", sandbox: "read-only", baseInstructions, ephemeral: false, ...overrides };
  const started = await session.rpc.request("thread/start", params);
  session.thread = started.thread;
  session.instructionSources = started.instructionSources ?? [];
  return started.thread.id;
}

function mcpReady(session) {
  return session.rpc.waitFor(m => m.method === "mcpServer/startupStatus/updated" && m.params.name === "aperture_excel"
    && m.params.status !== "starting", 60_000);
}

async function runTurn(session, threadId, text) {
  const turn = await session.rpc.request("turn/start", { threadId, input: [{ type: "text", text }] });
  return session.rpc.waitFor(m => m.method === "turn/completed" && m.params.turn.id === turn.turn.id, 120_000);
}

function writeConfig(home, { minimal, retries, requiredMcp, toolsApproval }) {
  const lit = value => "'" + value.replace(/'/g, "") + "'";
  fs.writeFileSync(path.join(home, "config.toml"), `
model = "${model}"
model_provider = "aperture_mock"
model_reasoning_summary = "auto"
${minimal ? MINIMAL_CONFIG : ""}
[model_providers.aperture_mock]
name = "Aperture local mock"
base_url = "http://127.0.0.1:${mock.port}/v1"
wire_api = "responses"
request_max_retries = ${retries}
stream_max_retries = 0

[mcp_servers.aperture_excel]
command = ${lit(python)}
args = ["-m", "aperture_mcp", "--context", ${lit(contextPath)}]
cwd = ${lit(root)}
startup_timeout_sec = 30
required = ${requiredMcp}${toolsApproval ? `
default_tools_approval_mode = "${toolsApproval}"` : ""}
tool_timeout_sec = 45

[mcp_servers.aperture_excel.env]
PYTHONPATH = ${lit(path.join(root, "src"))}
PYTHONIOENCODING = "utf-8"
`);
}

function isolatedEnv(home) {
  const env = { ...process.env, CODEX_HOME: home };
  for (const key of Object.keys(env)) if (/^(OPENAI_|CODEX_API_KEY|CODEX_ACCESS_TOKEN|OPENROUTER_)/i.test(key)) delete env[key];
  return env;
}

function jsonRpc(session) {
  let nextId = 1, buffer = "";
  const pending = new Map(), waiters = [];
  const proc = session.child;
  proc.stdout.setEncoding("utf8");
  proc.stdout.on("data", chunk => {
    buffer += chunk;
    let index;
    while ((index = buffer.indexOf("\n")) >= 0) {
      const line = buffer.slice(0, index).replace(/\r$/, ""); buffer = buffer.slice(index + 1);
      if (!line) continue;
      const message = JSON.parse(line);
      const record = { at: Date.now(), message };
      session.events.push(record); session.log.write(JSON.stringify(record) + "\n");
      if (message.id !== undefined && message.method === undefined) {
        const entry = pending.get(message.id); pending.delete(message.id);
        if (entry) message.error ? entry.reject(new Error(JSON.stringify(message.error))) : entry.resolve(message.result);
      } else if (message.id !== undefined) answerServerRequest(message);
      for (const waiter of [...waiters]) if (waiter.match(message)) { waiters.splice(waiters.indexOf(waiter), 1); waiter.resolve(message); }
    }
  });
  proc.on("exit", code => { for (const entry of pending.values()) entry.reject(new Error("codex exited with " + code)); });
  const send = message => proc.stdin.write(JSON.stringify(message) + "\n");
  // Approvals are declined and unknown requests rejected. An MCP tool approval
  // arrives as a form elicitation; only the write scenario's accept variant grants it.
  function answerServerRequest(message) {
    if (message.method === "mcpServer/elicitation/request")
      send({ id: message.id, result: { action: session.acceptElicitation ? "accept" : "decline", content: session.acceptElicitation ? {} : null } });
    else if (/requestApproval$|Approval$/.test(message.method)) send({ id: message.id, result: { decision: "decline" } });
    else send({ id: message.id, error: { code: -32601, message: "Not supported by the Aperture spike" } });
  }
  return {
    request(method, params) {
      const id = nextId++;
      return new Promise((resolve, reject) => { pending.set(id, { resolve, reject }); send({ id, method, params }); });
    },
    notify(method, params) { send(params === undefined ? { method } : { method, params }); },
    waitFor(match, timeoutMs) {
      const seen = session.events.find(record => match(record.message));
      if (seen) return Promise.resolve(seen.message);
      return new Promise((resolve, reject) => {
        const waiter = { match, resolve };
        waiters.push(waiter);
        setTimeout(() => { if (waiters.includes(waiter)) reject(new Error("Timed out waiting for codex event")); }, timeoutMs);
      });
    }
  };
}

// Minimal Responses API endpoint. Each scenario installs a responder keyed by the
// provider call number; every request body is kept for inspection.
async function startMockProvider() {
  let responder = () => textResponse("ok"), scope = out, calls = 0;
  const all = [];
  const server = http.createServer((req, res) => {
    let body = "";
    req.setEncoding("utf8");
    req.on("data", chunk => { body += chunk; });
    req.on("end", () => {
      let json = null;
      try { json = body ? JSON.parse(body) : null; } catch { json = { unparsed: body.slice(0, 2000) }; }
      const entry = { scope, method: req.method, url: req.url, body: json };
      all.push(entry);
      fs.writeFileSync(path.join(scope, "provider-requests.json"), JSON.stringify(all.filter(r => r.scope === scope), null, 2));
      if (req.method !== "POST" || !/\/responses$/.test(req.url)) {
        res.writeHead(404, { "content-type": "application/json" });
        res.end(JSON.stringify({ error: { message: "not mocked" } }));
        return;
      }
      const reply = responder(++calls, json);
      const plan = typeof reply === "function" ? reply(json) : reply;
      if (plan.status) { res.writeHead(plan.status, { "content-type": "application/json" }); res.end(JSON.stringify(plan.body)); return; }
      void stream(res, plan.events, plan.pause ?? 15);
    });
  });
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  return {
    server, port: server.address().port,
    use(next) { responder = next; calls = 0; },
    scope(dir) { scope = dir; },
    requests(dir) { return all.filter(r => r.scope === dir && r.method === "POST" && /\/responses$/.test(r.url)); }
  };
}

// Arguments are limited to the properties the advertised tool schema declares.
function toolCallResponse(needle, extra) { return request => {
  const tool = findTool(request?.tools ?? [], needle);
  const allowed = Object.keys(tool?.parameters?.properties ?? {});
  const argsObject = Object.fromEntries(Object.entries({ excelInstanceId: target.excelInstanceId, workbookId: target.workbookId, ...extra })
    .filter(([key]) => allowed.includes(key)));
  const call = JSON.stringify(argsObject);
  const half = Math.ceil(call.length / 2);
  return { events: [
    { type: "response.created", response: { id: "resp_1" } },
    ...reasoningItem(0, "rs_1", ["Нужно ", "проверить ", "привязку."]),
    ...messageItem(1, "msg_1", ["Проверяю ", "привязку ", "книги."]),
    ...(tool ? functionCallItem(2, "fc_1", "call_spike_1", tool, [call.slice(0, half), call.slice(half)]) : []),
    completed("resp_1")
  ] };
}; }

function textResponse(text, pause) {
  return { pause, events: [{ type: "response.created", response: { id: "resp_t" } }, ...messageItem(0, "msg_t", text.split(/(?<= )/)), completed("resp_t")] };
}

function findTool(tools, needle) {
  for (const tool of tools) {
    if (typeof tool.name === "string" && tool.name.includes(needle) && !Array.isArray(tool.tools)) return { name: tool.name, parameters: tool.parameters };
    for (const child of tool.tools ?? [])
      if (typeof child.name === "string" && child.name.includes(needle)) return { name: child.name, namespace: tool.name, parameters: child.parameters };
  }
  return null;
}

function reasoningItem(index, id, parts) {
  return [
    { type: "response.output_item.added", output_index: index, item: { type: "reasoning", id, summary: [] } },
    { type: "response.reasoning_summary_part.added", item_id: id, output_index: index, summary_index: 0, part: { type: "summary_text", text: "" } },
    ...parts.map(delta => ({ type: "response.reasoning_summary_text.delta", item_id: id, output_index: index, summary_index: 0, delta })),
    { type: "response.output_item.done", output_index: index, item: { type: "reasoning", id, summary: [{ type: "summary_text", text: parts.join("") }] } }
  ];
}

function messageItem(index, id, parts) {
  return [
    { type: "response.output_item.added", output_index: index, item: { type: "message", id, role: "assistant", content: [] } },
    ...parts.map(delta => ({ type: "response.output_text.delta", item_id: id, output_index: index, content_index: 0, delta })),
    { type: "response.output_item.done", output_index: index,
      item: { type: "message", id, role: "assistant", content: [{ type: "output_text", text: parts.join(""), annotations: [] }] } }
  ];
}

function functionCallItem(index, id, callId, tool, parts) {
  const base = { type: "function_call", id, call_id: callId, name: tool.name, ...(tool.namespace ? { namespace: tool.namespace } : {}) };
  return [
    { type: "response.output_item.added", output_index: index, item: { ...base, arguments: "", status: "in_progress" } },
    ...parts.map(delta => ({ type: "response.function_call_arguments.delta", item_id: id, output_index: index, delta })),
    { type: "response.output_item.done", output_index: index, item: { ...base, arguments: parts.join(""), status: "completed" } }
  ];
}

function completed(id) {
  return { type: "response.completed", response: { id, usage: { input_tokens: 10, input_tokens_details: { cached_tokens: 0 },
    output_tokens: 5, output_tokens_details: { reasoning_tokens: 0 }, total_tokens: 15 } } };
}

async function stream(res, items, pause) {
  res.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-cache" });
  for (const item of items) {
    if (res.destroyed) return;
    res.write(`event: ${item.type}\ndata: ${JSON.stringify(item)}\n\n`);
    await delay(pause);
  }
  res.end();
}

function describe(session, completedEvent) {
  const methods = [];
  for (const { message } of session.events) {
    if (message.method === undefined) continue;
    const item = message.params?.item;
    const label = message.method + (item ? `:${item.type}${item.status ? "/" + item.status : ""}` : "")
      + (message.method === "error" ? `(willRetry=${message.params.willRetry})` : "");
    if (methods.length && methods[methods.length - 1].label === label) methods[methods.length - 1].count++;
    else methods.push({ label, count: 1 });
  }
  const tools = session.events.map(r => r.message.params?.item).filter(item => item?.type === "mcpToolCall");
  return {
    codexUserAgent: session.init?.userAgent ?? null,
    threadPath: session.thread?.path ?? null,
    instructionSources: session.instructionSources,
    turnStatus: completedEvent.params.turn.status,
    turnError: completedEvent.params.turn.error ?? null,
    mcpToolCallItems: tools.map(item => ({ id: item.id, status: item.status, arguments: item.arguments, error: item.error,
      result: item.result?.content?.map(part => part.text ?? part.type).join("\n").slice(0, 200) ?? null })),
    serverRequests: session.events.filter(r => r.message.id !== undefined && r.message.method !== undefined).map(r => r.message.method),
    notificationSequence: methods.map(m => m.count > 1 ? `${m.label} ×${m.count}` : m.label)
  };
}

function describeProviderRequest(request) {
  const body = request?.body ?? {};
  return {
    requestKeys: Object.keys(body).sort(),
    instructionsField: typeof body.instructions === "string" ? body.instructions.slice(0, 160) + (body.instructions.length > 160 ? "…" : "") : null,
    instructionsLength: typeof body.instructions === "string" ? body.instructions.length : 0,
    toolsSentToModel: (body.tools ?? []).flatMap(tool => Array.isArray(tool.tools) ? tool.tools.map(child => `${tool.name}/${child.name}`) : [tool.name ?? tool.type]),
    inputMessages: (body.input ?? []).filter(item => item.type === "message").map(item => ({ role: item.role, length: textOf(item).length, text: textOf(item).slice(0, 200) }))
  };
}

function textOf(item) { return (item.content ?? []).map(part => part.text ?? "").join(""); }
function listFiles(dir) {
  if (!fs.existsSync(dir)) return [];
  return fs.readdirSync(dir, { withFileTypes: true }).flatMap(entry => entry.isDirectory() ? listFiles(path.join(dir, entry.name)) : [path.join(dir, entry.name)]);
}
function parseArgs(argv) {
  const result = {};
  for (let i = 0; i < argv.length; i++) {
    const key = argv[i].replace(/^--/, "").replace(/-(\w)/g, (_, c) => c.toUpperCase());
    if (argv[i + 1] === undefined || argv[i + 1].startsWith("--")) result[key] = true;
    else result[key] = argv[++i];
  }
  return result;
}
function writeJson(file, value) { fs.writeFileSync(file, JSON.stringify(value, null, 2) + "\n", "utf8"); }
function delay(ms) { return new Promise(resolve => setTimeout(resolve, ms)); }
