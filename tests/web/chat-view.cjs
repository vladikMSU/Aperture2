// Browser-only regression for the actual local HTML/CSS/JS. The bridge and audit
// findings below are synthetic fixtures, not results from Excel or an LLM.
const { chromium } = require('playwright');
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');

const root = path.resolve(__dirname, '../..');
const output = path.join(root, 'work');
const screenshots = ['chat-webview-welcome.png', 'chat-webview-conversation.png', 'chat-webview-narrow.png', 'chat-webview-streaming.png', 'chat-webview-all-tools.png'];
let checks = 0;
function check(actual, expected, message) {
  assert.deepStrictEqual(actual, expected, message);
  checks++;
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const launch = process.env.APERTURE_BROWSER_EXECUTABLE
    ? { executablePath: process.env.APERTURE_BROWSER_EXECUTABLE }
    : { channel: 'msedge' };
  const browser = await chromium.launch({ ...launch, headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 410, height: 850 }, deviceScaleFactor: 1 });
    page.setDefaultTimeout(10000);
    const errors = [], network = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('request', request => { if (/^https?:/.test(request.url())) network.push(request.url()); });
    await page.addInitScript(() => {
      window.apertureSent = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        postMessage: message => window.apertureSent.push(message),
        addEventListener: (name, callback) => { window.receiveAperture = message => callback({ data: message }); }
      };
    });
    await page.goto(pathToFileURL(path.join(root, 'src/Aperture.Excel/Web/chat.html')).href);
    const receive = message => page.evaluate(message => window.receiveAperture(message), message);
    await receive({ type: 'binding', name: 'Финансовая модель · 2026.xlsx' });
    await receive({ type: 'status', text: 'Книга привязана · готова к работе', error: false });
    await page.screenshot({ path: path.join(output, screenshots[0]) });
    check(await page.locator('#send').isDisabled(), true, 'Empty prompt must disable Send.');
    await page.locator('[data-prompt]').first().click();
    await page.locator('#prompt').press('Control+Enter');
    check(await page.evaluate(() => window.apertureSent.at(-1).command), 'send', 'Ctrl+Enter must emit Send.');
    check((await page.evaluate(() => window.apertureSent.at(-1).text)).includes('структуру'), true, 'Suggestion text must reach the bridge.');
    await receive({ type: 'clear_prompt' });
    await receive({ type: 'message', who: 'Вы', text: 'Проверь финансовую модель: где расчёты требуют внимания?' });
    // Deliberately fictional audit text exercises Markdown and table rendering only.
    await receive({ type: 'assistant_delta', text: 'Я проверила структуру и ключевые связи. Есть **два участка**, которые стоит разобрать.\n\n### Что проверить\n\n| Ячейка | Наблюдение |\n| --- | --- |\n| `DCF!F24` | Дисконтирование начинается со второго периода |\n| `ОП!H87` | Ссылка на прошлогоднюю выручку |\n\nЭто предварительные наблюдения. Сопоставлю их с допущениями модели, прежде чем делать выводы.\n\n- Исходные расчёты сохранены.\n- Проверяю формулы и их зависимости.' });
    await receive({ type: 'assistant_end' });
    await receive({ type: 'status', text: 'Проверка завершена', error: false });
    await page.screenshot({ path: path.join(output, screenshots[1]) });
    check(await page.locator('.message-body table').count(), 1, 'Completed assistant text must render a Markdown table.');
    check(await page.locator('#welcome').isVisible(), false, 'Conversation must replace the welcome state.');
    await receive({ type: 'message', who: 'Pi', text: '<img src="https://evil.invalid" onerror="window.__injected=1">\n\n```html\n<script>window.__injected=1</script>\n```' });
    check(await page.evaluate(() => window.__injected), undefined, 'Model text must never execute JavaScript.');
    check(await page.locator('.message-body img,.message-body script').count(), 0, 'Model markup must remain text.');
    await receive({ type: 'busy', busy: true, canStop: true });
    check(await page.locator('#send').isDisabled(), true, 'Busy state must disable Send.');
    check(await page.locator('#new-chat').isDisabled(), true, 'Busy state must disable New Chat.');
    check(await page.locator('#prompt').evaluate(node => node.readOnly), true, 'Busy state must protect the pending prompt from edits.');
    check(await page.locator('#stop').isVisible(), true, 'Busy state must expose Stop.');
    await page.locator('#stop').click();
    check(await page.evaluate(() => window.apertureSent.at(-1).command), 'stop', 'Stop must reach the bridge.');
    await receive({ type: 'busy', busy: false, canStop: true });
    await page.locator('#settings').click();
    check(await page.evaluate(() => window.apertureSent.at(-1).command), 'settings', 'Settings must reach the bridge after the run.');
    await page.setViewportSize({ width: 320, height: 650 });
    await receive({ type: 'clear_transcript' });
    await receive({ type: 'status', text: 'Книга привязана · готова к работе', error: false });
    await page.screenshot({ path: path.join(output, screenshots[2]) });
    check(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'Narrow pane must not overflow horizontally.');
    check(await page.locator('#send').isVisible(), true, 'Narrow pane must retain the composer.');

    const activity = event => receive({ type: 'activity', event });
    const modelMessage = (content = [], stopReason = 'stop') => ({ role: 'assistant', content, stopReason, provider: 'fixture-provider', model: 'fixture-model' });
    const paint = () => page.evaluate(() => new Promise(resolve => setTimeout(() => requestAnimationFrame(resolve), 100)));
    for (const width of [320, 410, 760]) {
      await page.setViewportSize({ width, height: 850 });
      const height = await page.locator('.header').evaluate(node => node.getBoundingClientRect().height);
      check(height >= 70 && height <= 90, true, 'Header must stay compact at width ' + width + '.');
      check(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'Pane must fit width ' + width + '.');
    }
    await page.setViewportSize({ width: 410, height: 850 });
    await receive({ type: 'clear_transcript' });
    await receive({ type: 'message', who: 'Вы', text: 'Синтетическая проверка: один инструмент и два ответа модели.' });
    await receive({ type: 'busy', busy: true, canStop: true });
    await activity({ type: 'agent_start' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    const firstThinking = 'Сопоставляю **листы** с зависимостями.';
    const secondThinking = 'Проверяю формулы и входные допущения.';
    // Wire events are delta-only: no partial snapshots are needed.
    await activity({ type: 'message_update', assistantMessageEvent: { type: 'thinking_start', contentIndex: 0 } });
    await activity({ type: 'message_update', assistantMessageEvent: { type: 'thinking_delta', contentIndex: 0, delta: 'Сопоставляю **листы**' } });
    await activity({ type: 'message_update', assistantMessageEvent: { type: 'thinking_delta', contentIndex: 0, delta: ' с зависимостями.' } });
    await activity({ type: 'message_update', assistantMessageEvent: { type: 'thinking_end', contentIndex: 0, content: firstThinking } });
    await activity({ type: 'message_end', message: modelMessage([{ type: 'thinking', thinking: firstThinking }, { type: 'toolCall', id: 'target-1', name: 'excel_target', arguments: {} }], 'toolUse') });
    await activity({ type: 'tool_execution_start', toolCallId: 'target-1', toolName: 'mcp__aperture_excel__excel_target', args: {} });
    await activity({ type: 'tool_execution_update', toolCallId: 'target-1', toolName: 'mcp__aperture_excel__excel_target', args: {}, partialResult: { status: 'reading' } });
    await activity({ type: 'tool_execution_end', toolCallId: 'target-1', toolName: 'mcp__aperture_excel__excel_target', result: { content: [{ type: 'text', text: 'Synthetic workbook: DCF, Inputs' }] }, isError: false, durationMs: 40 });
    await activity({ type: 'message_end', message: { role: 'toolResult', toolCallId: 'target-1', content: [{ type: 'text', text: 'Synthetic workbook' }] } });
    await activity({ type: 'turn_end' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    await activity({ type: 'message_update', assistantMessageEvent: { type: 'thinking_delta', contentIndex: 0, delta: 'Проверяю формулы' } });
    await activity({ type: 'message_update', assistantMessageEvent: { type: 'thinking_end', contentIndex: 0, content: secondThinking } });
    await receive({ type: 'assistant_delta', text: '### Итоги\n\n**Связи найдены**.\n\n| Лист | Ячейка |\n| --- | --- |\n| DCF | F24 |\n\n```excel\n=SUM(A1:A3)' });
    await paint();
    check(await page.locator('.message.assistant h4').count(), 1, 'Heading must render before assistant_end.');
    check(await page.locator('.message.assistant strong').textContent(), 'Связи найдены', 'Bold text must render while streaming.');
    check(await page.locator('.message.assistant table').count(), 1, 'Table must render before assistant_end.');
    check(await page.locator('.message.assistant pre code').textContent(), '=SUM(A1:A3)', 'An unfinished code fence must remain readable.');
    check(await page.locator('.activity-run').evaluate(node => node.open), true, 'All tool-call rows must be visible by default.');
    check(await page.locator('.tool-step pre').allTextContents(), ['', ''], 'Tool arguments and results must remain lazy while collapsed.');
    for (const summary of await page.locator('.model-step > summary').all()) await summary.click();
    await paint();
    await activity({ type: 'message_end', message: modelMessage([{ type: 'thinking', thinking: secondThinking }, { type: 'text', text: 'Итоги' }]) });
    await paint();
    check(await page.locator('.model-step').count(), 2, 'Two assistant messages must create exactly two model steps.');
    check(await page.locator('.tool-step').count(), 1, 'Tool-result message must not duplicate the tool card.');
    check(await page.locator('.reasoning-section').count(), 2, 'Delta/end/final fallback must create exactly two reasoning blocks.');
    check(await page.locator('.reasoning-body').allTextContents(), ['Сопоставляю листы с зависимостями.', secondThinking], 'Authoritative reasoning must replace deltas without duplication.');
    await receive({ type: 'assistant_replace', text: '### Итоги\n\n**Окончательный ответ** с добавленным окончанием.' });
    await receive({ type: 'assistant_end' });
    check(await page.locator('.message.assistant').last().textContent(), 'ApertureИтогиОкончательный ответ с добавленным окончанием.', 'Final assistant replacement must flush immediately without duplicated partial text.');
    await activity({ type: 'agent_end', willRetry: true });
    check(await page.locator('.activity-run').getAttribute('data-state'), 'running', 'agent_end must not finish a run before retries.');
    await activity({ type: 'agent_settled', aborted: false });
    check(await page.locator('.activity-run').getAttribute('data-state'), 'done', 'agent_settled must finish the run.');

    // A second synthetic run exercises concurrent tools, completion order and lazy large results.
    await receive({ type: 'message', who: 'Вы', text: 'Проверь связи, формулы и допущения в модели.' });
    await activity({ type: 'agent_start' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    await activity({ type: 'message_end', message: modelMessage([{ type: 'toolCall', id: 'read-a', name: 'excel_read_range', arguments: {} }], 'toolUse') });
    for (const id of ['read-a', 'read-b', 'find-c']) await activity({ type: 'tool_execution_start', toolCallId: id, toolName: id === 'find-c' ? 'mcp__aperture_excel__excel_find' : 'mcp__aperture_excel__excel_read_range', args: { sheet: 'DCF', address: id === 'read-b' ? 'F20:F30' : 'A1:D8' } });
    await activity({ type: 'tool_execution_update', toolCallId: 'read-a', toolName: 'mcp__aperture_excel__excel_read_range', partialResult: { rowsRead: 4 } });
    await activity({ type: 'tool_execution_end', toolCallId: 'read-b', result: { values: [[12, 18]] }, isError: false, durationMs: 55 });
    await activity({ type: 'tool_execution_end', toolCallId: 'find-c', result: { error: 'Synthetic search failure' }, isError: true });
    check(await page.locator('[data-tool-call-id="read-a"]').getAttribute('data-state'), 'running', 'Out-of-order completion must leave the other tool running.');
    check(await page.locator('[data-tool-call-id="find-c"]').getAttribute('data-state'), 'error', 'Error must belong to the correct toolCallId.');
    await activity({ type: 'tool_execution_end', toolCallId: 'read-a', result: { content: [{ type: 'text', text: 'x'.repeat(45000) }] }, isError: false });
    const run = page.locator('.activity-run').last();
    await page.locator('[data-tool-call-id="read-a"] > summary').click();
    check((await page.locator('[data-tool-call-id="read-a"] pre').last().textContent()).includes('Показаны первые 40000'), true, 'Expanded large result must declare its display limit.');
    check(await page.locator('[data-tool-call-id="read-a"] .activity-entry-title').textContent(), 'Чтение диапазона', 'Known tools must use concise human labels.');
    check((await page.locator('[data-tool-call-id="read-a"] .activity-entry-caption').textContent()).includes('DCF · A1:D8'), true, 'Tool caption must retain range context.');
    await page.locator('[data-tool-call-id="read-a"] > summary').click();
    await activity({ type: 'turn_end' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    await activity({ type: 'message_update', assistantMessageEvent: { type: 'thinking_delta', contentIndex: 0, delta: 'Сравниваю связи между листами и проверяю допущения.' } });
    await receive({ type: 'assistant_delta', text: '### Предварительные результаты\n\nПроверила **связи между листами**.\n\n| Область | Наблюдение |\n| --- | --- |\n| `DCF!F24` | Проверяю базу дисконтирования |\n| `Inputs!B8` | Сопоставляю темп роста |\n\nПоиск вернул ошибку; использую уже прочитанные диапазоны.' });
    await receive({ type: 'status', text: 'Формируется ответ…', error: false });
    await paint();
    await page.setViewportSize({ width: 410, height: 1040 });
    await run.locator('.model-step').last().locator(':scope > summary').click();
    await paint();
    await page.locator('#conversation').evaluate(node => node.scrollTo({ top: node.scrollHeight, behavior: 'instant' }));
    await page.screenshot({ path: path.join(output, screenshots[3]) });
    await page.setViewportSize({ width: 410, height: 850 });
    check(await run.locator('.model-step').count(), 2, 'Several tools must not inflate the model-step count.');
    check(await run.locator('.tool-step').count(), 3, 'Three tool IDs must create three independent cards.');
    check((await run.locator('.activity-count').textContent()).includes('5 шаг. · 3 инстр.'), true, 'Activity count must represent steps and tool calls, not API requests.');

    // A reader above the tail must not be pulled down by incoming text/activity.
    await receive({ type: 'assistant_delta', text: '\n\n' + Array.from({ length: 35 }, (_, index) => 'Строка проверки ' + index + '.').join('\n\n') });
    await paint();
    await page.locator('#conversation').evaluate(node => { node.scrollTop = 20; });
    await paint();
    const beforeScroll = await page.locator('#conversation').evaluate(node => node.scrollTop);
    await receive({ type: 'assistant_delta', text: '\n\nНовая строка в конце ответа.' });
    await activity({ type: 'message_update', assistantMessageEvent: { type: 'thinking_delta', contentIndex: 0, delta: ' Проверка продолжается.' } });
    await paint();
    check(Math.abs((await page.locator('#conversation').evaluate(node => node.scrollTop)) - beforeScroll) < 2, true, 'Streaming must preserve the reader position above the tail.');
    // An authoritative empty or redacted reasoning block must remove prior partial text.
    await activity({ type: 'message_update', assistantMessageEvent: { type: 'thinking_end', contentIndex: 0, content: '' } });
    check(await run.locator('.reasoning-section').count(), 0, 'Authoritative empty reasoning must remove stale streamed text.');
    await activity({ type: 'message_update', assistantMessageEvent: { type: 'thinking_delta', contentIndex: 1, delta: 'Temporary partial' } });
    await activity({ type: 'message_end', message: modelMessage([{ type: 'text', text: 'Ответ' }]) });
    check(await run.locator('.reasoning-section').count(), 0, 'Reasoning absent from final message must remove stale partial blocks.');
    await receive({ type: 'assistant_replace', text: '' });
    check(await page.locator('.message.assistant').count(), 1, 'Empty authoritative assistant text must remove only the current partial bubble.');
    await activity({ type: 'tool_execution_start', toolCallId: 'cancel-me', toolName: 'custom_tool', args: { text: '<script>window.__activityInjected=1</script>' } });
    await activity({ type: 'aperture_run_end', status: 'stopped' });
    check(await run.getAttribute('data-state'), 'stopped', 'Confirmed abort must mark the run stopped.');
    check(await page.locator('[data-tool-call-id="cancel-me"]').getAttribute('data-state'), 'unknown', 'Abort must preserve the unknown outcome of an in-flight tool.');
    await page.locator('[data-tool-call-id="cancel-me"] > summary').click();
    check(await page.evaluate(() => window.__activityInjected), undefined, 'Tool arguments must not execute markup.');
    check(await page.locator('.activity-detail script,.activity-detail img').count(), 0, 'Activity details must use text nodes.');
    await receive({ type: 'message', who: 'Вы', text: 'Синтетическая ошибка провайдера.' });
    await activity({ type: 'agent_start' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    await activity({ type: 'message_update', assistantMessageEvent: { type: 'thinking_delta', contentIndex: 0, delta: 'Partial that will be redacted' } });
    await activity({ type: 'message_end', message: modelMessage([{ type: 'thinking', thinking: '', redacted: true, thinkingSignature: 'opaque-fixture-never-display' }], 'error') });
    await activity({ type: 'agent_settled', aborted: false });
    await activity({ type: 'aperture_run_end', status: 'error' });
    check(await page.locator('.activity-run').last().getAttribute('data-state'), 'error', 'Final provider failure must override ordinary settled status.');
    check(await page.locator('.activity-run').last().locator('.reasoning-section').count(), 0, 'Redacted provider reasoning must never be displayed.');
    check((await page.locator('#messages').textContent()).includes('opaque-fixture-never-display'), false, 'Opaque thinking signatures must never be rendered.');

    // Show every generated call before execution, including unknown/repeated names.
    await receive({ type: 'clear_transcript' });
    await receive({ type: 'message', who: 'Вы', text: 'Покажи все вызовы инструментов, включая формирующиеся.' });
    await activity({ type: 'agent_start' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    const allToolsRun = page.locator('.activity-run').last();
    const toolUpdate = update => activity({ type: 'message_update', assistantMessageEvent: update });
    await toolUpdate({ type: 'toolcall_start', contentIndex: 0, id: 'custom-1', toolName: 'private_lookup' });
    check(await page.locator('[data-tool-call-id="custom-1"]').isVisible(), true, 'A call row must appear before execution begins.');
    check(await page.locator('[data-tool-call-id="custom-1"]').getAttribute('data-state'), 'generating', 'toolcall_start must show formation, not execution.');
    check(await page.locator('[data-tool-call-id="custom-1"]').evaluate(node => node.open), false, 'Only call details must stay collapsed.');
    await toolUpdate({ type: 'toolcall_delta', contentIndex: 0, delta: '{"sheet":"DCF",' });
    await toolUpdate({ type: 'toolcall_delta', contentIndex: 0, delta: '"address":"B1:C3"}' });
    await page.locator('[data-tool-call-id="custom-1"] > summary').click();
    await paint();
    check(await page.locator('[data-tool-call-id="custom-1"] pre').first().textContent(), '{"sheet":"DCF","address":"B1:C3"}', 'Argument deltas must accumulate within the call block.');
    const firstCall = { type: 'toolCall', id: 'custom-1', name: 'private_lookup', arguments: { sheet: 'DCF', address: 'B1:C4' } };
    await toolUpdate({ type: 'toolcall_end', contentIndex: 0, toolCall: firstCall });
    check((await page.locator('[data-tool-call-id="custom-1"] pre').first().textContent()).includes('B1:C4'), true, 'Authoritative completed arguments must replace streamed JSON.');
    check(await page.locator('[data-tool-call-id="custom-1"]').getAttribute('data-state'), 'queued', 'A completed call block must await execution.');
    await page.locator('[data-tool-call-id="custom-1"] > summary').click();
    await toolUpdate({ type: 'toolcall_start', contentIndex: 1, id: 'custom-2', toolName: 'private_lookup' });
    const secondCall = { type: 'toolCall', id: 'custom-2', name: 'private_lookup', arguments: { sheet: 'Inputs', address: 'A1:B5' } };
    await toolUpdate({ type: 'toolcall_end', contentIndex: 1, toolCall: secondCall });
    await toolUpdate({ type: 'toolcall_start', contentIndex: 2, id: 'custom-3', toolName: 'external_metrics' });
    check(await allToolsRun.locator('.tool-step').count(), 3, 'Three unknown calls must have three rows.');
    check(await allToolsRun.locator('.tool-step .activity-entry-title').allTextContents(), ['private_lookup', 'private_lookup', 'external_metrics'], 'Repeated names and unknown tools must remain independently visible.');
    check(await allToolsRun.locator('.tool-step').evaluateAll(nodes => nodes.every(node => node.getBoundingClientRect().height > 0)), true, 'All call rows must be displayed without expanding the run.');
    check(await allToolsRun.locator('.tool-step').evaluateAll(nodes => nodes.every(node => !node.open)), true, 'Argument/result details must remain collapsed independently of row visibility.');
    await activity({ type: 'tool_execution_start', toolCallId: 'custom-1', toolName: 'private_lookup', args: firstCall.arguments });
    check(await allToolsRun.locator('.tool-step').count(), 3, 'Execution start must reuse the generated call card.');
    check(await page.locator('[data-tool-call-id="custom-1"]').getAttribute('data-state'), 'running', 'Execution start must advance only that call.');
    await activity({ type: 'tool_execution_update', toolCallId: 'custom-1', partialResult: { progress: 1 } });
    const firstResult = { type: 'tool_execution_end', toolCallId: 'custom-1', result: { value: 42 }, isError: false };
    await activity(firstResult);
    await activity(firstResult);
    check(await allToolsRun.locator('.tool-step').count(), 3, 'Repeated callbacks for the same ID must not duplicate the row.');
    check(await page.locator('[data-tool-call-id="custom-1"]').getAttribute('data-state'), 'done', 'Completed execution must retain its result state.');
    check((await allToolsRun.locator('.activity-title').textContent()).startsWith('Формируется: external_metrics'), true, 'Run summary must retain pending generation after another call completes.');
    await page.locator('#conversation').evaluate(node => node.scrollTo({ top: node.scrollHeight, behavior: 'instant' }));
    await page.screenshot({ path: path.join(output, screenshots[4]) });
    const thirdCall = { type: 'toolCall', id: 'custom-3', name: 'external_metrics', arguments: { metric: 'growth' } };
    const fallbackCall = { type: 'toolCall', id: 'fallback-4', name: 'fallback_only', arguments: { source: 'final-message' } };
    await activity({ type: 'message_end', message: modelMessage([firstCall, secondCall, thirdCall, fallbackCall], 'toolUse') });
    check(await allToolsRun.locator('.tool-step').count(), 4, 'Final message fallback must add a call with no streaming events.');
    check(await page.locator('[data-tool-call-id="fallback-4"]').isVisible(), true, 'Fallback-only call must be visible immediately.');
    check(await page.locator('[data-tool-call-id="custom-1"]').getAttribute('data-state'), 'done', 'Final fallback must not downgrade an already executed call.');
    await activity({ type: 'message_end', message: modelMessage([firstCall, secondCall, thirdCall, fallbackCall], 'toolUse') });
    check(await allToolsRun.locator('.tool-step').count(), 4, 'Repeated authoritative fallback must not duplicate calls.');
    await activity({ type: 'tool_execution_start', toolCallId: 'custom-2', toolName: 'private_lookup', args: secondCall.arguments });
    await activity({ type: 'agent_settled', aborted: true });
    check(await page.locator('[data-tool-call-id="custom-1"]').getAttribute('data-state'), 'done', 'Abort must preserve earlier completed calls.');
    check(await page.locator('[data-tool-call-id="custom-2"]').getAttribute('data-state'), 'unknown', 'Abort must not claim that an in-flight COM operation stopped.');
    check(await page.locator('[data-tool-call-id="custom-3"]').getAttribute('data-state'), 'not_run', 'Abort before execution must never claim success.');
    check(await page.locator('[data-tool-call-id="fallback-4"]').getAttribute('data-state'), 'not_run', 'Unexecuted fallback call must retain an explicit not-run status.');
    check(errors, [], 'UI must not raise uncaught JavaScript errors.');
    check(network, [], 'Local UI must not request external resources.');
    console.log(JSON.stringify({ passed: true, checks, screenshots: screenshots.map(name => 'work/' + name), consoleErrors: errors, externalRequests: network }));
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
