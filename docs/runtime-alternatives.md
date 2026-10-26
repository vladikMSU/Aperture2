# Альтернативы Pi: Codex и ACP (исследование, 15 октября 2026)

Исследовательская записка. **Решение о замене Pi не принималось**: Pi остаётся агентным runtime, README и AGENTS.md не меняются. Вопрос записки: можно ли заменить Pi на Codex (`codex app-server` или `codex exec --json`) или подключать движки через ACP (Agent Client Protocol), не ломая текущий UI и MCP-слой.

Маркировка утверждений:

- **[источник]** — прочитано в исходниках или документации указанной версии, ссылка рядом;
- **[spike]** — наблюдалось при запуске [tools/CodexSpike/spike.mjs](../tools/CodexSpike/spike.mjs) на этой машине (раздел 10);
- предположения и открытые вопросы собраны отдельно в разделах 8 и 9 и в выводах не выдаются за факты.

Исходные заметки исследования со всеми ссылками на первоисточники (на английском): [Codex](research/codex-research.md), [ACP](research/acp-research.md).

## Кратко

- **Codex app-server технически подходит как замена Pi RPC без изменения MCP-слоя.** Spike провёл полный цикл с нашим `aperture_mcp`: старт MCP, 10 инструментов, вызов, ошибка инструмента, прерывание, возобновление истории после перезапуска процесса. Базовые инструкции Codex заменяются полностью; shell, `apply_patch`, web search и прочие встроенные инструменты убираются конфигурацией. Остаются три MCP-resource инструмента Codex — выключателя для них не найдено. Результат зависит от метаданных модели (раздел 3.2, п. 3).
- **Цена — новый клиент и часть контракта ленты.** У Codex нет аналога `toolcall_*`: формирование аргументов не транслируется, вызов появляется уже с полными аргументами. Вместо `contentIndex` есть `itemId` и порядок прихода. Нет `message_end` со `stopReason`; повтор HTTP-запроса к провайдеру не виден; текст ошибки провайдера заменяется общим сообщением.
- **OpenRouter через Codex не проверен.** Codex удалил Chat Completions и работает только через Responses API. Pi ходит в `openrouter/free` именно через Chat Completions. OpenRouter описывает Codex только в собственном руководстве; сочетание с free router не проверено.
- **Записи в Excel через Codex по умолчанию требуют подтверждения.** Для MCP-инструмента с `readOnlyHint: false` политика `never` отказывает в вызове, `on-request` присылает клиенту `mcpServer/elicitation/request`. Без запроса к клиенту запись прошла с `default_tools_approval_mode = "approve"` [spike]; режимы `auto`, `writes` и `prompt` не проверялись.
- **`codex app-server` официально помечен экспериментальным** и не поддерживается для production. `codex exec --json` не стримит дельты текста и рассуждений, поэтому не подходит для живой ленты.
- **ACP делает движок сменным, но v1 теряет больше Codex.** Нет конца сообщения, нет индекса блока, нет стрима аргументов; reasoning — неструктурированные chunk-и; конец запроса — только ответ на `session/prompt`. Официального .NET SDK нет. Pi через адаптер `pi-acp` не пробрасывает MCP-серверы клиента и отдельный поток рассуждений.
- **DeepSeek Harness (`dsh`)** — официальный MIT-проект DeepSeek в стадии developer preview. В нём есть встроенный ACP-сервер «только для автоматизации»; провайдеры, кроме DeepSeek, подключаются через `@earendil-works/pi-ai` — LLM-библиотеку Pi.

## 1. Версии и источники

| Компонент | Версия | Источник |
| --- | --- | --- |
| Pi | 1.2.0, submodule `9fe3f371622cad9cc1a3cbf044288abd0252192f` (`v1.2.0-43`) | [json.md](https://github.com/earendil-works/pi/blob/9fe3f371622cad9cc1a3cbf044288abd0252192f/packages/coding-agent/docs/json.md), [rpc.md](https://github.com/earendil-works/pi/blob/9fe3f371622cad9cc1a3cbf044288abd0252192f/packages/coding-agent/docs/rpc.md) |
| Codex, исходники | стабильный `rust-v0.162.1` от 2026-10-09, commit `092d3acd6bec3e3a14bdc7e7a2810ab628ab759d`; npm `@openai/codex` 0.162.1 | [release](https://github.com/openai/codex/releases/tag/rust-v0.162.1) |
| Codex, spike | `codex-cli 0.162.0-alpha.17.2` — бинарник установленного Codex desktop 26.1007.71326 | JSON Schema протокола сгенерирована этой сборкой: `codex app-server generate-json-schema` |
| Документация Codex | без версии и даты; `developers.openai.com/codex/*` перенаправляет сюда | [learn.chatgpt.com/docs/app-server](https://learn.chatgpt.com/docs/app-server) |
| ACP | схема `schema-v1.25.0` от 2026-10-09, commit `4cf3dd858c819fc3ab99ae53f76883ede0345a10`, `protocolVersion: 1`; черновик v2 — `schema-v2.0.0-alpha.8` | [agent-client-protocol](https://github.com/agentclientprotocol/agent-client-protocol), [документация v1](https://agentclientprotocol.com/protocol/v1/transports) |
| Реестр ACP | снимок 2026-10-14, релиз `v2026.10.14-6f6e55b` | [registry.json](https://cdn.agentclientprotocol.com/registry/v1/latest/registry.json) |

Spike выполнен на alpha-сборке из desktop-приложения, а не на стабильном теге. Различия между 0.162.0-alpha.17.2 и 0.162.1 не проверялись. Факты из исходников относятся к `rust-v0.162.1`, наблюдения spike — к alpha-сборке; где они совпали, это отмечено. При любой интеграции spike нужно повторить на закреплённой стабильной версии.

## 2. Что сейчас использует лента Aperture2

Контракт Pi RPC по [json.md](https://github.com/earendil-works/pi/blob/9fe3f371622cad9cc1a3cbf044288abd0252192f/packages/coding-agent/docs/json.md), который потребляют [ChatPaneControl.cs](../src/Aperture.Excel/ChatPaneControl.cs) и `activity()` в [chat.js](../src/Aperture.Excel/Web/chat.js):

- `message_update.assistantMessageEvent`: `text_*`, `thinking_*`, `toolcall_start/delta/end`, у всех есть `contentIndex` внутри сообщения;
- `message_end` — авторитетное сообщение: `content[]`, `stopReason` (`aborted`, `error`), `errorMessage`, `usage`, фактическая модель;
- `tool_execution_start/update/end`, сопоставляются по `toolCallId`: `args`, `partialResult`, `result`, `isError`, `durationMs`;
- `auto_retry_start/end`; `agent_end` не терминален; терминальным считается только `agent_settled {aborted}`;
- команды `prompt` (ответ с `disposition`), `abort`, `get_state`; синтетический `process_exit` создаёт [PiSession.cs](../src/Aperture.Runtime/PiSession.cs).

## 3. Codex

### 3.1 Подтверждено по исходникам и документации (`rust-v0.162.1`)

**Лицензия.** Репозиторий и npm-пакет `@openai/codex` распространяются под Apache-2.0 ([LICENSE](https://github.com/openai/codex/blob/rust-v0.162.1/LICENSE)) [источник].

**Статус app-server.** CLI помечает команду `[experimental]`. В документации app-server и WebSocket-транспорт названы экспериментальными и не поддерживаемыми для production-нагрузки ([app-server](https://learn.chatgpt.com/docs/app-server)) [источник].

**Запуск и транспорт.**
- Команда: `codex app-server --listen stdio://` (значение по умолчанию). Другие варианты: `unix://`, `ws://IP:PORT`, `off` ([main.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/cli/src/main.rs#L571-L626)).
- stdio передаёт JSONL. Протокол — JSON-RPC 2.0 без поля `"jsonrpc"` ([rpc.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/rpc.rs#L1-L2)) [источник, spike].
- Рукопожатие: запрос `initialize {clientInfo, capabilities}`, затем уведомление `initialized`. Ответ на `initialize` содержит `userAgent`, `codexHome` и платформу ([v1.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v1.rs#L29-L84)) [источник, spike].

**Сессия и история.**
- Создание потока: `thread/start` принимает `model`, `modelProvider`, `cwd`, `approvalPolicy`, `sandbox`, `config`, `baseInstructions`, `developerInstructions`, `ephemeral` ([thread.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v2/thread.rs#L57-L176)).
- Запрос: `turn/start {threadId, input[]}`. Управление запросом: `turn/interrupt {threadId, turnId}` и `turn/steer` (с обязательным `expectedTurnId`).
- Работа с историей: `thread/resume`, `thread/fork`, `thread/list`, `thread/read`, `thread/turns/list`, `thread/items/list` ([common.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/common.rs#L499-L1510)).
- История хранится rollout-файлами `$CODEX_HOME/sessions/YYYY/MM/DD/rollout-*.jsonl` ([recorder.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/rollout/src/recorder.rs#L1740-L1761)) [источник, spike].
- `CODEX_HOME` один на процесс, отдельного каталога истории для потока нет ([config_manager.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server/src/config_manager.rs#L428-L470)). Есть `ephemeral`-потоки, которые не сохраняются на диск [источник].

**События.**
- Полный список уведомлений сервера: [common.rs L1935–2061](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/common.rs#L1935-L2061). Для ленты важны:
  - `turn/started`, `turn/completed` (`status`: `completed|interrupted|failed`, `error`);
  - `item/started`, `item/completed` (авторитетное состояние элемента);
  - `item/agentMessage/delta {itemId, delta}`;
  - `item/reasoning/summaryPartAdded`, `item/reasoning/summaryTextDelta {summaryIndex}`, `item/reasoning/textDelta {contentIndex}`;
  - `item/mcpToolCall/progress {message}`;
  - `error {willRetry}`, `thread/tokenUsage/updated`, `mcpServer/startupStatus/updated`, `model/rerouted`, `warning`.
- Запросы сервера к клиенту ([common.rs L1780–1851](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/common.rs#L1780-L1851)):
  - `item/commandExecution/requestApproval`, `item/fileChange/requestApproval`, `item/permissions/requestApproval`;
  - `item/tool/requestUserInput`, `mcpServer/elicitation/request`;
  - `item/tool/call` — инструменты, которые исполняет сам клиент (`dynamicTools`, экспериментально);
  - служебные запросы авторизации и аттестации.
- `ThreadItem` имеет 19 типов, среди них `agentMessage`, `reasoning {summary[], content[]}`, `mcpToolCall`, `commandExecution`, `fileChange`, `webSearch`, `contextCompaction` ([item.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/app-server-protocol/src/protocol/v2/item.rs#L232-L434)).
- Поля `mcpToolCall`: `id`, `server`, `tool`, `status` (`inProgress|completed|failed`), `arguments`, `readOnlyHint`, `result {content, structuredContent, _meta}`, `error {message}`, `durationMs` [источник, spike].
- **Аргументы вызова не стримятся.** SSE-парсер Codex игнорирует `response.function_call_arguments.delta` ([responses.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/codex-api/src/sse/responses.rs#L480-L492)). `item/started` для `mcpToolCall` приходит уже с полными аргументами. Исключение — freeform `apply_patch` за флагом `apply_patch_streaming_events` в статусе «under development» [источник, spike].
- **Общего индекса содержимого нет.** Элементы различаются по `itemId` и идут в порядке прихода. Индексы есть только у дельт рассуждений: `summaryIndex` у кратких сводок и `contentIndex` у сырого текста [источник].
- **Автоматическое продолжение после `turn/completed`** найдено только у целей потока (`features.goals`, включено по умолчанию) [источник].

**Инструкции и инструменты.**
- `baseInstructions` в `thread/start` заменяет встроенные инструкции. Порядок приоритета: запрос, затем `model_instructions_file`, затем `instructions` ([config/mod.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/core/src/config/mod.rs#L4011-L4023)) [источник, spike].
- Отдельно отключаются `include_permissions_instructions`, `include_apps_instructions`, `include_collaboration_mode_instructions` и `include_environment_context` ([config_toml.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/config/src/config_toml.rs#L256-L266)).
- `project_doc_max_bytes = 0` отключает только проектный AGENTS.md. Глобальный `$CODEX_HOME/AGENTS.md` загружается отдельно, поэтому нужен чистый `CODEX_HOME`.
- Shell отключается через `[features] shell_tool = false` ([features](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/features/src/lib.rs#L1010-L1015)).
- Отдельного ключа для `apply_patch` нет: инструмент добавляется, если в каталоге моделей указан `apply_patch_tool_type` ([spec_plan.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/core/src/tools/spec_plan.rs#L1344-L1347)).
- Инструменты `list_mcp_resources`, `list_mcp_resource_templates` и `read_mcp_resource` добавляются всегда, когда настроен хотя бы один MCP-сервер ([spec_plan.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/core/src/tools/spec_plan.rs#L1202-L1216)) [источник, spike].
- Ключи `[mcp_servers.<имя>]`: `command`, `args`, `env`, `cwd`, `required`, `enabled_tools`, `disabled_tools`, `startup_timeout_sec`, `tool_timeout_sec`, `default_tools_approval_mode` (`auto|prompt|writes|approve`), `tools.<имя>.approval_mode` [источник, spike].
- Песочница: `read-only|workspace-write|danger-full-access`. Политика подтверждений: `on-request|never|granular`.

**Провайдеры и авторизация.**
- **Chat Completions удалён.** `wire_api = "chat"` даёт ошибку конфигурации, остаётся только `responses` ([model-provider-info](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/model-provider-info/src/lib.rs#L100-L134), [discussion #7782](https://github.com/openai/codex/discussions/7782)) [источник, spike].
- Встроенные провайдеры: `openai`, `amazon-bedrock`, `ollama`, `lmstudio`. Пользовательский провайдер задаётся через `[model_providers.<id>]`: `base_url`, `env_key` или `auth` (команда, выдающая токен), заголовки, лимиты повторов.
- OpenRouter в документации OpenAI не упоминается. [Руководство самого OpenRouter](https://openrouter.ai/blog/tutorials/codex-cli-openrouter/) (обновлено 2026-09-24) настраивает `base_url = "https://openrouter.ai/api/v1"`, `wire_api = "responses"` и `env_key` или `auth`.
- Responses API OpenRouter не хранит состояние и отвечает HTTP 400 на `store: true` и `previous_response_id` ([OpenRouter Responses](https://openrouter.ai/docs/api/reference/responses/overview)) [источник].
- Pi ходит в `openrouter/free` через `openai-completions` (`external/pi/packages/ai/src/providers/data/openrouter.json`) [источник].
- Авторизация: `codex login` (ChatGPT, device code, `--with-api-key`) или `account/login/start` в app-server. Для пользовательских провайдеров — `env_key` или `auth`.

**`codex exec --json`.**
- События: `thread.started`, `turn.started`, `turn.completed`, `turn.failed`, `item.started`, `item.updated`, `item.completed`, `error`.
- Текст и рассуждения приходят только в `item.completed`; `item.updated` используется лишь для `todo_list` ([event_processor_with_jsonl_output.rs](https://github.com/openai/codex/blob/rust-v0.162.1/codex-rs/exec/src/event_processor_with_jsonl_output.rs#L343-L366)) [источник].
- Есть `codex exec resume <id>`.
- TypeScript SDK `@openai/codex-sdk` работает поверх `exec`, Python SDK `openai-codex` — поверх `app-server`.

**ACP.** Встроенной поддержки в Codex нет. Адаптер — [agentclientprotocol/codex-acp](https://github.com/agentclientprotocol/codex-acp) v2.2.2 (Apache-2.0, зависит от `@openai/codex ^0.160.1`); он сам запускает app-server [источник].

### 3.2 Результаты spike (`codex-cli 0.162.0-alpha.17.2`)

**Как устроен spike.**
- Каждому сценарию — отдельный чистый `CODEX_HOME`.
- Модель заменена локальным mock-сервером Responses API на `127.0.0.1`, поэтому ключи не нужны. Mock сохраняет тела запросов и показывает, что Codex реально отправляет модели.
- Наш `aperture_mcp` запускается с фиктивной привязкой. Любой вызов Excel отклоняется проверкой окна и PID до обращения к COM, поэтому живой Excel не затрагивается.

1. **MCP-слой не менялся.**
   - Codex запустил `python -m aperture_mcp --context …` из `[mcp_servers.aperture_excel]`; `mcpServerStatus/list` вернул все 10 инструментов.
   - Вызов `excel_target` дошёл до нашего сервера и вернул его собственную ошибку привязки.
   - После закрытия stdin app-server завершился, осиротевших процессов `aperture_mcp` не осталось (проверено по списку процессов).

2. **Минимальный профиль.**
   - Использованы ключи из раздела 3.1, все приняты `--strict-config`:
     - `include_*` = false, `project_doc_max_bytes = 0`, `web_search = "disabled"`;
     - `[skills] include_instructions = false`, `[skills.bundled] enabled = false`;
     - `[tools.experimental_request_user_input] enabled = false`;
     - 24 флага `[features]`, включая `shell_tool`, `unified_exec`, `view_image`, `multi_agent`, `goals`, `memories`, `apps`, `plugins`.
   - Модели ушли только наш `baseInstructions` первым `developer`-сообщением и сообщение пользователя. Поля `instructions` в запросе нет.
   - Инструменты: три MCP-resource инструмента Codex и 10 наших, в пространстве имён `mcp__aperture_excel`.
   - Для сравнения, без минимальной конфигурации и `baseInstructions` Codex отправил:
     - системный промпт Codex CLI (17 174 символа), инструкции skills (~2,5 тыс.) и `environment_context` с cwd, shell, датой и часовым поясом;
     - 16 инструментов: `exec_command`, `write_stdin`, `view_image`, `multi_agent_v1/*`, инструменты целей и `web_search`.

3. **Результат зависит от метаданных модели.**
   - Для неизвестного каталогу идентификатора (mock, а также `gpt-5.2-codex`, которого нет во встроенном каталоге этой сборки) Codex предупреждает о fallback-метаданных; `apply_patch` при этом не появляется.
   - Для `gpt-6-astra` из каталога Codex включает code mode. При выключенном `code_mode_host` модели не ушло ни одного инструмента, а в промпт добавились инструкции multi-agent, хотя `multi_agent = false`.
   - Минимальный профиль проверен только для fallback-метаданных.

4. **Порядок событий при вызове инструмента:**
   - `thread/started` → `mcpServer/startupStatus/updated` → `turn/started`;
   - `item/started/completed(userMessage)`;
   - `item/started(reasoning)` → `summaryPartAdded` → `summaryTextDelta ×3` → `item/completed(reasoning)`;
   - `item/started(agentMessage)` → `agentMessage/delta ×3` → `item/completed(agentMessage)`;
   - `item/started(mcpToolCall, inProgress)` → `item/completed(mcpToolCall, failed)`;
   - `thread/tokenUsage/updated`;
   - второе `agentMessage` с дельтами → `turn/completed(completed)`.

   Промежуточный текст и итоговый ответ — разные элементы, их порядок сохраняется.

5. **Стрима аргументов нет.** Mock отправил два `response.function_call_arguments.delta`; клиенту не пришло ничего, а `item/started` уже содержал полные `arguments`. Это совпадает с исходниками.

6. **Подтверждение записи.** `excel_write_values` помечен `readOnlyHint: false`:

   | Настройка | Что произошло |
   | --- | --- |
   | `approvalPolicy: "never"` | Вызов не выполнен: `failed`, Codex сообщил, что вызову нужно подтверждение, а политика `never` его не допускает |
   | `"on-request"`, клиент отклонил | Пришёл `mcpServer/elicitation/request` (`mode: form`, `_meta.codex_approval_kind: mcp_tool_call`, параметры инструмента); вызов отклонён |
   | `"on-request"`, клиент принял (`action: accept`) | Вызов выполнен и дошёл до MCP; ошибка привязки — ожидаемо для фиктивной цели |
   | `"never"` и `default_tools_approval_mode = "approve"` | Вызов выполнен без запроса к клиенту |

7. **Гонка старта MCP.** Если вызвать `turn/start` сразу после `thread/start`, при `required = false` первый запрос к модели ушёл **без инструментов Excel**: сервер ещё стартовал. С `required = true` инструменты были на месте. Клиенту нужно либо `required = true`, либо ждать `mcpServer/startupStatus/updated: ready`.

8. **Ошибка провайдера.**
   - Сценарий: HTTP 500 при `request_max_retries = 1`.
   - Было два HTTP-запроса, но повтор никак не отразился в событиях. Затем пришли `error(willRetry=false)` и `turn/completed(failed)` с `codexErrorInfo: internalServerError`.
   - Текст ошибки от mock не показан: Codex подставил общее сообщение о высокой нагрузке.

9. **Остановка.**
   - `turn/interrupt` привёл к `turn/completed(interrupted)` через 34–39 мс.
   - Начатый `agentMessage` так и не получил `item/completed`, поэтому открытые элементы клиенту придётся закрывать по `turn/completed`.

10. **Возобновление.** Во втором процессе с тем же `CODEX_HOME` выполнен `thread/resume` по ID. Следующий запрос к модели содержал наши базовые инструкции, прежние сообщения пользователя и ассистента и новый вопрос. Повторно передавать `baseInstructions` не потребовалось.

11. **Прочее.**
    - Без `--strict-config` процесс стартует и с недопустимым `config.toml` (например, `wire_api = "chat"`), записав в stderr «Invalid configuration; using defaults». В одном прогоне ошибка типа в `config.toml` проявилась только ответом на `thread/start` (`failed to load configuration`). С `--strict-config` недопустимый или неизвестный ключ останавливает запуск сразу; spike запускается с этим флагом.
    - `remoteControl/status/changed` сообщает имя компьютера и `installationId`.
    - `account/rateLimits/updated` приходит даже с пользовательским провайдером.
    - `turn/completed.turn.items` содержит только итоговый `agentMessage`; полный ход нужно собирать из потока событий или запрашивать через `thread/items/list`.
    - `thread/read` с `includeTurns` вызывает `deprecationNotice`.

## 4. ACP

### 4.1 Протокол (`schema-v1.25.0`)

**Транспорт и рукопожатие.**
- Лицензия Apache-2.0. JSON-RPC 2.0 в UTF-8 поверх stdio, сообщения разделены переводом строки. Клиент сам запускает агента дочерним процессом ([transports](https://agentclientprotocol.com/protocol/v1/transports)). HTTP/WebSocket-транспорт пока только на стадии RFD [источник].
- `initialize`: стороны обмениваются `clientCapabilities` (`fs`, `terminal`, `elicitation`, `session.*`) и `agentCapabilities` (`loadSession`, `promptCapabilities`, `mcpCapabilities {http, sse}`, `sessionCapabilities`). Авторизация — `authMethods` и `authenticate`.

**Сессии.**
- `session/new {cwd, mcpServers}`. Stdio-запись MCP: `{name, command (абсолютный путь), args, env: [{name, value}]}`. Поддержка stdio MCP обязательна для любого агента, поэтому **MCP-конфигурация переносима между движками** [источник].
- Стабильные методы: `session/prompt`, `session/cancel` (уведомление), `session/load` (с воспроизведением истории), `session/resume` (без воспроизведения), `session/list`, `session/close`, `session/delete`, `session/set_config_option` (категории `mode`, `model`, `model_config`, `thought_level`). `session/fork` нестабилен; `session/set_model` удалён.
- Ответ на `session/prompt` — `{stopReason}`: `end_turn|max_tokens|max_turn_requests|refusal|cancelled`. Других событий конца запроса в v1 нет.

**Типы `session/update` (14 стабильных).**
- `user_message_chunk`, `agent_message_chunk`, `agent_thought_chunk`;
- `tool_call`, `tool_call_update`, `plan`, `available_commands_update`, `current_mode_update`;
- `config_option_update`, `session_info_update`, `usage_update`, `notice`, `compaction_update`, `compaction_summary_chunk`.

**Вызовы инструментов.**
- `tool_call`: обязательны `toolCallId` и `title`; дополнительно `name`, `kind`, `status` (`pending|in_progress|completed|failed`), `content[]`, `locations[]`, `rawInput`, `rawOutput`.
- `tool_call_update` работает как патч.
- **Аргументы не стримятся** ни в v1, ни в черновике v2: есть только снимки `rawInput` и `title`. Спецификация лишь допускает, что статус `pending` покрывает ещё формирующийся ввод. В адаптере `claude-agent-acp` v0.90.0, по его тесту, во время стрима обновляется только `title`, а `rawInput` приходит целиком.

**Сообщения.**
- У chunk-ов есть необязательный `messageId` (стабилен с 2026-06-05). Индекса блока нет, события «сообщение завершено» нет.
- В черновике v2 `messageId` обязателен, и добавлено `state_update` (`running`, `idle` + `stopReason`).

**Подтверждения и SDK.**
- Подтверждение: `session/request_permission` с вариантами `allow_once|allow_always|reject_once|reject_always`.
- Официальные SDK: TypeScript, Rust, Python, Kotlin, Java. **Официального .NET SDK нет.** Из найденных community-пакетов на .NET Framework 4.8 работает только `dotacp` (net472/netstandard2.0, Apache-2.0); он сгенерирован по схеме 1.19.0.
- Среди стабильных методов нет замены системного промпта, белого списка встроенных инструментов движка и выбора провайдера, кроме `set_config_option` для модели. Всё это остаётся настройкой конкретного движка.

### 4.2 Поддержка ACP движками

Проверено по репозиториям, npm и реестру ACP 14 октября 2026. Ни один движок через ACP здесь не запускался.

| Движок | ACP | Запуск | Лицензия | Версия | Примечание |
| --- | --- | --- | --- | --- | --- |
| Pi | Адаптер [svkozak/pi-acp](https://github.com/svkozak/pi-acp) | `npx pi-acp` → `pi --mode rpc` | MIT / MIT | Pi 1.2.0; pi-acp 0.0.34 (2026-09-24) | Автор Pi отклонил встроенный ACP (PR #241, #836). README pi-acp: MCP-серверы из ACP сохраняются, но в Pi не передаются; рассуждения не выделены в отдельный поток |
| Codex | Адаптер [agentclientprotocol/codex-acp](https://github.com/agentclientprotocol/codex-acp) | `npx @agentclientprotocol/codex-acp` | Apache-2.0 / Apache-2.0 | 2.2.2 (2026-10-10) | Работает поверх app-server; старый `zed-industries/codex-acp` архивирован |
| Claude Code | Адаптер [agentclientprotocol/claude-agent-acp](https://github.com/agentclientprotocol/claude-agent-acp) | `npx @agentclientprotocol/claude-agent-acp` | Адаптер Apache-2.0; Claude Agent SDK проприетарный | 0.90.0 (2026-10-13) | Работает только с моделями Anthropic; ранее исключён из рассмотрения |
| Gemini CLI | Встроенный | `gemini --acp` | Apache-2.0 | 0.64.0 (2026-10-13) | `--experimental-acp` устарел |
| Goose | Встроенный | `goose acp` | Apache-2.0 | 1.54.0 (2026-10-08) | Репозиторий переехал в `aaif-goose/goose` |
| OpenCode | Встроенный | `opencode acp [--cwd]` | MIT | 1.19.0 (2026-10-11) | `anomalyco/opencode` |
| DeepSeek Harness (`dsh`) | Встроенный, профиль «automation-only» | `dsh --profile acp`; в README приведён только запуск из исходников (`pnpm dsh --profile acp`) | MIT | `dsh-v0.2.1-alpha.3` (2026-10-14); npm `@deepseek-ai/dsh` 0.2.0-rc.2 | Официальный, [deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness), developer preview, репозиторий создан 2026-08-13. Через ACP: `session/list`, `resume`, `close`, stdio/HTTP MCP, выбор модели и `reasoning_effort`. Нет `session/load`, режимов, планов, терминалов и `fs/*`. Провайдеры: DeepSeek напрямую, остальные через `@earendil-works/pi-ai` ^1.1.0, а также OpenAI- и Anthropic-совместимые шлюзы. В реестре ACP отсутствует |

Неофициальные DeepSeek-инструменты:
- [Reasonix](https://github.com/esengine/DeepSeek-Reasonix) (MIT): README заявляет `reasonix acp`.
- [Deep Code](https://github.com/lessweb/deepcode-cli) (MIT): поддерживает MCP, ACP не упоминается.

В реестре ACP 41 агент, на странице agents сайта — 43, в том числе Cursor, Copilot, Kimi, Qwen Code и Cline.

## 5. Соответствие событий Pi, Codex и ACP

| Смысл | Pi RPC 1.2.0 | Codex app-server | ACP v1 (1.25.0) |
| --- | --- | --- | --- |
| Запрос принят | ответ на `prompt` (`disposition`), `agent_start` | ответ на `turn/start` (`turn.id`), `turn/started` | отдельного события нет; запрос — это `session/prompt` |
| Шаг агента (ответ модели и его вызовы) | `turn_start` / `turn_end` | отдельного события нет; после каждого запроса к модели приходит `thread/tokenUsage/updated` [spike] | нет |
| Начало сообщения | `message_start` | `item/started` для каждого элемента (`agentMessage`, `reasoning`) | новый `messageId` у chunk (необязателен) |
| Текст | `text_start/delta/end` + `contentIndex` | `item/agentMessage/delta {itemId}`, итог в `item/completed` | `agent_message_chunk {content, messageId?}`, конца нет |
| Рассуждение | `thinking_start/delta/end` + `contentIndex`, `redacted` | `summaryPartAdded`, `summaryTextDelta {summaryIndex}`, `textDelta {contentIndex}`, итог `reasoning {summary[], content[]}` | `agent_thought_chunk` (без разделения на сводку и сырой текст) |
| Порядок блоков | `contentIndex` внутри сообщения | `itemId`, порядок прихода, `startedAtMs` | только порядок прихода и `messageId` |
| Формирование вызова | `toolcall_start {id, toolName}`, `toolcall_delta`, `toolcall_end {toolCall}` | **нет** | `tool_call` со `status: pending` возможен до полного ввода, но без дельт; поведение зависит от адаптера |
| ID вызова | `toolCallId` / `toolCall.id` | `item.id` у `mcpToolCall` (равен `call_id` модели [spike]) | `toolCallId` |
| Начало исполнения | `tool_execution_start {args}` | `item/started(mcpToolCall, inProgress, arguments)` | `tool_call` / `tool_call_update` со `status: in_progress` |
| Промежуточный результат | `tool_execution_update {partialResult}` | `item/mcpToolCall/progress {message}` (только текст) | `tool_call_update {content}` |
| Результат | `tool_execution_end {result, isError, durationMs}` | `item/completed(mcpToolCall)`: `status`, `result`, `error`, `durationMs` | `tool_call_update {status: completed/failed, content, rawOutput}` |
| Подтверждение | встроенного подтверждения вызовов нет ([security.md](https://github.com/earendil-works/pi/blob/9fe3f371622cad9cc1a3cbf044288abd0252192f/packages/coding-agent/docs/security.md)); расширение может спросить через `extension_ui_request`, панель такие диалоги отменяет | `mcpServer/elicitation/request`, `item/*/requestApproval` | `session/request_permission` |
| Конец сообщения | `message_end`: авторитетный `content`, `stopReason`, `errorMessage`, `usage` | `item/completed` по каждому элементу; `stopReason` у сообщения нет | нет |
| Повтор провайдера | `auto_retry_start/end` | `error {willRetry: true}` по исходникам; HTTP-повтор в spike ничего не выдал | нет |
| Ошибка | `message_end.stopReason: error`, `auto_retry_end {success: false}` | `error {willRetry: false}`, затем `turn/completed(failed)` с `turn.error {message, codexErrorInfo}` | ошибка JSON-RPC в ответ на `session/prompt`; `notice` при наличии capability |
| Остановка | команда `abort` → `stopReason: aborted` → `agent_settled {aborted: true}` | `turn/interrupt` → `turn/completed(interrupted)` | `session/cancel` → `stopReason: cancelled` |
| Конец всего запроса | `agent_settled` (`agent_end` не терминален) | `turn/completed`; автопродолжение только у goals | ответ на `session/prompt` |
| Токены | `usage` в `message_update` и `message_end` | `thread/tokenUsage/updated` | `usage_update {used, size, cost?}` |
| Фактическая модель | модель ответа в `message_end` | `model/rerouted` (для OpenRouter не проверено) | нет |
| Сжатие контекста | `compaction_start/end` | элемент `contextCompaction` | `compaction_update` (нужна capability) |
| Процесс завершился | `process_exit` (синтетический, из `PiSession`) | так же, по закрытию stdout | так же |

## 6. Что теряет лента

**При переходе на Codex app-server:**
- Нет аналога `toolcall_*`. Требование AGENTS.md показывать формирование аргументов до исполнения выполнить нельзя: карточка вызова появится сразу в состоянии «выполняется» и с полными аргументами.
- `contentIndex` заменяется на `itemId` и порядок прихода. Каждый элемент Codex — отдельный блок ленты, промежуточный текст и итоговый ответ приходят разными `agentMessage` [spike].
- Нет `message_end` со `stopReason` и `errorMessage`. Остановка и ошибка видны только на уровне запроса (`turn/completed`), а незавершённые элементы нужно закрывать самостоятельно [spike].
- HTTP-повторы к провайдеру не видны; исходный текст ошибки провайдера заменяется общим сообщением [spike]. Для free router это ухудшит диагностику по сравнению с Pi.
- Рассуждения приходят сводкой и/или сырым текстом. Что реально пришлёт OpenRouter через Responses, не проверено.

**При переходе на ACP v1** теряется всё перечисленное, и дополнительно:
- нет конца отдельного сообщения и элемента рассуждения;
- `messageId` необязателен;
- нет индекса и структуры рассуждений;
- полнота событий зависит от конкретного адаптера.

Хронологическую ленту можно собрать по порядку прихода `session/update`, но точность границ ниже, чем у Pi.

## 7. Объём замены в коде Aperture2

| Компонент | Pi сейчас | Codex app-server | ACP-клиент |
| --- | --- | --- | --- |
| `src/aperture_mcp`, `binding.py`, COM-привязка, `tests/backend` | — | Без изменений кода [spike]. Нужны `required = true` и политика подтверждения записи (`default_tools_approval_mode` или ответы на elicitation) | Без изменений кода; MCP передаётся в `session/new` |
| [SessionConfiguration.cs](../src/Aperture.Runtime/SessionConfiguration.cs) | `.pi/mcp.json`, `--system-prompt`, `--no-builtin-tools`, `--tools mcp__aperture_excel__*` | Переписать: чистый `CODEX_HOME` с `config.toml` (провайдер, MCP, `[features]`, `include_*`), `baseInstructions` в `thread/start`, запуск `codex app-server --strict-config` | Генерировать `session/new`. Промпт, инструменты и провайдер — в конфигурации конкретного движка |
| [PiSession.cs](../src/Aperture.Runtime/PiSession.cs) и [pi-launcher.mjs](../src/Aperture.Runtime/pi-launcher.mjs) | JSONL `type`/`id`, ответы `response` | Новый JSON-RPC: `initialize`/`initialized`, корреляция `id` → `result`/`error`, ответы на запросы сервера (elicitation). Шлюз запуска на Node не подходит к нативному `codex.exe`; привязку к Job Object нужно решить иначе | Новый JSON-RPC-клиент ACP или `dotacp`; та же проблема Job Object для каждого движка |
| [Contracts.cs](../src/Aperture.Runtime/Contracts.cs), [PaneSettings.cs](../src/Aperture.Excel/PaneSettings.cs), [SettingsDialog.cs](../src/Aperture.Excel/SettingsDialog.cs) | Provider/Model в терминах Pi | Провайдер Codex (`[model_providers]`) и модель; путь к `codex.exe` вместо Node | Команда движка и `configOptions` |
| [ChatPaneControl.cs](../src/Aperture.Excel/ChatPaneControl.cs) | `message_end`, `agent_settled`, `auto_retry_*`; `abort` + `get_state`; `disposition: handled` | Терминальное событие — `turn/completed`; Stop — `turn/interrupt`; подтверждения записи в UI; ожидание готовности MCP | Терминальное событие — ответ `session/prompt`; Stop — `session/cancel`; `session/request_permission` |
| [chat.js](../src/Aperture.Excel/Web/chat.js), `activity()` | Сырые события Pi, части по `contentIndex` | Либо адаптер Codex → Pi-подобные события в C# (фаза формирования просто не наступает), либо собственный обработчик по `itemId` | Обработчик `session/update` по `toolCallId` и `messageId` |
| История | `--session-dir` на чат | `CODEX_HOME/sessions`, `thread/resume` [spike] | `session/load` или `resume`, если движок поддерживает |
| Логин | `/login openrouter` в Pi, `%USERPROFILE%\.pi\agent` | `env_key`, `auth`-команда или `codex login`; учётные данные живут в `CODEX_HOME`, что конфликтует с отдельным `CODEX_HOME` на чат | У каждого движка свой (`authMethods`) |
| Сборка | [Aperture.Dependencies.targets](../build/Aperture.Dependencies.targets) собирает Pi из submodule | Закрепить `@openai/codex` (Apache-2.0) с платформенным бинарником | Закрепить адаптер и движок |
| Тесты и smoke | `tests/runtime/protocol-fixture.mjs`, `tests/web/chat-view.cjs`, `PaneLifecycleHarness`, `tools/PiSmoke`, `tools/LocalSmoke` (Pi-часть), `aperture-status.mjs` | Переписать фикстуры и Pi-проверки; `/aperture-status` заменяется `mcpServerStatus/list` | Переписать под ACP; добавить фикстуру движка |

Без изменений при любом варианте остаются WebView2-оболочка, Markdown-рендер, Ribbon и привязка книги в VSTO ([WorkbookBinding.cs](../src/Aperture.Excel/WorkbookBinding.cs)), а также внешний COM-backend.

## 8. Предположения (не проверены)

- OpenRouter-идентификаторы моделей не входят во встроенный каталог Codex. Тогда они получат fallback-метаданные, и минимальный профиль поведёт себя как в spike: без `apply_patch` и code mode.
- Codex совместим с Responses API OpenRouter, в том числе с `include: ["reasoning.encrypted_content"]`, `prompt_cache_key` и developer-сообщениями. Codex отправляет `store: false` и не отправляет `previous_response_id` [spike, mock], но реальный OpenRouter не проверялся.
- Адаптер «Codex → Pi-подобные события» в C# позволит почти не менять `chat.js`. Это вывод по структуре кода, не прототип.
- Для хронологической ленты на Codex порядка прихода `item/started` достаточно, потому что элементы не перемежаются внутри одного сообщения.
- `codex.exe`, запущенный по образцу `ChildProcessJob`, получит Job Object до запуска MCP: MCP стартует только после `thread/start`. Это рассуждение по наблюдаемому порядку событий; другие вспомогательные процессы Codex не проверялись.

## 9. Открытые вопросы

Для пользователя:
- Допустима ли зависимость от компонента, который сам OpenAI помечает экспериментальным и не поддерживаемым для production?
- Важна ли фаза «формирование аргументов» настолько, чтобы считать её отсутствие блокером? Сейчас это требование AGENTS.md.
- Нужен ли free router OpenRouter как основной вариант, если Codex поддерживает только Responses API?
- Какая политика записи нужна при Codex: `default_tools_approval_mode = "approve"`, как сейчас в Pi (вызовы выполняются без подтверждения), или подтверждения через elicitation в панели?
- Рассматривать ли ACP как способ сделать движок сменным, понимая, что ACP v1 беднее текущего контракта ленты, а Pi через `pi-acp` теряет MCP-проброс и рассуждения?

Для проверки:
- Повторить spike на стабильном `@openai/codex` 0.162.1 вместо alpha из desktop-приложения.
- Проверить реальный запрос к OpenRouter через Codex: free router и одну закреплённую модель с tool calling, наличие reasoning, текст ошибок.
- Проверить живой Excel через Codex: чтение, запись, пересчёт, повторное чтение на тестовых книгах `tools/LocalSmoke`. Неясно, работает ли COM из MCP-процесса, запущенного Codex при `sandbox: read-only` на Windows; в spike COM не вызывался.
- Как хранить авторизацию Codex при отдельном `CODEX_HOME` на чат: общий `CODEX_HOME` с потоковыми `config`-переопределениями (синтаксис не проверен) или ключ через `env_key`/`auth`.
- Выдаёт ли Codex `error {willRetry: true}` при повторе потока (`stream_max_retries`), а не только HTTP-запроса.
- Как ведут себя адаптеры ACP (`codex-acp`, `pi-acp`, `dsh`) на практике: `messageId`, `rawInput` до исполнения, `usage_update`. Проверены только README и один тест `claude-agent-acp`.
- Насколько `dotacp` годится для .NET Framework 4.8 и устроит ли схема 1.19.0, или проще свой небольшой JSON-RPC-клиент.

## 10. Как повторить spike

[tools/CodexSpike/spike.mjs](../tools/CodexSpike/spike.mjs) — отдельный Node-скрипт без зависимостей. Панель и поведение по умолчанию он не затрагивает. Нужен собранный `.venv` (сборка решения).

```powershell
node tools\CodexSpike\spike.mjs --codex <путь к codex.exe> [--scenario all]
```

Параметры:
- `--python` — путь к Python (по умолчанию `.venv\Scripts\python.exe`);
- `--context` — файл привязки (по умолчанию фиктивная привязка без доступа к Excel). С реальной привязкой сценарий `write` пропускается, остальные вызывают только `excel_target`. Для живой проверки использовать только тестовые книги, не пользовательские;
- `--model` — идентификатор модели для метаданных Codex (по умолчанию `aperture-mock`);
- `--out` — каталог результатов (по умолчанию `.local\codex-spike\<время>`, вне Git).

Сценарии:
- `tool` — рассуждение, текст и вызов MCP;
- `write` — четыре варианта подтверждения записи;
- `race` — старт MCP без ожидания;
- `defaults` — профиль Codex без ограничений;
- `error` — HTTP 500;
- `interrupt` — остановка;
- `resume` — возобновление после перезапуска процесса.

В каталоге результатов для каждого сценария лежат:
- `events.jsonl` — все сообщения app-server;
- `provider-requests.json` — что Codex отправил модели;
- `codex-home` — rollout-файлы;
- общий `summary.json`.

Ключи провайдеров скрипт не читает и удаляет `OPENAI_*`, `CODEX_API_KEY` и `OPENROUTER_*` из окружения Codex. Пользовательский `~/.codex` не используется.
