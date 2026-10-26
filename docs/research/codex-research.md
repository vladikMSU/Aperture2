# OpenAI Codex CLI: verified facts for comparison (research 2026-10-14/15)

**Baseline version:** latest stable GitHub release **`rust-v0.162.1`**, published **2026-10-09T19:44:51Z** (non-prerelease, marked "Latest"). Tag commit `092d3acd6bec3e3a14bdc7e7a2810ab628ab759d`. https://github.com/openai/codex/releases/tag/rust-v0.162.1
Newer pre-releases exist (`0.163.0-alpha.*`, up to `rust-v0.163.0-alpha.10.1` on 2026-10-14); those were not examined.
Unless noted, every GitHub link below points at tag `rust-v0.162.1`. Docs pages were read on 2026-10-14/15 and show no version or date.

**Docs location changed.** `developers.openai.com/codex/*` now 308-redirects to `learn.chatgpt.com/docs/*`. Examples: `/codex/app-server` → https://learn.chatgpt.com/docs/app-server, and `/codex/config-advanced` → https://learn.chatgpt.com/docs/config-file/config-advanced.
In-repo `docs/config.md`, `docs/exec.md` and `docs/authentication.md` are only stubs that link to the website.
At this tag, `codex-rs/app-server/README.md` is not the protocol reference either. It contains only addendum sections (Guardian errors, MCP App UI, attachments, and so on): https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server/README.md

---

## 1. License and versions
- The repo is licensed **Apache-2.0**. https://github.com/openai/codex/blob/rust-v0.162.1/LICENSE (NOTICE: "Copyright 2025 OpenAI", plus Ratatui code under MIT: https://github.com/openai/codex/blob/rust-v0.162.1/NOTICE)
- The npm package **`@openai/codex`** is **Apache-2.0**. Its `latest` dist-tag is **0.162.1**, published 2026-10-09T19:50:30Z (checked with `npm view`). https://www.npmjs.com/package/@openai/codex
  - The in-repo manifest also declares `"license": "Apache-2.0"`: https://github.com/openai/codex/blob/rust-v0.162.1/codex-cli/package.json
- Release notes for 0.162.1 contain only bug fixes (#51866, #52648).

## 2. `codex app-server`: launch, transport, handshake, lifecycle, storage

**Status**
- The CLI marks it experimental: "`[experimental] Run the app server or related tooling.`" https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/cli/src/main.rs#L173-L174
- The docs say: "The app-server command and WebSocket transport are experimental and aren't supported for production workloads." https://learn.chatgpt.com/docs/app-server

**Launch**
- Command: `codex app-server`. Transport is set with `--listen <URL>`. Supported values are `stdio://` (the default), `unix://`, `unix://PATH`, `ws://IP:PORT` and `off`. `--stdio` is an alias.
- Other flags: `--strict-config`, `--analytics-default-enabled`, WebSocket auth flags, and the hidden `--remote-control`.
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/cli/src/main.rs#L571-L626
  - Standalone binary: https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server/src/main.rs
- Subcommands: `daemon` (start/stop/restart/bootstrap/update/version), `proxy` (stdio to a unix control socket), `generate-ts` and `generate-json-schema`. Same file, L628-L735.

**Framing**
- stdio uses "newline-delimited JSON (JSONL)".
- WebSocket sends "one JSON-RPC message per WebSocket text frame". A unix socket carries WebSocket over an HTTP Upgrade.
- The protocol is JSON-RPC 2.0 "with the `"jsonrpc":"2.0"` header omitted on the wire". https://learn.chatgpt.com/docs/app-server
- Source confirms it: "We do not do true JSON-RPC 2.0, as we neither send nor expect the "jsonrpc": "2.0" field". https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/rpc.rs#L1-L2
- Request ids can be strings or integers. Requests may carry an optional W3C `trace`.
- Overload error: `-32001`, and the client should retry with backoff (docs).

**Handshake**
- The client sends one `initialize` request per connection, then the `initialized` notification.
  - Requests sent earlier get "Not initialized". A second `initialize` gets "Already initialized" (docs).
- `InitializeParams` contains:
  - `clientInfo {name, title?, version}`
  - `capabilities {experimentalApi, optOutNotificationMethods[], requestAttestation, mcpServerOpenaiFormElicitation, explicitGatewayOauth, extensions}`
- `InitializeResponse` contains `{userAgent, codexHome, platformFamily, platformOs}`.
- https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v1.rs#L29-L84
- Experimental methods and fields fail unless `experimentalApi` is set (docs).

**Session lifecycle (client→server methods, from the macro list)** https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/common.rs#L499-L1510
- Threads:
  - Create and reopen: `thread/start`, `thread/resume`, `thread/fork`.
  - Remove and restore: `thread/archive`, `thread/unarchive`, `thread/delete`, `thread/unsubscribe`.
  - List and read: `thread/list`, `thread/loaded/list`, `thread/read`, `thread/turns/list`, `thread/items/list`.
  - Edit and maintain: `thread/name/set`, `thread/metadata/update`, `thread/compact/start`, `thread/revert` (`thread/rollback` was removed), `thread/inject_items`, `thread/shellCommand`.
  - Goals: `thread/goal/set|get|clear`.
  - Experimental: `thread/queue/*`, `thread/settings/update`, `thread/search`.
- Turns: `turn/start`, `turn/steer`, `turn/interrupt`, plus experimental `turn/settings/update`.
- Also present: `review/start`, `model/list`, `config/read`, `config/value/write`, `config/batchWrite`, `mcpServerStatus/list`, `config/mcpServer/reload`, `mcpServer/tool/call`, `mcpServer/resource/read`, `mcpServer/oauth/login`, `account/*`, `command/exec*`, `fs/*`, `skills/*`, `plugin/*`.

**Key parameters by method**
- `thread/start`:
  - `model`, `modelProvider`, `cwd`, `approvalPolicy`, `sandbox`, `config` (map), `serviceName`, `baseInstructions`, `developerInstructions`, `ephemeral`.
  - Experimental: `environments`, `dynamicTools`, `historyMode`, `permissions`.
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v2/thread.rs#L57-L176
  - The response includes `instructionSources` (paths of the instruction files that were loaded), `sandbox` and `approvalPolicy`. Same file, around L178-L230.
- `thread/resume`:
  - Takes `threadId`, plus experimental `path` (an explicit rollout file) and `history`, plus the same overrides as `thread/start`.
  - On a running thread, the `config`, `baseInstructions` and `developerInstructions` overrides are "ignored while running".
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v2/thread.rs#L340-L420 and https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server/src/request_processors/thread_processor.rs#L214-L227
- `thread/fork`: `threadId`, `lastTurnId`, `ephemeral`, `excludeTurns`; experimental `beforeTurnId`, `path` and `deferGoalContinuation`. thread.rs L534-L626.
- `turn/start`:
  - `threadId`, `input[]` (text, image, localImage), and per-turn overrides that are sticky for later turns: `cwd`, `approvalPolicy`, `sandboxPolicy`, `model`, `effort`, `summary`.
  - Also `outputSchema` (this turn only) and `toolOutput`.
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v2/turn.rs#L167-L290
- `turn/steer`:
  - Takes `{threadId, input, expectedTurnId}` (required precondition) and returns `{turnId}`. turn.rs L308-L335.
  - The docs say it "doesn't emit a new `turn/started`".
- `turn/interrupt`: takes `{threadId, turnId}` and returns `{}`. The turn then ends with `status: "interrupted"`. turn.rs L342-L350 and docs.

**History storage**
- History is stored in rollout JSONL files at `$CODEX_HOME/sessions/YYYY/MM/DD/rollout-<ts>-<id>.jsonl`. Archived files go to `$CODEX_HOME/archived_sessions`.
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/rollout/src/lib.rs#L86-L87
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/rollout/src/recorder.rs#L1740-L1761
- `CODEX_HOME` defaults to `~/.codex`. If set, it must already exist as a directory. https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/utils/home-dir/src/lib.rs#L5-L12
- `Thread.path` is "[UNSTABLE] Path to the thread on disk". https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v2/thread_data.rs#L204-L262
- The SQLite state DB location is configurable with `sqlite_home`.
- **A client cannot choose the storage directory per thread.**
  - `codex_home` is process-wide. Per-request `config` overrides are merged into the CLI overrides of a ConfigBuilder that uses the server's own `codex_home`. https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server/src/config_manager.rs#L428-L470
  - The options are: run one app-server per `CODEX_HOME`, or use the experimental `thread/resume.path` and `thread/fork.path` to read from an explicit rollout file.
  - `experimental_thread_store` is marked "Experimental / do not use", and its schema offers only `local`. https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/config/src/config_toml.rs#L462-L468
- **Ephemeral threads exist.**
  - `thread/start.ephemeral` creates one. `Thread.ephemeral` is documented as "should not be materialized on disk" (thread_data.rs L224-L225).
  - The docs say ephemeral forks are in memory and unlisted, ephemeral root threads cannot be deleted, and `thread/revert` and `includeTurns` are unsupported for them (thread_processor.rs L2151, L3015).
  - `codex exec --ephemeral` also exists.

## 3. v2 notifications, server requests, items and streaming semantics

**Server→client notifications** (full list): https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/common.rs#L1935-L2061
- General: `error`, `warning`, `guardianWarning`, `deprecationNotice`, `configWarning`.
- Thread lifecycle: `thread/started`, `thread/status/changed`, `thread/archived`, `thread/deleted`, `thread/unarchived`, `thread/closed`, `thread/reverted`.
- Thread metadata and state:
  - `thread/name/updated`, `thread/attachment/updated`, `thread/goal/updated`, `thread/goal/cleared`, `thread/tokenUsage/updated`.
  - `thread/compacted` (deprecated; use the `contextCompaction` item instead).
  - Experimental: `thread/prediction/updated`, `thread/queue/changed`, `project/changed`, `thread/project/updated`, `thread/environment/connected`, `thread/environment/disconnected`, `thread/settings/updated`.
- Turn: `turn/started`, `turn/completed`, `turn/diff/updated`, `turn/plan/updated`; experimental `turn/moderationMetadata`.
- Hooks: `hook/started`, `hook/completed`.
- Item lifecycle: `item/started`, `item/completed`, `item/autoApprovalReview/started`, `item/autoApprovalReview/completed`; experimental `autoApprovalReview/strictReviewRequired`.
- Item deltas and progress:
  - `item/agentMessage/delta`.
  - `item/plan/delta` (experimental).
  - `item/reasoning/summaryTextDelta`, `item/reasoning/summaryPartAdded`, `item/reasoning/textDelta`.
  - `item/commandExecution/outputDelta`, `item/commandExecution/terminalInteraction`.
  - `item/fileChange/outputDelta` (deprecated), `item/fileChange/patchUpdated`.
  - `item/mcpToolCall/progress` with `{threadId, turnId, itemId, message}`: https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v2/mcp.rs#L333-L338
- Server requests: `serverRequest/resolved`.
- Internal-only: `rawResponseItem/completed`, `rawResponse/completed`.
- MCP servers: `mcpServer/oauthLogin/completed`, `mcpServer/startupStatus/updated`; experimental `mcpServer/event/stream/notification`.
- Account and model:
  - Account: `account/updated`, `account/login/completed`, `account/gatewayOAuth/changed`, `account/rateLimits/updated`.
  - Model: `model/rerouted`, `model/verification`, `model/safetyBuffering/updated`.
  - Provider auth recovery: `modelProvider/authRecoveryStarted`, `modelProvider/authRecoveryCompleted`.
- Apps and integrations: `app/list/updated`, `remoteControl/status/changed`, `externalAgentConfig/import/progress`, `externalAgentConfig/import/completed`.
- Processes: `command/exec/outputDelta`; experimental `process/outputDelta` and `process/exited`.
- Files and skills: `fs/changed`, `skills/changed`, `fuzzyFileSearch/sessionUpdated`, `fuzzyFileSearch/sessionCompleted`.
- Platform: `windows/worldWritableWarning`, `windowsSandbox/setupCompleted`.
- Realtime (all experimental): `thread/realtime/*`.

**Server→client requests**: https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/common.rs#L1780-L1851
- Approvals: `item/commandExecution/requestApproval`, `item/fileChange/requestApproval`, `item/permissions/requestApproval`.
- User input: `item/tool/requestUserInput` (EXPERIMENTAL; note the `item/` prefix).
- MCP: `mcpServer/elicitation/request` (modes `form`, `openai/form`, `url`).
- Dynamic tools: `item/tool/call`, which executes a client-side dynamic tool.
- Auth and system: `account/chatgptAuthTokens/refresh`, `attestation/generate`, experimental `currentTime/read`.
- Deprecated v1: `ApplyPatchApproval`, `ExecCommandApproval`.
- Command decisions are `accept`, `acceptForSession`, `decline`, `cancel`, or `acceptWithExecpolicyAmendment`.
- Order of an approval: `item/started` → approval request → client decision → `serverRequest/resolved` → `item/completed` (docs).

**ThreadItem variants** (19 tags, also in the generated TS schema):
- https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v2/item.rs#L232-L434
- https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/schema/typescript/v2/ThreadItem.ts
- Messages and context: `userMessage`, `hookPrompt`, `agentMessage` (`text`, `phase?`), `functionCallOutput`, `plan`, `contextCompaction`.
- Reasoning: `reasoning` (`summary[]`, `content[]`).
- Execution and edits: `commandExecution`, `fileChange`.
- Tool calls: `mcpToolCall`, `dynamicToolCall`, `collabAgentToolCall`, `subAgentActivity`.
- Media and search: `webSearch`, `imageView`, `imageGeneration`, `sleep`.
- Review mode: `enteredReviewMode`, `exitedReviewMode`.
- The docs say `collabToolCall`, but the source and schema tag is `collabAgentToolCall`.

**`mcpToolCall` fields**: https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v2/item.rs#L337-L357
- `id`, `server`, `tool`, `status` (`inProgress | completed | failed`, L1211-L1215).
- `arguments` (JSON), `appContext?`, `mcpAppResourceUri?` (legacy), `mcpAppUi?`, `pluginId?`, `readOnlyHint?`.
- `result? {content[], structuredContent?, _meta?}`, `error? {message}` (mcp.rs L232-L250), `durationMs?`.

**`item/started` envelope**
- `{item, threadId, turnId, startedAtMs}`. `item/completed` carries `completedAtMs`. item.rs L1343-L1350 and L1421-L1428.
- The docs call `item/completed` "the authoritative state".

**Streaming of tool-call arguments: none for MCP or function tools**
- The Responses SSE parser explicitly ignores `response.function_call_arguments.delta` and `.done`. It logs "unhandled responses event".
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/codex-api/src/sse/responses.rs#L480-L492
- Only `response.custom_tool_call_input.delta` (freeform custom tools, meaning `apply_patch`) becomes `ToolCallInputDelta` (L366-L376).
  - It is surfaced as `item/fileChange/patchUpdated` only behind feature `apply_patch_streaming_events`, which is `UnderDevelopment` and off by default.
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/features/src/lib.rs#L1273-L1278
  - Test: https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server/tests/suite/v2/turn_start.rs#L4080-L4224
- MCP calls reach `handle_mcp_tool_call(…, arguments: String)` as a complete string, which is parsed before the start notification.
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/core/src/mcp_tool_call.rs#L130-L151
- `item/started(mcpToolCall)` therefore arrives with complete arguments once the call is dispatched.
- The exec docs agree: the MCP item "starts when the invocation is dispatched". https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/exec/src/exec_events.rs#L119-L121

**No cross-item content index**
- Items are correlated by `itemId`/`turnId` and ordered by arrival plus `startedAtMs`/`completedAtMs`.
- `item/agentMessage/delta` carries only `{threadId, turnId, itemId, delta}` (item.rs L1443-L1448).
- Index fields exist only inside reasoning: `summaryIndex` on summaryTextDelta and summaryPartAdded (L1465-L1483), and `contentIndex` on `item/reasoning/textDelta` (L1488-L1495).

**Reasoning**
- The item has `summary[]` (streamed summaries) and `content[]` (raw text). Raw `textDelta` streams "when supported by the model" (docs).
- Config:
  - `model_reasoning_summary` takes `auto|concise|detailed|none`. `turn/start.summary` overrides it per turn.
  - `hide_agent_reasoning` and `show_raw_agent_reasoning` are documented for TUI and exec.
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/config/src/config_toml.rs#L384-L398 and https://learn.chatgpt.com/docs/config-file/config-advanced
- In core, `show_raw_agent_reasoning` gates only the legacy events (`as_legacy_events(show_raw)` in `core/src/session/mod.rs` L2343-L2344).
- The app-server forwards `ReasoningRawContentDelta` without that gate (`app-server/src/bespoke_event_handling.rs` around L1000-L1010). Runtime effect: see UNVERIFIED.

**End of turn and retries**
- A turn ends with `turn/completed`, where `turn.status` is `completed | interrupted | failed`. `inProgress` is the live state.
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v2/turn.rs#L33-L38 and L564-L567
- A failure emits `error` first. Then the turn fails with `{message, codexErrorInfo?, additionalDetails?}`.
- `error` carries `willRetry`: "if the error is transient and the app-server process will automatically retry… this will not interrupt a turn".
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v2/notification.rs#L62-L69
- Provider retries default to `request_max_retries=4` and `stream_max_retries=5` (config-reference).
- **Automatic continuation after a turn exists only for thread goals.** `features.goals` is Stable and on by default (features lib.rs L1824-L1828).
  - With an active goal set via `thread/goal/set`, "normal automatic continuation" starts more turns.
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v2/thread.rs#L620-L626
  - The experimental `thread/queue/*` holds queued submissions.
  - No other auto-continuation was found.

## 4. Replacing instructions and stripping built-in tools

**Instructions**
- **Base instructions can be replaced.** `thread/start`, `thread/resume` and `thread/fork` accept `baseInstructions` and `developerInstructions` (thread.rs L104-L106).
- Config precedence: request/CLI `base_instructions` > `model_instructions_file` > `instructions`.
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/core/src/config/mod.rs#L4011-L4023
  - `model_instructions_file` "will override the built-in instructions for the selected model. Users are STRONGLY DISCOURAGED…" https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/config/src/config_toml.rs#L268-L272
- `experimental_instructions_file` is **not present** at this tag. It does not appear in `codex-rs/core/config.schema.json`.
- Other injected blocks are separately toggleable, all default `true`:
  - `include_permissions_instructions`, `include_apps_instructions`, `include_collaboration_mode_instructions`, `include_environment_context`.
  - config_toml.rs L256-L266 and https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/config/defaults.toml

**AGENTS.md loading**
- `project_doc_max_bytes = 0` stops project AGENTS.md discovery. Untrusted projects also skip project docs.
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/core/src/agents_md.rs#L58-L71 and L125-L135
- The **global** `$CODEX_HOME/AGENTS.override.md` or `AGENTS.md` is loaded by a separate provider, so it is unaffected by that limit.
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/codex-home/src/instructions/mod.rs
  - A dedicated, empty `CODEX_HOME` is needed for a fully clean prompt.
- `thread/start`'s response field `instructionSources` shows which instruction files were loaded.

**Shell tool**
- `[features] shell_tool = false` removes it. The flag is Stable and on by default; the docs describe it as "enables command execution tools".
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/features/src/lib.rs#L1010-L1015
- `add_shell_tools` returns early when the feature is off or the model catalog's `shell_type` is `disabled`.
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/core/src/tools/spec_plan.rs#L1150-L1199

**apply_patch**
- There is **no `include_apply_patch_tool` config key** at this tag.
- `apply_patch` is registered when environment tools are advertised **and** the model catalog entry has `apply_patch_tool_type`. spec_plan.rs L1344-L1347.
- Two possible levers, neither runtime-tested:
  - A custom `model_catalog_json`, applied at startup only (config_toml.rs L400-L402).
  - Experimental `thread/start.environments: []`, which "disables environment access" (thread.rs L135-L144). Without an environment, shell, apply_patch, view_image and request_permissions are not advertised, unless `stable_environment_tools` (UnderDevelopment, off) is enabled.

**Other built-in tools**
- `web_search = "disabled"` (values `disabled|cached|indexed|live`; docs give the default as `cached`).
- `features.view_image` and `features.sleep_tool` are stable and on by default; set them to `false`.
- `update_plan` is **off** unless `[tools.update_plan] enabled = true`. https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/core/src/config/mod.rs#L2722-L2736
- `request_user_input` is **on** unless `[tools.experimental_request_user_input] enabled = false` (same lines).
- MCP resource tools (`list_mcp_resources`, `list_mcp_resource_templates`, `read_mcp_resource`) are added automatically whenever any MCP server is configured. No config switch was found. spec_plan.rs L1202-L1216.

**Sandbox and approvals**
- `sandbox_mode`: `read-only | workspace-write | danger-full-access`.
- `approval_policy`: `on-request | never | {granular=…}`. The docs say `untrusted` is unsupported and `on-failure` is deprecated.
- Sources: `codex-rs/core/config.schema.json` at the tag and https://learn.chatgpt.com/docs/config-file/config-reference

**`[mcp_servers.<name>]` keys** (schema `RawMcpServerConfig`):
- `command`, `args`, `env`, `env_vars`, `cwd`, `enabled`, `required`.
- `enabled_tools`, `disabled_tools`.
- `startup_timeout_sec` / `startup_timeout_ms` (docs default 10 s), `tool_timeout_sec` (docs default 60 s).
- `url`, `bearer_token_env_var`, `http_headers`, `env_http_headers`, `http_headers_helper`, `auth`, `oauth`, `oauth_resource`, `scopes`.
- `default_tools_approval_mode` (`auto|prompt|writes|approve`), `tools.<tool>.approval_mode`.
- `startup_readiness`, `supports_parallel_tool_calls`, `tool_input_schema_max_bytes`, `omit_tools_from`, `environment_id`, `name`.
- A `required` server that fails to initialize makes `thread/start` and `thread/resume` fail (docs).

**Per-thread `config`**
- `thread/start.config` is a `{key: JSON}` map converted to TOML and appended to the CLI `-c` overrides (config_manager.rs L437-L455).
- `model_catalog_json` overrides are accepted per thread but are no-ops.

**Is there a documented way to remove the shell tool entirely?** Yes, `features.shell_tool = false`. There is no equally documented single switch for apply_patch.

## 5. Providers and auth

**Chat Completions is removed**
- `WireApi` has only `Responses`. `wire_api = "chat"` fails deserialization with "`wire_api = "chat"` is no longer supported… set `wire_api = "responses"`".
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/model-provider-info/src/lib.rs#L100-L134
- The deprecation announcement is discussion #7782 (2025-12-09): "Full removal is slated for early February 2026." https://github.com/openai/codex/discussions/7782
- The docs list `responses` as "the only supported value". The legacy `ollama-chat` provider was also removed.

**`model_providers.<id>` fields**: https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/model-provider-info/src/lib.rs#L139-L206
- Endpoint: `name`, `base_url`, `model_catalog_url`, `wire_api`, `capabilities`, `query_params`.
- Auth: `env_key`, `env_key_instructions`, `experimental_bearer_token`, `auth` (command-backed token), `gateway_oauth`, `aws`, `requires_openai_auth`.
- Headers: `http_headers`, `env_http_headers`.
- Retries and timeouts: `request_max_retries`, `stream_max_retries`, `stream_idle_timeout_ms`, `websocket_connect_timeout_ms`.
- Capability flags: `supports_websockets`, `supports_standalone_web_search`.

**Built-in providers**
- `openai`, `amazon-bedrock`, `amazon-bedrock-runtime`, `ollama` (port 11434) and `lmstudio` (port 1234), all on Responses. Same file, L653-L695.
- The IDs `openai`, `ollama` and `lmstudio` are reserved.
- OSS usage: `--oss`, `--local-provider`, and the `oss_provider` config (`lmstudio|ollama`). https://learn.chatgpt.com/docs/config-file/config-advanced

**OpenRouter**
- OpenAI's docs do not mention it (config-advanced has examples for a proxy, Ollama, Mistral and Azure). Repo code search found only a single hit, in a network-proxy test.
- OpenRouter's own tutorial (published 2026-06-17, updated 2026-09-24) configures:
  - `base_url = "https://openrouter.ai/api/v1"`, `wire_api = "responses"`.
  - Auth through an `auth` command (recommended) or `env_key = "OPENROUTER_API_KEY"`.
  - It says `model_providers` work only in user-level `~/.codex/config.toml`.
  - https://openrouter.ai/blog/tutorials/codex-cli-openrouter/
- OpenRouter's Responses endpoint is `https://openrouter.ai/api/v1/responses` and is "stateless". It rejects `store: true` and `previous_response_id` with HTTP 400. https://openrouter.ai/docs/api/reference/responses/overview.md

**Auth**
- `codex login`: ChatGPT browser flow by default; also `--device-auth`, `--with-api-key` (key read from stdin), `--with-access-token`, and `login status`. https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/cli/src/main.rs#L516-L560
- `CODEX_API_KEY` works for a single `codex exec` run. https://learn.chatgpt.com/docs/non-interactive-mode
- App-server `account/login/start` types:
  - `apiKey {apiKey}`, `chatgpt`, `chatgptDeviceCode`.
  - `chatgptAuthTokens` (experimental, internal) and Bedrock (experimental).
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v2/account.rs#L64-L110
- Related methods: `account/read`, `account/logout`, `account/login/cancel`, plus the `account/login/completed` and `account/updated` notifications.
- Custom providers use `env_key`, `auth` or a bearer token. With `requires_openai_auth = false` (the default) the login screen is skipped.

## 6. `codex exec --json`
- The flag is `--json` (alias `--experimental-json`): "Print events to stdout as JSONL". https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/exec/src/cli.rs#L63-L68
- Events: `thread.started`, `turn.started`, `turn.completed` (with usage), `turn.failed`, `item.started`, `item.updated`, `item.completed`, `error`. https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/exec/src/exec_events.rs#L8-L36
- Item types: `agent_message`, `reasoning`, `command_execution`, `file_change`, `mcp_tool_call`, `collab_tool_call`, `web_search`, `todo_list`, `error`. Same file, L104-L132.
- **No deltas.**
  - `agent_message` and `reasoning` are emitted only on `item.completed`. https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/exec/src/event_processor_with_jsonl_output.rs#L343-L366
  - `item.updated` is used only for `todo_list` (L568-L593).
  - Exec now consumes app-server `ServerNotification`s internally (same file).
- **Resume is supported**: `codex exec resume <SESSION_ID>` or `codex exec resume --last "<prompt>"` (cli.rs L174-L175 and docs).
- Other flags: `--ephemeral`, `--output-schema FILE`, `--ignore-user-config`, `--skip-git-repo-check`.

## 7. SDKs and schema generation
- **TypeScript: `@openai/codex-sdk`** (npm 0.162.1, Apache-2.0).
  - It "wraps the `codex` CLI… exchanges JSONL events over stdin/stdout". It spawns `codex exec --experimental-json` (`exec … resume <id>` to resume). It does **not** use app-server.
  - https://github.com/openai/codex/blob/rust-v0.162.1/sdk/typescript/README.md and https://github.com/openai/codex/blob/rust-v0.162.1/sdk/typescript/src/exec.ts#L95
- **Python: `openai-codex`** (PyPI 0.162.1, Apache-2.0, Python ≥3.10).
  - It is a "typed JSON-RPC client for `codex app-server` over stdio". It spawns `app-server --listen stdio://`.
  - It pins the matching runtime `openai-codex-cli-bin`.
  - https://github.com/openai/codex/blob/rust-v0.162.1/sdk/python/src/openai_codex/client.py#L215-L310 and https://github.com/openai/codex/blob/rust-v0.162.1/sdk/python/pyproject.toml
- The app-server docs page recommends "the Codex SDK" for CI and automation.
- **Schema generation** (both `[experimental]`):
  - `codex app-server generate-ts --out DIR [--prettier BIN] [--experimental]`
  - `codex app-server generate-json-schema --out DIR [--experimental]`
  - https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/cli/src/main.rs#L635-L735
  - Output is specific to the Codex version that generated it (docs).
  - Pre-generated copies are in the repo: https://github.com/openai/codex/tree/rust-v0.162.1/codex-rs/app-server-protocol/schema (`json/`, `typescript/`).

## 8. ACP
- **Codex itself has no native ACP.**
  - At `rust-v0.162.1`, the repo tree has no ACP paths, and `codex-rs/Cargo.lock` has no `agent-client-protocol` crate.
  - The CLI subcommand list has no ACP command (cli main.rs L141-L215).
- **`zed-industries/codex-acp` is ARCHIVED.**
  - Last release v0.16.0 (2026-06-08). License Apache-2.0 (Cargo.toml `license = "Apache-2.0"`; LICENSE © Zed Industries).
  - Its README: "Development has moved to agentclientprotocol/codex-acp… built on the new Codex App Server". https://github.com/zed-industries/codex-acp
- **Successor: `agentclientprotocol/codex-acp`.**
  - Latest release **v2.2.2** (2026-10-10, tag commit 202e66e). License Apache-2.0 (LICENSE © JetBrains s.r.o.).
  - npm `@agentclientprotocol/codex-acp` 2.2.2 depends on `@openai/codex ^0.160.1` and `@agentclientprotocol/sdk ^1.5.0`.
  - README: it "starts the Codex App Server, translates ACP requests into Codex operations".
  - https://github.com/agentclientprotocol/codex-acp

---

## UNVERIFIED / could not confirm
1. Whether `show_raw_agent_reasoning` and `hide_agent_reasoning` affect app-server `item/reasoning/*` notifications at runtime. Source reading suggests app-server forwards raw deltas unconditionally and `show_raw` gates only legacy events. Not run.
2. Whether `thread/start.environments: []` (experimental) or a custom `model_catalog_json` (`shell_type: "disabled"`, no `apply_patch_tool_type`) actually yields a tool list with only MCP tools. This is inferred from `spec_plan.rs` and was not tested.
3. Whether stdio MCP servers run inside or outside the Codex sandbox. Not checked.
4. The exact key syntax for `thread/start.config`. Dotted `-c`-style keys are inferred from the code path (`config_manager.rs`), and the docs page does not describe this param.
5. Whether `model_providers` and `model_provider` can be set through per-thread `config` overrides or `-c`. OpenRouter's blog says only user-level `config.toml` works; not tested.
6. End-to-end Codex ↔ OpenRouter Responses compatibility, including whether Codex sends `store`/`previous_response_id` for custom providers. The only source is OpenRouter's third-party tutorial; there is no OpenAI doc and no test.
7. Whether `turn/start` sent while a turn is active steers it. Some field comments ("Ignored when this request steers an already-active turn") suggest so; not confirmed.
8. Full coverage of the docs pages. learn.chatgpt.com pages were partly read through a summarizer, and only key quotes were grep-verified from the raw HTML. The docs show no version or date.
9. Default `web_search` mode. The docs say `cached`; the code default was not checked.
10. Nothing here was executed against a live `codex` binary. All findings come from source at the tag, the docs, and registry metadata.
