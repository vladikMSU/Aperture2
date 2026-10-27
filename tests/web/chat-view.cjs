// Browser-only regression for the actual local HTML/CSS/JS. The bridge and audit
// findings below are synthetic fixtures, not results from Excel or an LLM.
const { chromium } = require('playwright');
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');

const root = path.resolve(__dirname, '../..');
const output = path.join(root, 'work');
const screenshots = ['chat-webview-welcome.png', 'chat-webview-conversation.png', 'chat-webview-streaming.png',
  'chat-webview-collapsed.png', 'chat-webview-narrow.png', 'chat-webview-light.png', 'chat-webview-all-tools.png',
  'chat-webview-composer-idle.png', 'chat-webview-composer-busy.png', 'chat-webview-composer-error.png',
  'chat-webview-compaction-running.png', 'chat-webview-compaction.png'];
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
    const page = await browser.newPage({ viewport: { width: 410, height: 850 }, deviceScaleFactor: 1, colorScheme: 'dark' });
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
    const activity = event => receive({ type: 'activity', event });
    const update = event => activity({ type: 'message_update', assistantMessageEvent: event });
    const lastSent = () => page.evaluate(() => window.apertureSent.at(-1));
    const paint = () => page.evaluate(() => new Promise(resolve => setTimeout(() => requestAnimationFrame(resolve), 100)));
    const modelMessage = (content = [], stopReason = 'stop') => ({ role: 'assistant', content, stopReason, provider: 'fixture-provider', model: 'fixture-model', usage: { input: 900, output: 120, totalTokens: 1020 } });
    const run = () => page.locator('.run').last();
    const tool = id => page.locator(`[data-tool-call-id="${id}"]`);
    const notice = (level, title, detail, inRun = false) => receive({ type: 'notice', level, title, detail, inRun });
    // A short pane: the composer and the end of the timeline above it in one picture.
    const bottom = async name => {
      await page.setViewportSize({ width: 410, height: 460 });
      await page.locator('#conversation').evaluate(node => { node.scrollTop = node.scrollHeight; });
      await page.screenshot({ path: path.join(output, name) });
      await page.setViewportSize({ width: 410, height: 850 });
    };
    const promptScroll = () => page.locator('#prompt').evaluate(node => ({ overflowY: getComputedStyle(node).overflowY, scrolls: node.scrollHeight > node.clientHeight }));

    // ---------------------------------------------------------------- header, composer and commands
    check(await page.locator('#ready-marker').count(), 0, 'Fixture sanity: no unexpected markup.');
    check(await page.evaluate(() => window.apertureSent[0].command), 'ready', 'The page must announce readiness to the host.');
    check(await page.locator('#workbook-name, #rebind').count(), 0, 'The header must not name the workbook or offer rebinding.');
    check(await page.locator('.toolbar button').evaluateAll(nodes => nodes.map(node => node.id)), ['new-chat', 'settings'], 'The header keeps only New chat and Settings.');
    check(await page.locator('.header').evaluate(node => Array.from(node.children).map(child => child.className)), ['toolbar'], 'The header holds only the chat actions: Excel titles the pane.');
    check((await page.locator('.header').textContent()).trim(), '', 'The header shows no product name or other text.');
    check(await page.locator('#status, #status-text, .status-dot').count(), 0, 'The composer has no status line.');
    check(await page.locator('.composer-bar').evaluate(node => Array.from(node.children).map(child => child.id)), ['stop', 'send'], 'The composer bar holds only Stop/Send.');
    check(await promptScroll(), { overflowY: 'hidden', scrolls: false }, 'An empty one-line prompt must have no scrollbar arrows.');
    await page.screenshot({ path: path.join(output, screenshots[0]) });
    await bottom(screenshots[7]);
    check(await page.locator('#send').isDisabled(), true, 'Empty prompt must disable Send.');
    await page.locator('[data-prompt]').first().click();
    await page.locator('#prompt').press('Shift+Enter');
    check((await lastSent()).command, 'ready', 'Shift+Enter must insert a line break, not send.');
    await page.locator('#prompt').press('Enter');
    check((await lastSent()).command, 'send', 'Enter must emit Send.');
    check((await lastSent()).text.includes('структуру'), true, 'Suggestion text must reach the bridge.');
    await receive({ type: 'busy', busy: true, canStop: true });
    check(await page.locator('#prompt').evaluate(node => node.readOnly), true, 'A submitted prompt must stay protected until the host clears it.');
    check(await page.locator('#send').isDisabled(), true, 'Busy state must disable Send.');
    check(await page.locator('#new-chat').isDisabled(), true, 'Busy state must disable New Chat.');
    check(await page.locator('#stop').isVisible(), true, 'Busy state must expose Stop.');
    await receive({ type: 'message', who: 'Вы', text: 'Проверь финансовую модель: где расчёты требуют внимания?' });
    await receive({ type: 'clear_prompt' });
    check(await page.locator('#prompt').evaluate(node => node.readOnly), false, 'After the host clears the prompt the next question can be drafted.');
    await page.locator('#prompt').fill('Черновик следующего вопроса');
    check(await page.locator('#send').isDisabled(), true, 'A draft must not be sendable while the agent works.');
    await page.locator('#stop').click();
    check((await lastSent()).command, 'stop', 'Stop must reach the bridge.');
    await receive({ type: 'busy', busy: true, canStop: false });
    check(await page.locator('#stop').isDisabled(), true, 'Pending Stop must disable a duplicate Stop.');
    await receive({ type: 'busy', busy: false, canStop: true });
    check(await page.locator('#send').isDisabled(), false, 'The draft becomes sendable once the run is idle.');
    await page.locator('#settings').click();
    check((await lastSent()).command, 'settings', 'Settings must reach the bridge after the run.');
    await page.locator('#new-chat').click();
    check((await lastSent()).command, 'new_chat', 'New chat must reach the bridge.');
    // A request started through the add-in's automation: the draft stays, but cannot be edited or sent.
    const placeholder = await page.locator('#prompt').getAttribute('placeholder');
    await receive({ type: 'scripted', active: true });
    await receive({ type: 'busy', busy: true, canStop: true });
    check(await page.locator('#prompt').evaluate(node => node.readOnly), true, 'A scripted request must lock the composer.');
    check(await page.locator('#prompt').inputValue(), 'Черновик следующего вопроса', 'A scripted request must keep the draft.');
    check(await page.locator('#stop').isDisabled(), false, 'Stop must stay available during a scripted request.');
    // The read-only field cannot be typed into; set the value the way an empty composer would have it.
    const setPrompt = value => page.locator('#prompt').evaluate((node, value) => { node.value = value; node.dispatchEvent(new Event('input')); }, value);
    await setPrompt('');
    check(await page.locator('#prompt').getAttribute('placeholder'), placeholder, 'A scripted request keeps the usual placeholder.');
    check(await promptScroll(), { overflowY: 'hidden', scrolls: false }, 'The locked empty prompt must not wrap or show scrollbar arrows.');
    await page.setViewportSize({ width: 320, height: 850 });
    check(await promptScroll(), { overflowY: 'hidden', scrolls: false }, 'The locked empty prompt must not wrap at 320 px either.');
    await page.setViewportSize({ width: 410, height: 850 });
    await setPrompt('Черновик следующего вопроса');
    await receive({ type: 'busy', busy: false, canStop: true });
    await receive({ type: 'scripted', active: false });
    check(await page.locator('#prompt').evaluate(node => node.readOnly), false, 'The composer must unlock after the scripted request.');
    await page.locator('#prompt').fill(Array.from({ length: 14 }, (_, index) => 'Строка черновика ' + index).join('\n'));
    await page.locator('#prompt').dispatchEvent('input');
    check(await promptScroll(), { overflowY: 'auto', scrolls: true }, 'Only text taller than the grown field may scroll.');
    await page.locator('#prompt').fill('');
    await page.locator('#prompt').dispatchEvent('input');
    check(await promptScroll(), { overflowY: 'hidden', scrolls: false }, 'Clearing the prompt must remove the scrollbar again.');

    // ---------------------------------------------------------------- host notices replace the status line
    await receive({ type: 'clear_transcript' });
    await notice('info', 'Настройки сохранены · начните новый чат');
    check(await page.locator('#messages > .message.notice.info').textContent(), 'Настройки сохранены · начните новый чат', 'A note outside a request is its own quiet line.');
    check(await page.locator('#welcome').isVisible(), false, 'A notice replaces the welcome state.');
    await notice('error', 'Не удалось выполнить запрос', 'В другом окне Excel уже выполняется запрос Aperture2.');
    await notice('error', 'Не удалось выполнить запрос', 'В другом окне Excel уже выполняется запрос Aperture2.');
    check(await page.locator('#messages > .message.notice.error').count(), 1, 'An immediately repeated error must not stack a second line.');
    check(await page.locator('#messages > .message.notice.error').evaluate(node => node.classList.contains('again')), true, 'A repeated error marks the existing line.');
    check(await page.locator('#messages > .message.notice.error .notice-title').textContent(), 'Не удалось выполнить запрос', 'An error line names what failed.');
    check(await page.locator('#messages > .message.notice.error .notice-detail').textContent(), 'В другом окне Excel уже выполняется запрос Aperture2.', 'An error line keeps the reason.');
    await receive({ type: 'message', who: 'Вы', text: 'Прочитай лист Расчёт.' });
    await receive({ type: 'busy', busy: true, canStop: true });
    await notice('warning', 'Статус запроса неизвестен · остановите агента перед повтором', 'Pi RPC response timed out.', true);
    await notice('warning', 'Статус запроса неизвестен · остановите агента перед повтором', 'Pi RPC response timed out.', true);
    check(await run().locator('.step.notice.warning.host-notice').count(), 1, 'A warning during a request belongs to its card, once.');
    check(await run().getAttribute('data-state'), 'running', 'A host warning must not end the request.');
    check(await page.locator('#send').isHidden() && await page.locator('#stop').isVisible(), true, 'A running request shows Stop instead of Send.');
    await bottom(screenshots[8]);
    await notice('error', 'Не удалось выполнить запрос', 'Pi RPC failed to start. Pi is not built.', true);
    await activity({ type: 'aperture_run_end', status: 'error' });
    await receive({ type: 'busy', busy: false, canStop: true });
    check(await run().locator('.step.error.host-notice .notice-title').textContent(), 'Не удалось выполнить запрос', 'An error during a request is shown in its card.');
    check(await run().getAttribute('data-state'), 'error', 'The card ends with the error.');
    await notice('error', 'Агент остановлен или вернул ошибку', 'Pi exited.');
    check(await page.locator('#messages > .message.notice.error').count(), 2, 'An error after the request is its own line.');
    await bottom(screenshots[9]);
    await receive({ type: 'message', who: 'Вы', text: 'Остановлю сразу.' });
    await activity({ type: 'aperture_run_end', status: 'stopped' });
    check(await run().getAttribute('data-state'), 'stopped', 'A Stop before the first Pi event still gets its card.');
    check(await run().locator('.step.notice.stopped').textContent(), 'Ответ остановлен', 'That card says the answer was stopped.');

    // ---------------------------------------------------------------- one request: chronological timeline
    await receive({ type: 'clear_transcript' });
    check(await page.locator('#welcome').isVisible(), true, 'New chat must restore the welcome state.');
    await receive({ type: 'message', who: 'Вы', text: 'Опиши структуру модели и связи между листами.' });
    check(await page.locator('#welcome').isVisible(), false, 'Conversation must replace the welcome state.');
    await receive({ type: 'busy', busy: true, canStop: true });
    await activity({ type: 'agent_start' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    const firstThinking = 'Сопоставляю **листы** с зависимостями.';
    // Wire events are delta-only: no partial snapshots are needed.
    await update({ type: 'thinking_start', contentIndex: 0 });
    await update({ type: 'thinking_delta', contentIndex: 0, delta: 'Сопоставляю **листы**' });
    await update({ type: 'thinking_delta', contentIndex: 0, delta: ' с зависимостями.' });
    await update({ type: 'thinking_end', contentIndex: 0, content: firstThinking });
    await update({ type: 'text_start', contentIndex: 1 });
    await update({ type: 'text_delta', contentIndex: 1, delta: 'Начну с обзора книги.' });
    await update({ type: 'toolcall_start', contentIndex: 2, id: 'target-1', toolName: 'mcp__aperture_excel__excel_target' });
    const targetCall = { type: 'toolCall', id: 'target-1', name: 'mcp__aperture_excel__excel_target', arguments: {} };
    await update({ type: 'toolcall_end', contentIndex: 2, toolCall: targetCall });
    await activity({ type: 'message_end', message: modelMessage([{ type: 'thinking', thinking: firstThinking }, { type: 'text', text: 'Начну с обзора книги.' }, targetCall], 'toolUse') });
    await activity({ type: 'tool_execution_start', toolCallId: 'target-1', toolName: 'mcp__aperture_excel__excel_target', args: {} });
    await activity({ type: 'tool_execution_update', toolCallId: 'target-1', toolName: 'mcp__aperture_excel__excel_target', args: {}, partialResult: { status: 'reading' } });
    await activity({ type: 'tool_execution_end', toolCallId: 'target-1', toolName: 'mcp__aperture_excel__excel_target', result: { content: [{ type: 'text', text: 'Synthetic workbook: DCF, Inputs' }] }, isError: false, durationMs: 40 });
    await activity({ type: 'message_end', message: { role: 'toolResult', toolCallId: 'target-1', content: [{ type: 'text', text: 'Synthetic workbook' }] } });
    await activity({ type: 'turn_end' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    await update({ type: 'text_delta', contentIndex: 0, delta: 'Теперь прочитаю ключевой диапазон.' });
    const readCall = { type: 'toolCall', id: 'read-1', name: 'mcp__aperture_excel__excel_read_range', arguments: { sheet: 'DCF', address: 'A1:D8' } };
    await update({ type: 'toolcall_start', contentIndex: 1, id: 'read-1', toolName: readCall.name });
    await update({ type: 'toolcall_end', contentIndex: 1, toolCall: readCall });
    await activity({ type: 'message_end', message: modelMessage([{ type: 'text', text: 'Теперь прочитаю ключевой диапазон.' }, readCall], 'toolUse') });
    await activity({ type: 'tool_execution_start', toolCallId: 'read-1', toolName: readCall.name, args: readCall.arguments });
    await activity({ type: 'tool_execution_end', toolCallId: 'read-1', toolName: readCall.name, result: { content: [{ type: 'text', text: '{"values":[[1]]}' }] }, isError: false, durationMs: 55 });
    await activity({ type: 'turn_end' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    await update({ type: 'thinking_delta', contentIndex: 0, delta: 'Проверяю формулы' });
    await update({ type: 'thinking_end', contentIndex: 0, content: 'Проверяю формулы и входные допущения.' });
    // Deliberately fictional audit text exercises Markdown rendering while streaming.
    await update({ type: 'text_delta', contentIndex: 1, delta: 'Вступление.\n\n---\n\n### Итоги\n\n**Связи найдены**, *часть* — гипотезы.\n\n| Лист | Ячейка |\n| --- | ---: |\n| DCF | `F24` |\n\n1. Первый\n   - вложенный\n\n> Цитата\n\n```excel\n=SUM(A1:A3)' });
    await paint();
    const body = run().locator('.text').last();
    check(await body.locator('h3').count(), 1, 'Heading must render before message_end.');
    check(await body.locator('hr').count(), 1, 'A Markdown rule (---) must render as a divider, not as three dashes.');
    check((await body.textContent()).includes('---'), false, 'Literal --- must not remain in the answer.');
    check(await body.locator('strong').textContent(), 'Связи найдены', 'Bold text must render while streaming.');
    check(await body.locator('em').textContent(), 'часть', 'Italic text must render.');
    check(await body.locator('table').count(), 1, 'Table must render before message_end.');
    check(await body.locator('td.align-right').count(), 1, 'Table column alignment must be kept.');
    check(await body.locator('ol > li > ul > li').count(), 1, 'Nested lists must keep their structure.');
    check(await body.locator('blockquote').count(), 1, 'Block quotes must render.');
    check(await body.locator('pre code').textContent(), '=SUM(A1:A3)', 'An unfinished code fence must remain readable.');
    await page.screenshot({ path: path.join(output, screenshots[2]) });
    const finalText = 'Вступление.\n\n---\n\n### Итоги\n\n**Окончательный ответ** с добавленным окончанием.';
    await activity({ type: 'message_end', message: modelMessage([{ type: 'thinking', thinking: 'Проверяю формулы и входные допущения.' }, { type: 'text', text: finalText }]) });
    await paint();
    // The fixed bug: text, reasoning and tool steps keep their order instead of all text sinking to the bottom.
    const order = await run().locator('.run-body > *').evaluateAll(nodes => nodes.map(node =>
      node.classList.contains('trace') ? 'trace:' + Array.from(node.children).map(step => step.classList.contains('tool') ? step.dataset.toolCallId : 'reasoning').join(',') : 'text'));
    check(order, ['trace:reasoning', 'text', 'trace:target-1', 'text', 'trace:read-1,reasoning', 'text'], 'Timeline must follow the order of the model events.');
    check(await run().locator('.reasoning').count(), 2, 'Delta/end/final fallback must create exactly two reasoning steps.');
    check(await run().locator('.text').last().textContent(), 'Вступление.ИтогиОкончательный ответ с добавленным окончанием.', 'Authoritative final text must replace the streamed partial text without duplication.');
    check(await run().locator('.tool').count(), 2, 'A tool-result message must not duplicate tool rows.');
    await activity({ type: 'agent_end', willRetry: true });
    check(await run().getAttribute('data-state'), 'running', 'agent_end must not finish a run before retries.');
    await activity({ type: 'agent_settled', aborted: false });
    await receive({ type: 'busy', busy: false, canStop: true });
    check(await run().getAttribute('data-state'), 'done', 'agent_settled must finish the run.');
    check(await run().locator('.run-body > .text.final').count(), 1, 'The last text after the last step is the final answer.');
    check((await run().locator('.run-meta').textContent()).startsWith('2 действия'), true, 'Run summary must count tool calls.');
    check((await run().locator('.run-footer').textContent()).includes('fixture-model'), true, 'Run footer must name the model.');
    await page.screenshot({ path: path.join(output, screenshots[1]) });
    await run().locator('.run-header').click();
    check(await run().locator('.trace').first().isVisible(), false, 'Collapsing the run must hide its steps.');
    check(await run().locator('.text.final').isVisible(), true, 'Collapsing the run must keep the final answer visible.');
    check(await run().locator('.text').first().isVisible(), false, 'Collapsing the run must hide intermediate text.');
    await page.screenshot({ path: path.join(output, screenshots[3]) });
    await run().locator('.run-header').click();
    check(await run().locator('.trace').first().isVisible(), true, 'Expanding the run must restore its steps.');

    // ---------------------------------------------------------------- safety of model and tool text
    await receive({ type: 'message', who: 'Вы', text: 'Проверка безопасного вывода.' });
    await activity({ type: 'agent_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    await update({ type: 'text_delta', contentIndex: 0, delta: '<img src="https://evil.invalid" onerror="window.__injected=1">\n\n[ссылка](https://evil.invalid/x)\n\n```html\n<script>window.__injected=1</script>\n```' });
    await activity({ type: 'tool_execution_start', toolCallId: 'unsafe-1', toolName: 'custom_tool', args: { text: '<script>window.__activityInjected=1</script>' } });
    await paint();
    await tool('unsafe-1').locator('summary').click();
    check(await page.evaluate(() => window.__injected || window.__activityInjected), undefined, 'Model and tool text must never execute JavaScript.');
    check(await page.locator('#messages img, #messages script, #messages a').count(), 0, 'Model markup and links must remain inert text.');
    await activity({ type: 'agent_settled', aborted: false });

    // ---------------------------------------------------------------- concurrent tools, reasoning edge cases
    await receive({ type: 'message', who: 'Вы', text: 'Проверь связи, формулы и допущения в модели.' });
    await activity({ type: 'agent_start' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    await activity({ type: 'message_end', message: modelMessage([{ type: 'toolCall', id: 'read-a', name: 'excel_read_range', arguments: {} }], 'toolUse') });
    for (const id of ['read-a', 'read-b', 'find-c']) await activity({ type: 'tool_execution_start', toolCallId: id, toolName: id === 'find-c' ? 'mcp__aperture_excel__excel_find' : 'mcp__aperture_excel__excel_read_range', args: { sheet: 'DCF', address: id === 'read-b' ? 'F20:F30' : 'A1:D8', text: id === 'find-c' ? 'WACC' : undefined } });
    await activity({ type: 'tool_execution_update', toolCallId: 'read-a', toolName: 'mcp__aperture_excel__excel_read_range', partialResult: { rowsRead: 4 } });
    await activity({ type: 'tool_execution_end', toolCallId: 'read-b', result: { values: [[12, 18]] }, isError: false, durationMs: 55 });
    await activity({ type: 'tool_execution_end', toolCallId: 'find-c', result: { error: 'Synthetic search failure' }, isError: true });
    check(await tool('read-a').getAttribute('data-state'), 'running', 'Out-of-order completion must leave the other tool running.');
    check(await tool('find-c').getAttribute('data-state'), 'error', 'Error must belong to the correct toolCallId.');
    check(await tool('read-a').locator('.step-label').textContent(), 'Чтение диапазона', 'Known tools must use concise human labels.');
    check(await tool('read-a').locator('.step-context').textContent(), 'DCF!A1:D8', 'Tool row must show the Excel-style range.');
    check(await tool('find-c').locator('.step-context').textContent(), 'DCF!A1:D8 · «WACC»', 'Search rows must show the searched text.');
    check(await tool('read-a').locator('pre').allTextContents(), ['', ''], 'Tool arguments and results must stay lazy while collapsed.');
    await activity({ type: 'tool_execution_end', toolCallId: 'read-a', result: { content: [{ type: 'text', text: 'x'.repeat(45000) }] }, isError: false });
    await tool('read-a').locator('summary').click();
    await paint();
    check((await tool('read-a').locator('pre').last().textContent()).includes('Показаны первые 40000'), true, 'Expanded large result must declare its display limit.');
    await tool('read-a').locator('summary').click();
    await activity({ type: 'turn_end' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    await update({ type: 'thinking_delta', contentIndex: 0, delta: 'Сравниваю связи между листами.' });
    await update({ type: 'text_delta', contentIndex: 1, delta: '### Предварительно\n\n' + Array.from({ length: 30 }, (_, index) => 'Строка проверки ' + index + '.').join('\n\n') });
    await paint();
    // A reader above the tail must not be pulled down by incoming text/activity.
    await page.locator('#conversation').evaluate(node => { node.scrollTop = 20; });
    await paint();
    const beforeScroll = await page.locator('#conversation').evaluate(node => node.scrollTop);
    await update({ type: 'text_delta', contentIndex: 1, delta: '\n\nНовая строка в конце ответа.' });
    await update({ type: 'thinking_delta', contentIndex: 0, delta: ' Проверка продолжается.' });
    await paint();
    check(Math.abs((await page.locator('#conversation').evaluate(node => node.scrollTop)) - beforeScroll) < 2, true, 'Streaming must preserve the reader position above the tail.');
    // Authoritative empty or redacted reasoning must remove prior partial text.
    await update({ type: 'thinking_end', contentIndex: 0, content: '' });
    check(await run().locator('.reasoning').count(), 0, 'Authoritative empty reasoning must remove stale streamed text.');
    await update({ type: 'thinking_delta', contentIndex: 2, delta: 'Temporary partial' });
    await activity({ type: 'message_end', message: modelMessage([{ type: 'text', text: '' }]) });
    check(await run().locator('.reasoning').count(), 0, 'Reasoning absent from the final message must remove stale partial steps.');
    check(await run().locator('.text').count(), 0, 'Empty authoritative text must remove the partial answer.');
    await activity({ type: 'tool_execution_start', toolCallId: 'cancel-me', toolName: 'custom_tool', args: { text: 'x' } });
    await activity({ type: 'aperture_run_end', status: 'stopped' });
    check(await run().getAttribute('data-state'), 'stopped', 'Confirmed abort must mark the run stopped.');
    check(await tool('cancel-me').getAttribute('data-state'), 'unknown', 'Abort must preserve the unknown outcome of an in-flight tool.');
    check(await run().locator('.step.notice.stopped').textContent(), 'Ответ остановлен', 'A Stop during a tool call still ends the card with "Ответ остановлен".');
    await receive({ type: 'message', who: 'Вы', text: 'Синтетическая ошибка провайдера.' });
    await activity({ type: 'agent_start' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    await update({ type: 'thinking_delta', contentIndex: 0, delta: 'Partial that will be redacted' });
    await activity({ type: 'message_end', message: { ...modelMessage([{ type: 'thinking', thinking: '', redacted: true, thinkingSignature: 'opaque-fixture-never-display' }], 'error'), errorMessage: 'Synthetic provider failure' } });
    await activity({ type: 'agent_settled', aborted: false });
    await activity({ type: 'aperture_run_end', status: 'error' });
    check(await run().getAttribute('data-state'), 'error', 'Final provider failure must override ordinary settled status.');
    check(await run().locator('.reasoning').count(), 0, 'Redacted provider reasoning must never be displayed.');
    check(await run().locator('.step.error').textContent(), 'Synthetic provider failure', 'Provider error must appear in the timeline.');
    check((await page.locator('#messages').textContent()).includes('opaque-fixture-never-display'), false, 'Opaque thinking signatures must never be rendered.');
    // Whitespace-only text between steps, a user Stop and a router that switches models.
    await receive({ type: 'message', who: 'Вы', text: 'Остановка и пустой текст.' });
    await activity({ type: 'agent_start' });
    await activity({ type: 'message_start', message: { ...modelMessage(), model: 'openrouter/free' } });
    await update({ type: 'thinking_delta', contentIndex: 0, delta: 'Читаю листы.' });
    await update({ type: 'text_delta', contentIndex: 1, delta: '\n\n' });
    await update({ type: 'text_delta', contentIndex: 1, delta: '  ' });
    const blankRead = { type: 'toolCall', id: 'blank-read', name: 'mcp__aperture_excel__excel_read_range', arguments: { sheet: 'Свод', address: 'A1:B2' } };
    await update({ type: 'toolcall_start', contentIndex: 2, id: 'blank-read', toolName: blankRead.name });
    await activity({ type: 'message_end', message: { ...modelMessage([{ type: 'thinking', thinking: 'Читаю листы.' }, { type: 'text', text: '\n\n  ' }, blankRead], 'toolUse'), model: 'openrouter/free', responseModel: 'vendor/first:free' } });
    await activity({ type: 'tool_execution_end', toolCallId: 'blank-read', toolName: blankRead.name, result: { values: [[1]] }, isError: false, durationMs: 10 });
    check(await run().locator('.text').count(), 0, 'Whitespace-only text must not create an empty block.');
    check(await run().locator('.run-body > .trace').count(), 1, 'Whitespace-only text must not split the step group.');
    await activity({ type: 'message_start', message: { ...modelMessage(), model: 'openrouter/free' } });
    await update({ type: 'text_delta', contentIndex: 0, delta: 'Промежуточный ' });
    await update({ type: 'text_delta', contentIndex: 0, delta: '' });
    await activity({ type: 'message_end', message: { ...modelMessage([{ type: 'thinking', thinking: 'Долго думаю.' }], 'aborted'), model: 'openrouter/free', responseModel: 'vendor/second:free', errorMessage: 'Request was aborted' } });
    check(await run().locator('.run-body > .trace').count(), 1, 'Removing partial text must merge the neighbouring step groups back.');
    await activity({ type: 'agent_settled', aborted: true });
    check(await run().locator('.step.error').count(), 0, 'A user Stop must not be shown as a provider error.');
    check(await run().locator('.step.notice.stopped').textContent(), 'Ответ остановлен', 'A user Stop must be a neutral note in the timeline.');
    check((await run().textContent()).includes('Request was aborted'), false, 'The raw abort message must not be shown.');
    check(await run().locator('.run-facts').textContent(), 'openrouter/free → 2 модели · 240 ток.', 'Footer must summarize router models compactly.');
    check(await run().locator('.run-facts').getAttribute('title'), 'openrouter/free\nvendor/first:free\nvendor/second:free', 'All concrete models stay available in the tooltip.');
    await activity({ type: 'aperture_run_end', status: 'stopped' });
    check(await run().locator('.step.notice.stopped').count(), 1, 'The host end of a stopped run must not repeat "Ответ остановлен".');

    // ---------------------------------------------------------------- context compaction is a step of its request
    await receive({ type: 'clear_transcript' });
    const compaction = (index = 0) => run().locator('.step.compaction').nth(index);
    const steps = () => run().locator('.run-body > *').evaluateAll(nodes => nodes.map(node => node.classList.contains('trace')
      ? 'trace:' + Array.from(node.children).map(step => step.classList.contains('compaction') ? 'compaction' : step.classList.contains('tool') ? step.dataset.toolCallId : step.classList.contains('error') ? 'error' : 'other').join(',')
      : node.classList.contains('error') ? 'error' : 'text'));
    await receive({ type: 'message', who: 'Вы', text: 'Продолжи аудит модели.' });
    await receive({ type: 'busy', busy: true, canStop: true });
    // Pi checks the context before sending the prompt: this compaction precedes agent_start.
    await activity({ type: 'compaction_start', reason: 'threshold' });
    check(await run().locator('.step.compaction').count(), 1, 'compaction_start must add a step to the card of the request.');
    check(await compaction().getAttribute('data-state'), 'running', 'A compaction in progress is a running step.');
    check(await compaction().locator('.step-state .spinner').count(), 1, 'A compaction in progress shows a spinner.');
    check(await compaction().locator('.step-label').textContent(), 'Сжимаю контекст…', 'The step says the context is being compacted.');
    check(await compaction().locator('.step-context').textContent(), 'контекст близок к пределу', 'The step names the reason Pi gave.');
    check(await run().locator('.run-title').textContent(), 'Сжимаю контекст', 'The card header shows the compaction as the current action.');
    await activity({ type: 'summarization_retry_scheduled', attempt: 1, maxAttempts: 3, delayMs: 2000, errorMessage: 'Synthetic stream drop' });
    check(await compaction().locator('.step-context').textContent(), 'контекст близок к пределу · повтор 1 из 3', 'A retried summary request is shown in the same row.');
    check(await page.locator('#composer-status, #status').count(), 0, 'Compaction must not bring back a status line under the composer.');
    await page.screenshot({ path: path.join(output, screenshots[10]) });
    const summary = '## Цель\n\nАудит синтетической модели: **DCF** и Inputs.';
    await activity({ type: 'compaction_end', reason: 'threshold', result: { summary, firstKeptEntryId: 'entry-7', tokensBefore: 152340, estimatedTokensAfter: 20990 }, aborted: false, willRetry: false });
    check(await run().locator('.step.compaction').count(), 1, 'compaction_end must update the same row, not add another.');
    check(await compaction().getAttribute('data-state'), 'done', 'A finished compaction is done.');
    check(await compaction().locator('.step-label').textContent(), 'Контекст сжат', 'The finished row says the context was compacted.');
    check(await compaction().locator('.step-context').textContent(), 'до сжатия 152,3 тыс. ток.', 'The row shows the tokens Pi counted before compaction.');
    check(/^\d+,\d с$/.test(await compaction().locator('.step-meta').textContent()), true, 'The finished row shows its duration.');
    check(await compaction().locator('.markdown').textContent(), '', 'The summary stays lazy while the row is collapsed.');
    await compaction().locator('summary').click();
    await paint();
    check(await compaction().locator('.markdown h2').textContent(), 'Цель', 'The opened row renders the summary Pi kept.');
    check(await compaction().locator('.step-info').first().textContent(), 'reason: threshold · tokensBefore: 152340 · estimatedTokensAfter: 20990', 'Details keep the exact figures Pi reported.');
    check(await compaction().locator('.step-info.warning').textContent(), 'Повтор запроса сводки, попытка 1 из 3: Synthetic stream drop', 'Details keep the retry and its error.');
    await compaction().locator('summary').click();
    await activity({ type: 'agent_start' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    await update({ type: 'text_delta', contentIndex: 0, delta: 'Сверю итог.' });
    const auditCall = { type: 'toolCall', id: 'after-compaction', name: 'mcp__aperture_excel__excel_read_range', arguments: { sheet: 'DCF', address: 'F24' } };
    await activity({ type: 'message_end', message: modelMessage([{ type: 'text', text: 'Сверю итог.' }, auditCall], 'toolUse') });
    await activity({ type: 'tool_execution_start', toolCallId: auditCall.id, toolName: auditCall.name, args: auditCall.arguments });
    await activity({ type: 'tool_execution_end', toolCallId: auditCall.id, toolName: auditCall.name, result: { values: [[1]] }, isError: false, durationMs: 30 });
    await activity({ type: 'turn_end' });
    await activity({ type: 'message_start', message: modelMessage() });
    await activity({ type: 'message_end', message: modelMessage([{ type: 'text', text: 'Итог сходится с допущениями.' }]) });
    await activity({ type: 'agent_end', willRetry: false });
    // Threshold compaction after the answer: after agent_end, before agent_settled.
    await activity({ type: 'compaction_start', reason: 'threshold' });
    check(await run().getAttribute('data-state'), 'running', 'A compaction after agent_end keeps the card running.');
    check(await compaction(1).getAttribute('data-state'), 'running', 'The second compaction is its own running row.');
    await activity({ type: 'compaction_end', reason: 'threshold', aborted: false, willRetry: false, errorMessage: 'Auto-compaction failed: Synthetic summary failure' });
    check(await compaction(1).getAttribute('data-state'), 'error', 'A failed compaction is an error in its own row.');
    check(await compaction(1).locator('.step-label').textContent(), 'Сжатие не удалось', 'The failed row says compaction failed.');
    check(await compaction(1).locator('.step-context').textContent(), 'Auto-compaction failed: Synthetic summary failure', 'The failed row shows Pi\'s error.');
    await activity({ type: 'agent_settled', aborted: false });
    await receive({ type: 'busy', busy: false, canStop: true });
    check(await run().getAttribute('data-state'), 'done', 'agent_settled finishes the card after the compaction.');
    check(await steps(), ['trace:compaction', 'text', 'trace:after-compaction', 'text', 'trace:compaction'], 'Compaction steps keep their place in the timeline.');
    check(await run().locator('.run-footer .copy-button').count(), 1, 'The answer before a trailing compaction can still be copied.');
    check(await run().locator('.text.final').textContent(), 'Итог сходится с допущениями.', 'A compaction after the answer does not hide the final answer.');
    await run().locator('.run-header').click();
    check(await compaction(1).isVisible(), false, 'Collapsing the card hides compaction steps with the rest of the work.');
    await run().locator('.run-header').click();
    await page.locator('#conversation').evaluate(node => { node.scrollTop = node.scrollHeight; });
    await page.screenshot({ path: path.join(output, screenshots[11]) });
    // Overflow: the provider rejected the context, Pi compacts and retries the request.
    await receive({ type: 'message', who: 'Вы', text: 'Сравни все листы.' });
    await activity({ type: 'agent_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    await activity({ type: 'message_end', message: { ...modelMessage([], 'error'), errorMessage: 'Synthetic maximum context length exceeded' } });
    await activity({ type: 'agent_end', willRetry: false });
    await activity({ type: 'compaction_start', reason: 'overflow' });
    check(await compaction().locator('.step-context').textContent(), 'контекст переполнен', 'An overflow compaction says the context overflowed.');
    await activity({ type: 'compaction_end', reason: 'overflow', result: { summary: '', firstKeptEntryId: 'entry-9', tokensBefore: 210000 }, aborted: false, willRetry: true });
    check(await compaction().locator('.step-context').textContent(), 'до сжатия 210,0 тыс. ток. · повтор запроса', 'An overflow compaction says the request is retried.');
    check(await compaction().locator('section').evaluateAll(nodes => nodes.map(node => node.hidden)), [true, true], 'An empty summary and no error leave no detail sections.');
    await activity({ type: 'message_start', message: modelMessage() });
    await activity({ type: 'message_end', message: modelMessage([{ type: 'text', text: 'Листы сравнены.' }]) });
    await activity({ type: 'agent_settled', aborted: false });
    check(await steps(), ['error', 'trace:compaction', 'text'], 'The overflow error, the compaction and the retried answer stay in order.');
    // Pi gives up after one overflow recovery with compaction_end alone; no token count is invented.
    await receive({ type: 'message', who: 'Вы', text: 'Ещё раз.' });
    await activity({ type: 'agent_start' });
    await activity({ type: 'compaction_end', reason: 'overflow', aborted: false, willRetry: false, errorMessage: 'Context overflow recovery failed after one compact-and-retry attempt.' });
    check(await compaction().getAttribute('data-state'), 'error', 'A compaction_end without a start still gets its row.');
    check(await compaction().locator('.step-meta').textContent(), '', 'Without a start there is no duration to show.');
    check((await compaction().textContent()).includes('ток.'), false, 'No token count is shown when Pi gave none.');
    await activity({ type: 'agent_settled', aborted: false });
    // Stop during compaction: Pi aborts it before the request settles.
    await receive({ type: 'message', who: 'Вы', text: 'Остановлю сжатие.' });
    await activity({ type: 'agent_start' });
    await activity({ type: 'compaction_start', reason: 'threshold' });
    await activity({ type: 'compaction_end', reason: 'threshold', aborted: true, willRetry: false });
    await activity({ type: 'agent_settled', aborted: true });
    check(await compaction().getAttribute('data-state'), 'stopped', 'An aborted compaction is stopped, not an error.');
    check(await compaction().locator('.step-label').textContent(), 'Сжатие прервано', 'The aborted row says so.');
    check(await run().getAttribute('data-state'), 'stopped', 'A Stop during compaction stops the card.');
    // The agent ended mid-compaction: the outcome is unknown until a late end arrives.
    await receive({ type: 'message', who: 'Вы', text: 'Агент упадёт при сжатии.' });
    await activity({ type: 'agent_start' });
    await activity({ type: 'compaction_start', reason: 'threshold' });
    await activity({ type: 'process_exit' });
    check(await compaction().getAttribute('data-state'), 'unknown', 'A card ended during compaction must not claim the compaction finished.');
    check(await compaction().locator('.step-label').textContent(), 'Сжатие не завершено', 'The row says the compaction did not finish.');
    await activity({ type: 'compaction_end', reason: 'threshold', result: { summary: 's', firstKeptEntryId: 'e', tokensBefore: 99000 }, aborted: false, willRetry: false });
    check(await compaction().locator('.step-label').textContent(), 'Контекст сжат', 'A late compaction_end still updates the same row.');
    check(await run().locator('.step.compaction').count(), 1, 'A late compaction_end must not add a row.');

    // ---------------------------------------------------------------- layout
    for (const width of [320, 410, 760]) {
      await page.setViewportSize({ width, height: 850 });
      const header = await page.locator('.header').evaluate(node => {
        const box = node.getBoundingClientRect();
        const buttons = Array.from(node.querySelectorAll('button')).map(button => button.getBoundingClientRect());
        return { height: box.height, right: box.right - Math.max(...buttons.map(button => button.right)),
          left: Math.min(...buttons.map(button => button.left)) - box.left,
          hit: buttons.every(button => button.width >= 28 && button.height >= 28 && button.top >= box.top && button.bottom <= box.bottom) };
      });
      check(header.height >= 32 && header.height <= 38, true, 'Header must stay one row no taller than its buttons need at width ' + width + '.');
      check(header.hit, true, 'Header buttons must keep a 28 px hit area inside the header at width ' + width + '.');
      check(header.right <= 12 && header.left > header.right, true, 'Header buttons must sit at the right edge at width ' + width + '.');
      check(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false, 'Pane must fit width ' + width + '.');
    }
    await page.setViewportSize({ width: 320, height: 650 });
    await page.locator('#conversation').evaluate(node => { node.scrollTop = 0; });
    await page.screenshot({ path: path.join(output, screenshots[4]) });
    await page.setViewportSize({ width: 410, height: 850 });
    await page.emulateMedia({ colorScheme: 'light' });
    const lightBackground = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
    check(lightBackground !== 'rgb(28, 29, 32)', true, 'Light Office/Windows theme must switch the palette.');
    check(await page.locator('.toolbar button').evaluateAll(nodes => nodes.map(node => node.checkVisibility() && getComputedStyle(node).color !== getComputedStyle(document.body).backgroundColor)),
      [true, true], 'New chat and Settings must stay visible in the light theme.');
    await page.screenshot({ path: path.join(output, screenshots[5]) });
    await page.emulateMedia({ colorScheme: 'dark' });

    // ---------------------------------------------------------------- every call is visible from formation to result
    await receive({ type: 'clear_transcript' });
    await receive({ type: 'message', who: 'Вы', text: 'Покажи все вызовы инструментов, включая формирующиеся.' });
    await activity({ type: 'agent_start' });
    await activity({ type: 'turn_start' });
    await activity({ type: 'message_start', message: modelMessage() });
    await update({ type: 'toolcall_start', contentIndex: 0, id: 'custom-1', toolName: 'private_lookup' });
    check(await tool('custom-1').isVisible(), true, 'A call row must appear before execution begins.');
    check(await tool('custom-1').getAttribute('data-state'), 'generating', 'toolcall_start must show formation, not execution.');
    check(await tool('custom-1').evaluate(node => node.open), false, 'Only call details stay collapsed.');
    await update({ type: 'toolcall_delta', contentIndex: 0, delta: '{"sheet":"DCF",' });
    await update({ type: 'toolcall_delta', contentIndex: 0, delta: '"address":"B1:C3"}' });
    await tool('custom-1').locator('summary').click();
    await paint();
    check(await tool('custom-1').locator('pre').first().textContent(), '{"sheet":"DCF","address":"B1:C3"}', 'Argument deltas must accumulate within the call row.');
    check(await tool('custom-1').locator('.step-context').textContent(), 'DCF!B1:C3', 'Complete streamed arguments must update the row context.');
    const firstCall = { type: 'toolCall', id: 'custom-1', name: 'private_lookup', arguments: { sheet: 'DCF', address: 'B1:C4' } };
    await update({ type: 'toolcall_end', contentIndex: 0, toolCall: firstCall });
    check((await tool('custom-1').locator('pre').first().textContent()).includes('B1:C4'), true, 'Authoritative completed arguments must replace streamed JSON.');
    check(await tool('custom-1').getAttribute('data-state'), 'queued', 'A completed call must await execution.');
    await tool('custom-1').locator('summary').click();
    await update({ type: 'toolcall_start', contentIndex: 1, id: 'custom-2', toolName: 'private_lookup' });
    const secondCall = { type: 'toolCall', id: 'custom-2', name: 'private_lookup', arguments: { sheet: 'Inputs', address: 'A1:B5' } };
    await update({ type: 'toolcall_end', contentIndex: 1, toolCall: secondCall });
    await update({ type: 'toolcall_start', contentIndex: 2, id: 'custom-3', toolName: 'external_metrics' });
    check(await run().locator('.tool').count(), 3, 'Three unknown calls must have three rows.');
    check(await run().locator('.tool .step-label').allTextContents(), ['private_lookup', 'private_lookup', 'external_metrics'], 'Repeated names and unknown tools must remain independently visible.');
    check(await run().locator('.tool').evaluateAll(nodes => nodes.every(node => node.getBoundingClientRect().height > 0)), true, 'All call rows must be displayed by default.');
    await activity({ type: 'tool_execution_start', toolCallId: 'custom-1', toolName: 'private_lookup', args: firstCall.arguments });
    check(await run().locator('.tool').count(), 3, 'Execution start must reuse the generated call row.');
    check(await tool('custom-1').getAttribute('data-state'), 'running', 'Execution start must advance only that call.');
    check((await run().locator('.run-title').textContent()).startsWith('private_lookup · DCF!B1:C4'), true, 'Run header must show the running call.');
    const firstResult = { type: 'tool_execution_end', toolCallId: 'custom-1', result: { value: 42 }, isError: false };
    await activity(firstResult);
    await activity(firstResult);
    check(await run().locator('.tool').count(), 3, 'Repeated callbacks for the same ID must not duplicate the row.');
    check(await tool('custom-1').getAttribute('data-state'), 'done', 'Completed execution must keep its result state.');
    check(await run().locator('.run-title').textContent(), 'Формирую вызов: external_metrics', 'Run header must keep pending generation after another call completes.');
    check(await tool('custom-3').locator('.step-meta').textContent(), 'формируется', 'A forming call must say so.');
    await page.locator('#conversation').evaluate(node => { node.scrollTop = node.scrollHeight; });
    await page.screenshot({ path: path.join(output, screenshots[6]) });
    const thirdCall = { type: 'toolCall', id: 'custom-3', name: 'external_metrics', arguments: { metric: 'growth' } };
    const fallbackCall = { type: 'toolCall', id: 'fallback-4', name: 'fallback_only', arguments: { source: 'final-message' } };
    await activity({ type: 'message_end', message: modelMessage([firstCall, secondCall, thirdCall, fallbackCall], 'toolUse') });
    check(await run().locator('.tool').count(), 4, 'Final message fallback must add a call with no streaming events.');
    check(await tool('fallback-4').isVisible(), true, 'Fallback-only call must be visible immediately.');
    check(await tool('custom-1').getAttribute('data-state'), 'done', 'Final fallback must not downgrade an executed call.');
    await activity({ type: 'message_end', message: modelMessage([firstCall, secondCall, thirdCall, fallbackCall], 'toolUse') });
    check(await run().locator('.tool').count(), 4, 'Repeated authoritative fallback must not duplicate calls.');
    await activity({ type: 'tool_execution_start', toolCallId: 'custom-2', toolName: 'private_lookup', args: secondCall.arguments });
    await activity({ type: 'agent_settled', aborted: true });
    check(await run().getAttribute('data-state'), 'stopped', 'An aborted settlement must mark the run stopped.');
    check(await tool('custom-1').getAttribute('data-state'), 'done', 'Abort must preserve earlier completed calls.');
    check(await tool('custom-2').getAttribute('data-state'), 'unknown', 'Abort must not claim that an in-flight COM operation stopped.');
    check(await tool('custom-3').getAttribute('data-state'), 'not_run', 'Abort before execution must never claim success.');
    check(await tool('fallback-4').getAttribute('data-state'), 'not_run', 'Unexecuted fallback call must keep an explicit not-run status.');
    check(await run().locator('.text.final').count(), 0, 'A run that ends on a step has no final answer to promote.');
    check(errors, [], 'UI must not raise uncaught JavaScript errors.');
    check(network, [], 'Local UI must not request external resources.');
    console.log(JSON.stringify({ passed: true, checks, screenshots: screenshots.map(name => 'work/' + name), consoleErrors: errors, externalRequests: network }));
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
