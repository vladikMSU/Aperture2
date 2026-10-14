// Browser-only regression for the actual local HTML/CSS/JS. The bridge and audit
// findings below are synthetic fixtures, not results from Excel or an LLM.
const { chromium } = require('playwright');
const assert = require('assert');
const fs = require('fs');
const path = require('path');
const { pathToFileURL } = require('url');

const root = path.resolve(__dirname, '../..');
const output = path.join(root, 'work');
const screenshots = ['chat-webview-welcome.png', 'chat-webview-conversation.png', 'chat-webview-narrow.png'];
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
    check(errors, [], 'UI must not raise uncaught JavaScript errors.');
    check(network, [], 'Local UI must not request external resources.');
    console.log(JSON.stringify({ passed: true, checks, screenshots: screenshots.map(name => 'work/' + name), consoleErrors: errors, externalRequests: network }));
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
