# Agent Client Protocol (ACP): protocol facts and engine support

Research date: 2026-10-14. Read-only research; nothing installed. Sources were read from primary GitHub repos (via `gh api`), npm/crates.io/PyPI/NuGet/Maven registries, the ACP registry CDN, and the docs sites.

Baseline citation for Part A, unless stated otherwise:
- Repo: https://github.com/agentclientprotocol/agent-client-protocol (Apache-2.0). `zed-industries/agent-client-protocol` is the original name.
- Tag `schema-v1.25.0` is the latest stable schema release, published 2026-10-09. The annotated tag points to commit `4cf3dd858c819fc3ab99ae53f76883ede0345a10`.
- Pre-release: `schema-v2.0.0-alpha.8` (2026-10-09), the ACP v2 draft.
- Wire `protocolVersion`: integer `1` (`schema/v1/meta.json` `"version": 1`). v2 uses `2` and is a draft (announced 2026-07-20).
- Doc pages are at `https://agentclientprotocol.com/protocol/v1/<page>`. Their sources are at `docs/protocol/v1/<page>.mdx` in the tag.
- Schemas: `schema/v1/schema.json` (stable) and `schema/v1/schema.unstable.json` (unstable/RFD). The method tables are in `schema/v1/meta.json` and `meta.unstable.json`.

---

## Part A — Protocol

### A1. Transport, launch, handshake, capabilities, auth

**Transport.** Source: `docs/protocol/v1/transports.mdx`, https://agentclientprotocol.com/protocol/v1/transports
- Messages are JSON-RPC 2.0 and UTF-8.
- The defined transport is **stdio**. Streamable HTTP is marked "draft proposal in progress". Custom transports are allowed.
- With stdio, "The client launches the agent as a subprocess." The agent reads from stdin and writes to stdout.
- Messages are **newline-delimited** (`\n`) and "MUST NOT contain embedded newlines".
- stderr may carry logs. stdout must carry nothing that is not an ACP message.
- The HTTP/WebSocket transport RFD is in the **Active** stage, not stable (`docs/rfds/streamable-http-websocket-transport.mdx`). A Transports Working Group was announced 2026-04-22.

**Message conventions.** Source: `overview.mdx`
- Requests and notifications follow JSON-RPC 2.0.
- All file paths MUST be absolute. Line numbers are 1-based.
- Property keys use camelCase. Discriminator values use snake_case.

**`initialize` (client → agent).** Source: `initialization.mdx`
- The request requires `protocolVersion`. It optionally carries `clientCapabilities`, `clientInfo {name, title, version}` and `_meta`.
- Version negotiation: the agent echoes the requested version if it supports it. Otherwise it returns its latest version. If the client does not support that version, it SHOULD close the connection.
- The response requires `protocolVersion`. It optionally carries `agentCapabilities`, `authMethods[]`, `agentInfo` and `_meta`.
- Any capability omitted from `initialize` MUST be treated as unsupported.

**`clientCapabilities` (stable schema).**
- `fs.readTextFile` and `fs.writeTextFile` (booleans).
- `terminal` (boolean, covers all `terminal/*` methods).
- `auth.terminal` (boolean).
- `elicitation` (`form` / `url` modes).
- `session`, which contains:
  - `session.configOptions.boolean: {}`
  - `session.compaction: {}`
  - `session.notices: {}`
- `_meta`.

**`agentCapabilities` (stable schema).**
- `loadSession` (boolean, default false).
- `promptCapabilities {image, audio, embeddedContext}`. Text and ResourceLink are mandatory baseline content.
- `mcpCapabilities {http, sse}`. SSE is deprecated by MCP. stdio MCP support is mandatory for all agents.
- `sessionCapabilities {list, delete, additionalDirectories, resume, close}`. Each is an object; `{}` means supported.
- `auth.logout`.
- `_meta`.
- The unstable schema adds `sessionCapabilities.fork`, plus `providers`, `nes` and `positionEncoding`.

**Authentication.** Source: `authentication.mdx`
- `authMethods[]` entries have `id`, `name`, `description` and an optional `type`.
- `type` absent means `agent`: the client calls `authenticate {methodId}` and receives `{}`.
- `type: "terminal"` (with `args` and `env`) means the client re-runs the configured agent command in an interactive terminal and then reconnects. Agents may advertise it only if the client sets `auth.terminal: true`. The client MUST NOT call `authenticate` for it.
- `logout` is stable since 2026-05-21 and gated by `agentCapabilities.auth.logout`.
- Auth-gated requests fail with an `auth_required` error.

### A2. Session lifecycle

**Stable agent methods** (`schema/v1/meta.json`, schema-v1.25.0):
- `initialize`, `authenticate`
- `session/new`, `session/load`
- `session/set_mode`, `session/set_config_option`
- `session/prompt`, `session/cancel` (notification)
- `session/list`, `session/delete`, `session/resume`, `session/close`
- `logout`
- Protocol-level: `$/cancel_request`

**Stable client methods:**
- `session/request_permission`, `session/update` (notification)
- `fs/read_text_file`, `fs/write_text_file`
- `terminal/create`, `terminal/output`, `terminal/release`, `terminal/wait_for_exit`, `terminal/kill`
- `elicitation/create`, `elicitation/complete` (notification)

**Unstable-only** (`meta.unstable.json`):
- `session/fork`
- `providers/list|set|disable`
- `mcp/message` (MCP-over-ACP)
- `nes/*`
- `document/did*`

**`session/new`.** Source: `session-setup.mdx`
- `NewSessionRequest` requires `cwd` (absolute path) and `mcpServers`. Optional fields: `additionalDirectories` (stable since 2026-06-01) and `_meta`.
- The response requires `sessionId`. Optional fields: `modes`, `configOptions`.

**stdio MCP server entry (`McpServerStdio`).**
- Required fields: `name`, `command`, `args`, `env`. `command` is the absolute path to the executable.
- `env` is an **array of `{name, value}` objects**, not a map. Example:
  `{"name":"filesystem","command":"/path/to/mcp-server","args":["--stdio"],"env":[{"name":"API_KEY","value":"..."}]}`
- No `type` field is needed for stdio.
- HTTP entry: `{type:"http", name, url, headers:[{name,value}]}`, required when `mcpCapabilities.http` is advertised.
- SSE entry: `{type:"sse", ...}`, gated by `mcpCapabilities.sse`.
- The unstable schema adds an `acp` MCP-server variant (MCP-over-ACP RFD, Draft).

**`session/load`.**
- Requires `loadSession: true`. Parameters: `sessionId`, `cwd`, `mcpServers`.
- The agent MUST replay the entire conversation as `session/update` notifications (`user_message_chunk`, `agent_message_chunk`, …) and only then respond.
- Notices are not replayed.

**`session/resume`.**
- Stable since 2026-04-22, gated by `sessionCapabilities.resume`.
- Same parameters as `session/load`. The agent MUST NOT replay history.

**`session/close`.**
- Stable since 2026-04-23. It cancels ongoing work as if `session/cancel` had been sent, then frees resources.

**`session/list`.**
- Stable since 2026-03-09. Optional parameters: `cwd`, `cursor`.
- Response: `sessions[] {sessionId, cwd, additionalDirectories?, title?, updatedAt?}` and `nextCursor`.

**`session/delete`.**
- Stable since 2026-06-05. It removes a session from future `session/list` results.

**`session/fork`.**
- **Unstable.** The RFD is in the **Draft** stage (`docs/docs.json` RFD groups at the tag).
- `ForkSessionRequest {sessionId, cwd, additionalDirectories?, mcpServers?}` returns `{sessionId, modes?, configOptions?}`.

**`session/prompt`.** Source: `prompt-turn.mdx`
- Request: `{sessionId, prompt: ContentBlock[]}`.
- Response (stable): `{stopReason}` only.
- `StopReason` values in the stable schema: `end_turn`, `max_tokens`, `max_turn_requests`, `refusal`, `cancelled`.
- The unstable schema adds an optional `usage {totalTokens, inputTokens, outputTokens, thoughtTokens?, cachedReadTokens?, cachedWriteTokens?}` to the response. This comes from the "End-Turn Token Usage" RFD, which is in the **Draft** stage.
- An `error` stop reason exists in the v2 draft (`ErrorStopReason`; changelog entry "report failures after prompt insertion with an error stop reason", #2281). It is **not** a v1 stable value.

**`session/cancel`** (notification):
- The client SHOULD mark its unfinished tool calls as cancelled.
- The client MUST answer pending `session/request_permission` requests with `outcome: "cancelled"`.
- The agent MUST flush its updates and then answer the original `session/prompt` with `stopReason: "cancelled"`. It must not answer with an error.
- Separately, `$/cancel_request` is a generic JSON-RPC request cancel, stable since 2026-06-29. Its error code is `-32800`.

**`session/set_mode`.**
- Still in the stable schema. The docs say modes "will be removed in a future version" in favour of config options.

**`session/set_config_option`.** Stable since 2026-02-04.
- Request: `{sessionId, configId, value}`. A boolean option also sends `type: "boolean"`.
- The response returns the full `configOptions`.
- `ConfigOption` fields: `id`, `name`, `description?`, `category?`, `type` (`select` | `boolean`), `currentValue`, `options`.
- `category` values: `mode`, `model`, `model_config`, `thought_level`. `_`-prefixed values are custom.
- `boolean` options require the client capability `session.configOptions.boolean`.

**`session/set_model`.**
- **Removed.** RFD updates log, 2026-06-01: "never-stabilized `session/set_model` API … removed".
- Model selection is done through a config option with `category: "model"`.

### A3. `session/update` variants and related types

**Stable `SessionUpdate`** (schema-v1.25.0, `sessionUpdate` discriminator). The list matches the table in `prompt-turn.mdx`, which calls it "the complete set":
1. `user_message_chunk`
2. `agent_message_chunk`
3. `agent_thought_chunk` (all three are `ContentChunk {content, messageId?, _meta?}`)
4. `tool_call` (`ToolCall`)
5. `tool_call_update` (`ToolCallUpdate`)
6. `plan` (`{entries[]}`, always the complete list; the client replaces the whole plan)
7. `available_commands_update` (`{availableCommands[{name, description, input?{hint}}]}`)
8. `current_mode_update`
9. `config_option_update` (`{configOptions}`, the full set)
10. `session_info_update` (`{title?, updatedAt?}`, stable since 2026-03-09)
11. `usage_update` (`{used, size, cost?{amount, currency}}`, stable since 2026-06-05)
12. `notice` (`{severity: info|warning|error, title, description?}`). Requires the client capability `session.notices`. Stabilised in 1.25.0.
13. `compaction_update` (`{compactionId, status: in_progress|completed|failed|cancelled, summary?, error?}`)
14. `compaction_summary_chunk` (13 and 14 require `session.compaction` and were stabilised in 1.25.0)

**Unstable v1 additions:**
- `plan_update`, `plan_removed`
- `subagent_update`
- `session_message`, `session_message_chunk`

**v2 draft** (schema-v2.0.0-alpha.8) has a different set:
- `user_message`, `agent_message`, `agent_thought`: upserts by `messageId`.
- `state_update` with `running` | `idle` (+ `stopReason`) | `requires_action`.
- `tool_call_content_chunk`, `terminal_update`, `terminal_output_chunk`, `plan_update`.
- `other` for unknown variants.

**`ToolCall` (`tool_call`).**
- Required: `toolCallId`, `title`.
- Optional:
  - `name`: programmatic tool name, stabilised 2026-09-17 in schema 1.22.0.
  - `kind`: `read | edit | delete | move | search | execute | think | fetch | switch_mode | other`.
  - `status`: `pending | in_progress | completed | failed`. There is no `cancelled` tool status in v1.
  - `content[]`: a `ToolCallContent` of type `content` (wraps a ContentBlock), `diff {path, oldText?, newText}`, or `terminal {terminalId}`.
  - `locations[{path, line?}]`.
  - `rawInput` and `rawOutput` (any JSON).
  - `_meta`.

**`ToolCallUpdate`.**
- Only `toolCallId` is required. Every other field is a patch.
- `content` and `locations` **replace** the whole collection.
- An omitted or null `rawInput`/`rawOutput` leaves the previous value unchanged.

**Streaming tool-call arguments.**
- **There is no argument-delta mechanism in v1 or the v2 draft.** Only replacement snapshots exist: `rawInput` and `title` on `tool_call_update`.
- The spec acknowledges input streaming only through the status text. `pending` means "the input is either streaming or awaiting approval" (`tool-calls.mdx`).
- v2 adds `tool_call_content_chunk`, which appends whole **output** content items, not argument tokens.
- Implementation evidence: `claude-agent-acp` v0.90.0 (`src/tests/field-tracker.test.ts`, tag commit `7f7a1404…`) parses Anthropic `input_json_delta`. It updates only the `title` while input streams, and "No rawInput travels until the input is complete."

**Message boundaries and IDs.**
- `messageId` on `ContentChunk` is optional in v1. The Message ID RFD was stabilised 2026-06-05.
- Chunks with the same ID belong to one message. A changed ID starts a new message.
- **There is no content-index or block-index field**: `ContentChunk` has only `content`, `messageId` and `_meta`.
- In v2, `messageId` is required.

**End of message versus end of turn.**
- **v1 has no "message finished" event.** The end of a message is implied by a new `messageId`, a change of chunk type, or the turn ending.
- The **turn ends only with the `session/prompt` response** (`stopReason`). Updates sent after a cancel must arrive before that response.
- v2 changes this. The `session/prompt` response returns the inserted user `messageId` on acceptance. The end of foreground work is `state_update {state: "idle", stopReason}`. Updates may also arrive outside turns.

**Permission requests: `session/request_permission` (agent → client request).**
- Parameters: `{sessionId, toolCall: ToolCallUpdate, options[{optionId, name, kind}]}`.
- `kind` values: `allow_once`, `allow_always`, `reject_once`, `reject_always`.
- The result is `{outcome: {outcome: "selected", optionId}}` or `{outcome: {outcome: "cancelled"}}`.
- In v2, permission requests carry their own `title`/`description` and an extensible `subject`.

**Client-side methods.**
- `fs/read_text_file {sessionId, path, line?, limit?}` returns `{content}`. The docs note it can include unsaved editor changes.
- `fs/write_text_file {sessionId, path, content}`.
- `terminal/create {sessionId, command, args?, env?, cwd?, outputByteLimit?}` returns `terminalId`. Related methods: `terminal/output`, `wait_for_exit`, `kill`, `release`.
- All of these are gated by client capabilities.

**Extensibility.** Source: `extensibility.mdx`
- `_meta` is available on most types. Root keys `traceparent`, `tracestate` and `baggage` are reserved for W3C trace context.
- Custom methods and notifications must be prefixed with `_` (e.g. `_zed.dev/...`). Unknown notifications SHOULD be ignored.
- Custom capabilities are advertised via `_meta` inside capability objects.

**RFD stages at the tag** (`docs/docs.json`):

| Stage | RFDs |
|---|---|
| Completed | session-config-options, session-info-update, session-list, acp-agent-registry, session-resume, session-close, logout-method, additional-directories, meta-propagation, session-delete, session-usage, message-id, model-config-category, rust-sdk-v1, request-cancellation, boolean-config-option, elicitation, auth-methods, tool-call-name, session-compaction, session-notices |
| Draft | session-fork, proxy-chains, mcp-over-acp, diff-delete, custom-llm-endpoint, plan-operations, end-turn-token-usage, get-auth-state, subagents |
| Active | streamable-http-websocket-transport, all v2 RFDs |
| To be removed | next-edit-suggestions |

### A4. SDKs and versions

**Protocol repo license:** Apache-2.0 (`LICENSE` at the tag; GitHub reports `Apache-2.0`).

| SDK | Package | Latest version (date) | License | Source |
|---|---|---|---|---|
| TypeScript (official) | `@agentclientprotocol/sdk` | 1.8.0 (2026-10-09) | Apache-2.0 | npm; github.com/agentclientprotocol/typescript-sdk tag v1.8.0 |
| TypeScript (old name) | `@zed-industries/agent-client-protocol` | 0.4.5 | — | npm; deprecated: "renamed to @agentclientprotocol/sdk" |
| Rust SDK (official) | crate `agent-client-protocol` | 3.3.0 (2026-10-09) | Apache-2.0 | crates.io; github.com/agentclientprotocol/rust-sdk |
| Rust schema (official) | crate `agent-client-protocol-schema` | 1.11.0 (2026-10-09) | Apache-2.0 | release "Rust Crate v1.11.0" in the protocol repo |
| Python (official) | PyPI `agent-client-protocol` | 0.12.1 stable (2026-08-16); 1.0.0rc3 pre-release (2026-10-12) | Apache-2.0 | PyPI; github.com/agentclientprotocol/python-sdk |
| Kotlin (official) | Maven `com.agentclientprotocol:acp` | 0.33.0 (2026-10-09) | Apache-2.0 | repo1.maven.org metadata; GitHub release v0.33.0 |
| Java (official) | Maven `com.agentclientprotocol:acp-core` / `acp-java-sdk` | 0.80.0 (2026-10-08) | Apache-2.0 | Maven metadata; github.com/agentclientprotocol/java-sdk |

Notes on the table:
- The TypeScript and Rust SDKs reached 1.0 on 2026-06-25 (announcement `sdk-1-0-releases.mdx`).
- The Kotlin docs page still shows `0.1.0-SNAPSHOT`, which is stale.
- The Java versions jumped from 0.18.0 to 0.80.0, as published.
- Test kit: `agentclientprotocol/acp-tck`, Apache-2.0. It is experimental, and passing it "is not an official statement of ACP conformance".

**C#/.NET: there is no official SDK.** The org repo list has no .NET repo. The docs list three community .NET libraries in `docs/libraries/community.mdx`; others exist on NuGet. Checked on NuGet on 2026-10-14:

| Package | Repo | Version (date) | License | TFMs | Notes |
|---|---|---|---|---|---|
| **dotacp.protocol / .client / .agent** | github.com/timxx/dotacp | 2026.7.19 (2026-07-19) | Apache-2.0 | net8/9/10, **.NETFramework 4.7.2**, **.NETStandard 2.0** | The only package found that runs directly on .NET Framework 4.8. Uses Newtonsoft.Json. Generated from **schema-v1.19.0** (`protocol/schema/VERSION`), so it lacks `name`, notices and compaction (schema 1.22+). Not in the docs list. |
| AgentClientProtocol | github.com/nuskey8/acp-csharp (listed in docs) | 0.1.5 (2025-11-05) | MIT | net8/9/10, netstandard2.1 | netstandard2.1 is not usable from .NET Framework 4.8. No releases since 2025-11. |
| Acp.Net.Process / Acp.Net.Testing | github.com/MertBasar0/acp-net (listed in docs) | 0.1.1 (2026-07-26) | Apache-2.0 | net8.0 | — |
| Agentic.ACPLibrary | github.com/AgenticDesktop/ACPLibrary (listed in docs) | none (pushed 2026-09-30) | MIT | — | No NuGet release found. |
| AcpKit.Protocol.V1 | github.com/vezaynk/acpkit | 0.1.0 (2026-09-11) | Apache-2.0 | net8/10 | — |
| LibAcp | github.com/sargeMonkey/libACP | 0.1.0 (2026-05-18) | MIT | net8/10 | — |
| Andy.Acp.Core | github.com/rivoli-ai/andy-acp | 2026.7.21.1 | Apache-2.0 | net8 | — |
| SalmonEgg.Acp | github.com/salmonloop/salmon-egg | 1.2.0 (2026-10-01) | MIT | net10 | — |

---

## Part B — Engines

**ACP registry snapshot.**
- Fetched from `https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json` on 2026-10-14 at 20:54 UTC. Registry repo release `v2026.10.14-6f6e55b`, format version `1.0.0`.
- The registry is curated and includes only agents that support ACP authentication. It was stabilised 2026-03-09.

| Engine | ACP support | How it is launched | License | Version / date | Source | Verified? |
|---|---|---|---|---|---|---|
| **Pi Coding Agent** (`earendil-works/pi`; `badlogic/pi-mono` redirects here) | **Adapter only.** No native ACP mode. The maintainer declined native ACP PRs #241 (2025-12-19) and #836 (2026-01-19), saying it should be a separate project "on top of rpc mode". | Adapter `pi-acp` (svkozak/pi-acp): `npx pi-acp`. It spawns `pi --mode rpc`. Needs `pi` ≥ v0.81.0 on PATH and Node 22+. | Pi: MIT. pi-acp: MIT. | Pi v1.2.0 (2026-10-12), npm `@earendil-works/pi-coding-agent` 1.2.0. pi-acp v0.0.34 (2026-09-24, tag commit 4eade93d). Registry id `pi-acp`. | github.com/earendil-works/pi/pull/241, /pull/836, /issues/175; github.com/svkozak/pi-acp README | Yes |
| **OpenAI Codex CLI** | **Adapter only.** No native ACP: code search for `agentclientprotocol` in openai/codex returns 0 hits, and feature request #30052 "Built-in ACP protocol support" is still open. | `npx @agentclientprotocol/codex-acp`. It starts the Codex App Server and bundles `@openai/codex`; `CODEX_PATH` overrides the binary. Auth: ChatGPT login, `CODEX_API_KEY`/`OPENAI_API_KEY`, or a gateway. Client MCP servers over stdio and HTTP. | Adapter: Apache-2.0 ("Copyright 2025 JetBrains s.r.o."). Codex: Apache-2.0. | codex-acp v2.2.2 (2026-10-10, tag commit 202e66e6). Rewritten in TypeScript under agentclientprotocol. The old `zed-industries/codex-acp` (Rust) is **archived**: last release v0.16.0 (2026-06-08), npm `@zed-industries/codex-acp` deprecated. | github.com/agentclientprotocol/codex-acp; npm | Yes (absence of native ACP is inferred) |
| **Claude Code** | **Adapter only**, built on the Claude Agent SDK. No native ACP mode was found (inferred). | `npx @agentclientprotocol/claude-agent-acp` (bin `claude-agent-acp`). It depends on `@anthropic-ai/claude-agent-sdk` 0.3.296 and `@agentclientprotocol/sdk` 1.8.0. Supports client MCP servers, permissions, terminals and subagent sessions. | Adapter code: **Apache-2.0** (LICENSE and package.json). Claude Agent SDK and Claude Code: **proprietary**, "SEE LICENSE IN README.md" / "Use is subject to Anthropic's Commercial Terms". The ACP registry marks this entry `"license": "proprietary"`. | claude-agent-acp v0.90.0 (2026-10-13, tag commit 7f7a1404). Renamed twice, both old npm names deprecated: `@zed-industries/claude-code-acp` (last 0.16.2), then `@zed-industries/claude-agent-acp` (last 0.23.1). The `zed-industries/claude-code-acp` repo redirects to agentclientprotocol/claude-agent-acp. | github.com/agentclientprotocol/claude-agent-acp; npm; github.com/anthropics/claude-code LICENSE.md | Yes |
| **Gemini CLI** | **Native.** | `gemini --acp`. `--experimental-acp` still exists but is described as "deprecated, use --acp instead". Registry: `npx @google/gemini-cli@0.64.0 --acp`. | Apache-2.0 | v0.64.0 (2026-10-13) | `packages/cli/src/config/config.ts` at tag v0.64.0 | Yes |
| **Goose** (`block/goose` redirects to `aaif-goose/goose`) | **Native.** | `goose acp` ("Run goose as an ACP agent server on stdio"), with `--with-builtin` options. A separate `acp-http` feature exists. | Apache-2.0 | v1.54.0 (2026-10-08) | `crates/goose-cli/src/cli.rs` at tag v1.54.0 | Yes |
| **OpenCode** (`sst/opencode` redirects to `anomalyco/opencode`) | **Native.** | `opencode acp [--cwd]`. It starts the local OpenCode server and bridges it via `@agentclientprotocol/sdk` (`AgentSideConnection`, `ndJsonStream`). | MIT | v1.19.0 (2026-10-11) | `packages/opencode/src/cli/cmd/acp.ts` at tag v1.19.0 | Yes |
| **DeepSeek Harness (`dsh`)**, official DeepSeek | **Native**, automation-only ACP v1 server (package `@deepseek-ai/dsh-acp`). Also has an ACP *client* (`subagent-acp`) for driving other agents. | `dsh --profile acp`; the README gives `pnpm dsh --profile acp` from source. Default user entry point: `npx @deepseek-ai/dsh web`. | MIT | Repo created 2026-08-13 (developer preview). Latest pre-release `dsh-v0.2.1-alpha.3` (2026-10-14, commit 2fbf4359). npm `@deepseek-ai/dsh`: latest `0.2.0-rc.2` (2026-09-29), alpha `0.2.1-alpha.3`. | github.com/deepseek-ai/deepseek-harness: `packages/acp/acp/README.md`, `packages/bundle/acp-app/README.md` | Yes (the npx command line is unverified) |

**Other ACP-capable engines in the registry** (registry command shown):
- Cursor: `cursor-agent acp`
- GitHub Copilot: `copilot --acp`
- Kimi CLI: `kimi acp`
- Qwen Code: `--acp --experimental-skills`
- Cline: `--acp`
- Junie: `--acp=true`
- Mistral Vibe: `vibe-acp`
- Kilo: `acp`
- fast-agent: `fast-agent-acp`
- Devin: `devin acp`
- Factory Droid: `droid exec --output-format acp-daemon`
- Grok Build: `grok agent stdio`

### DeepSeek Harness details

**What it supports over ACP** (from `packages/acp/acp/README.md` at `dsh-v0.2.1-alpha.3`):
- Stable ACP v1, plus `session/list`, `session/resume` and `session/close`.
- MCP servers declared by the client over stdio and Streamable HTTP.
- `session/set_config_option` for `model` and `reasoning_effort`.
- Text, resource-link and image prompts.
- `session/request_permission` with one-shot allow/reject.
- `usage` updates.
- `authenticate` is a no-op.

**Explicitly unsupported over ACP:**
- `session/load` (no transcript replay), delete, fork, additionalDirectories.
- SSE MCP and ACP-transport MCP.
- Modes, commands, plans, terminals, client `fs/*`, elicitation.

**Implementation:**
- Depends on `@agentclientprotocol/sdk` 1.4.0.
- The `acp-app` profile defaults to provider `deepseek-official` with model `deepseek-v4-flash`. The README example uses `deepseek-v4-pro`.

**Providers:**
- A direct DeepSeek adapter (`dsh-llm-deepseek`).
- Third-party providers through **`@earendil-works/pi-ai` ^1.1.0**, Pi's LLM library (`packages/llm/llm-pi-ai/package.json`). Examples include anthropic, openai, moonshotai (Kimi), zai (GLM) and openai-codex.
- Custom gateways speaking OpenAI Chat Completions, OpenAI Responses or Anthropic Messages (`docs/user/guide/providers.md`).

**MCP:** `@deepseek-ai/dsh-mcp-client` (`docs/subsystems/mcp.md`).

**Registry status:** not in the ACP registry and not on the agentclientprotocol.com agents page as of 2026-10-14.

**Other DeepSeek-related candidates (community, not official):**
- **Deep Code**: github.com/lessweb/deepcode-cli, MIT, v0.4.3 (2026-10-03), npm `@vegamo/deepcode-cli`. Listed on DeepSeek's API docs integration page (https://api-docs.deepseek.com/quick_start/agent_integrations/deepcode/). Supports MCP. No ACP mention in its README.
- **Reasonix**: github.com/esengine/DeepSeek-Reasonix, MIT, latest studio-v2.40.0 (2026-10-14). The README says ACP editors "can connect to `reasonix acp`"; see `docs/ACP.md`.

### Official agents lists

**agentclientprotocol.com/get-started/agents** (live page, fetched 2026-10-14), 43 entries:
- AgentPool
- Augment Code
- AutoDev
- Blackbox AI
- Bub (via bub-acp-server)
- Claude Agent (via "Zed's SDK adapter", link to claude-agent-acp)
- Claw Orchestrator
- Cline
- Codex CLI (via ACP's adapter codex-acp)
- Code Assistant
- Construct
- crow-cli
- CUGA (`cuga-acp`)
- Cursor
- Docker's cagent
- fast-agent
- Factory Droid
- fount
- Gemini CLI
- GitHub Copilot (public preview)
- Goose
- Hermes Agent
- Junie by JetBrains
- Kaagum
- Kimi CLI
- Kiro CLI
- localharness
- Minion Code
- Mistral Vibe
- Muse Spark Code
- OpenClaw
- OpenCode
- OpenHands
- Pi (via pi-acp adapter)
- Poolside
- Qoder CLI
- Qwen Code
- Raven
- Raxol
- siGit Code
- Stakpak
- stdio Bus
- VT Code

At the `schema-v1.25.0` tag the page had 41 entries; CUGA and Muse Spark Code were added later.

**ACP registry.json** (2026-10-14), 41 agent ids:
- agoragentic-acp, amp-acp, antigravity-acp, auggie, autohand, bob
- claude-acp, cline, codebuddy-code, codex-acp, cortex-code, corust-agent
- crow-cli, cursor, deepagents, devin, dimcode, dirac
- factory-droid, fast-agent, gemini, github-copilot-cli, glm-acp-agent, goose
- grok-build, harn, junie, kilo, kimchi, kimi
- minimax-code, minion-code, mistral-vibe, nova, opencode, pi-acp
- poolside, qoder, qwen-code, sigit, stakpak, vtcode

**Zed docs** (https://zed.dev/docs/ai/external-agents, undated):
- The ACP Registry (`zed: acp registry`) is the primary install path.
- Documented agents: Claude Agent, Codex, Gemini CLI, OpenCode, Copilot, Cursor, Pi Coding Agent, Poolside.
- Custom agents are configured as `agent_servers.<id> = {type:"custom", command, args, env}`. Here `env` is a map in Zed settings, unlike ACP's `EnvVariable[]`.

### Relevance notes for Aperture2 (inferences, not decisions)

- **pi-acp does not wire ACP `mcpServers` into Pi.** Its README says MCP servers are "accepted … but not wired through to pi". It also emits no separate thought stream and implements no `fs/*` or `terminal/*` delegation.
- Using Pi through ACP would therefore lose features Aperture2 gets today from Pi RPC: the reasoning stream and the per-session MCP passthrough. The Excel MCP would still need to be configured in Pi itself.
- ACP v1 has no `message_end`/`agent_settled` split and no tool-argument deltas. Aperture2's current Pi-RPC timeline relies on `toolcall_*`, `message_end`, `agent_settled` and `contentIndex`, and these do not map one-to-one. ACP v1 gives `messageId` changes plus the `session/prompt` response.
- The only .NET Framework-compatible ACP library found (dotacp, net472/netstandard2.0) is community-maintained and pinned to schema 1.19.0.

---

## UNVERIFIED / caveats

1. Whether Claude Code or Codex CLI themselves have a hidden native ACP flag. Only absence of evidence: Codex code search found 0 hits and issue #30052 is open; I did not search the Claude Code binary.
2. The exact npm launch line for DeepSeek Harness ACP (`npx @deepseek-ai/dsh --profile acp`). The README only shows `pnpm dsh --profile acp` from source. I did not check that the published npm bundle includes `dsh-acp-app`, or which tag is "latest stable" for ACP use.
3. The DeepSeek Harness "release date 2026-08-13" is the GitHub repo `created_at`. Third-party sites say "v0.1 developer preview Aug 13, 2026", but there is no official announcement page.
4. How well each adapter covers ACP features beyond its README (e.g. whether codex-acp sends `messageId`, `usage_update`, or `rawInput` snapshots during input streaming). Only claude-agent-acp's title-only streaming was checked, via a test file.
5. Whether the official TS, Rust, Python, Kotlin and Java SDK versions above implement schema 1.25.0 completely (e.g. notices and compaction types). Their schema pins were not checked.
6. Content on the agentclientprotocol.com live site is served from `main`. Part A was read at tag `schema-v1.25.0`; I spot-checked only the agents page live, and it differs slightly as noted.
7. Registry `license` fields are self-declared metadata (e.g. claude-acp "proprietary" while the adapter repo is Apache-2.0).
8. GitHub star counts for deepseek-harness (~250k) are as reported by the API and have no bearing on maturity.
