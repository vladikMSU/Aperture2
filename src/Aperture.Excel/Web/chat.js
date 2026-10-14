(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const conversation = $('conversation');
  const messages = $('messages');
  const prompt = $('prompt');
  const bridge = window.chrome && window.chrome.webview;
  let busy = false;
  let canStop = true;
  let awaitingClear = false;
  let currentRun = null;
  let runSequence = 0;
  let renderTimer = 0;
  let renderFrame = 0;
  const markdownUpdates = new Map();

  function post(command, text = '') { if (bridge) bridge.postMessage({ command, text }); }

  // ---------------------------------------------------------------- DOM helpers
  const SVG = 'http://www.w3.org/2000/svg';
  const ICONS = {
    chevron: 'M6 3.5 10.5 8 6 12.5',
    check: 'M3.5 8.5 6.5 11.5 12.5 4.5',
    cross: 'M4.5 4.5l7 7M11.5 4.5l-7 7',
    dash: 'M4.5 8h7',
    question: 'M6 6.2a2 2 0 1 1 2.8 1.8c-.5.3-.8.6-.8 1.2v.5M8 11.8v.2',
    spark: 'M8 2.5 9.2 6.8 13.5 8 9.2 9.2 8 13.5 6.8 9.2 2.5 8 6.8 6.8z',
    alert: 'M8 2.5 14 13H2zM8 6.5v3M8 11.2v.2',
    retry: 'M13 5.5A5.5 5.5 0 1 0 13.4 9M13 2.5v3h-3',
    book: 'M3 3.5h3.5A1.5 1.5 0 0 1 8 5v8a1.5 1.5 0 0 0-1.5-1.5H3zM13 3.5H9.5A1.5 1.5 0 0 0 8 5v8a1.5 1.5 0 0 1 1.5-1.5H13z',
    grid: 'M2.5 3.5h11v9h-11zM2.5 6.5h11M2.5 9.5h11M6.5 3.5v9',
    pencil: 'M10.5 3 13 5.5 6 12.5H3.5V10z',
    formula: 'M7.5 3.5c-1.4 0-2 .8-2.2 2L4.2 12.5M3.2 6.5h3.6M9 7.5l3.5 5M12.5 7.5 9 12.5',
    search: 'M7 11.5a4.5 4.5 0 1 1 0-9 4.5 4.5 0 0 1 0 9zM10.3 10.3l3.2 3.2',
    tag: 'M2.5 2.5h5l6 6-5 5-6-6zM5.5 5.5h.1',
    plus: 'M8 3.5v9M3.5 8h9',
    brush: 'M9.5 2.5l4 4-5 5-4-4zM4.5 7.5l-2 6 6-2',
    pulse: 'M2 8.5h3l1.5-4 3 7 1.5-3H14',
    tool: 'M10 2.8a3 3 0 0 0-3.7 3.9L2.8 10.2l3 3 3.5-3.5A3 3 0 0 0 13.2 6l-1.8 1.8-2.2-.3-.3-2.2z',
    copy: 'M5.5 5.5h7v8h-7zM3.5 10.5v-8h7'
  };
  function icon(name, className = 'icon') {
    const svg = document.createElementNS(SVG, 'svg');
    svg.setAttribute('viewBox', '0 0 16 16');
    svg.setAttribute('aria-hidden', 'true');
    svg.setAttribute('class', className);
    const path = document.createElementNS(SVG, 'path');
    path.setAttribute('d', ICONS[name] || ICONS.tool);
    svg.appendChild(path);
    return svg;
  }
  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }

  // ---------------------------------------------------------------- formatting
  function plural(count, forms) {
    const n10 = count % 10, n100 = count % 100;
    const form = n10 === 1 && n100 !== 11 ? 0 : n10 >= 2 && n10 <= 4 && (n100 < 12 || n100 > 14) ? 1 : 2;
    return count + ' ' + forms[form];
  }
  function seconds(ms) {
    if (!(ms >= 0)) return '';
    if (ms < 10000) return (ms / 1000).toFixed(1).replace('.', ',') + ' с';
    const total = Math.round(ms / 1000);
    return total < 60 ? total + ' с' : Math.floor(total / 60) + ' мин ' + (total % 60) + ' с';
  }
  function elapsed(ms) {
    const total = Math.max(0, Math.floor(ms / 1000));
    return total < 60 ? total + ' с' : Math.floor(total / 60) + ' мин ' + (total % 60) + ' с';
  }
  function tokens(count) {
    return count < 1000 ? String(count) : (count / 1000).toFixed(1).replace('.', ',') + ' тыс.';
  }
  function plainLine(text, tail) {
    const plain = String(text).replace(/[`*_#>|]+/g, '').replace(/\s+/g, ' ').trim();
    if (!tail) return plain.length > 160 ? plain.slice(0, 160) + '…' : plain;
    return plain.length > 120 ? '…' + plain.slice(-120) : plain;
  }

  // ---------------------------------------------------------------- Markdown
  // Builds DOM nodes from model text and never parses it as HTML. Links are shown as text
  // (URL in the tooltip) so a reply cannot navigate the pane; there are no images or raw markup.
  const INLINE = /(`+)([^`]|[^`][\s\S]*?[^`])\1(?!`)|\[([^\]\n]+)\]\(([^)\s]+)\)|\*\*(?=\S)([\s\S]*?\S)\*\*|__(?=\S)([\s\S]*?\S)__|~~(?=\S)([\s\S]*?\S)~~|\*(?=[^\s*])([\s\S]*?[^\s*])\*|(?<![\p{L}\p{N}_])_(?=[^\s_])([\s\S]*?[^\s_])_(?![\p{L}\p{N}_])/gu;
  function inline(target, text) {
    let start = 0, match;
    const pattern = new RegExp(INLINE.source, 'gu');
    while ((match = pattern.exec(text))) {
      if (match.index > start) target.appendChild(document.createTextNode(text.slice(start, match.index)));
      let node;
      if (match[1]) node = element('code', '', match[2]);
      else if (match[3]) { node = element('span', 'link'); inline(node, match[3]); node.title = match[4]; }
      else if (match[5] || match[6]) { node = element('strong'); inline(node, match[5] || match[6]); }
      else if (match[7]) { node = element('del'); inline(node, match[7]); }
      else { node = element('em'); inline(node, match[8] || match[9]); }
      target.appendChild(node);
      start = pattern.lastIndex;
    }
    if (start < text.length) target.appendChild(document.createTextNode(text.slice(start)));
  }
  const FENCE = /^\s{0,3}(`{3,}|~{3,})\s*([^\s`]*)/;
  const RULE = /^\s{0,3}([-*_])(?:[ \t]*\1){2,}\s*$/;
  const HEADING = /^\s{0,3}(#{1,6})\s+(.*?)(?:\s+#+)?\s*$/;
  const QUOTE = /^\s{0,3}>\s?(.*)$/;
  const LIST = /^(\s*)([-*+]|\d{1,9}[.)])\s+(.*)$/;
  function tableCells(line) {
    return line.trim().replace(/^\|/, '').replace(/(?<!\\)\|$/, '').split(/(?<!\\)\|/).map(cell => cell.trim().replace(/\\\|/g, '|'));
  }
  function isTableDivider(line) { return /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)*\|?\s*$/.test(line) && line.includes('-') && line.includes('|'); }
  function startsBlock(lines, index) {
    const line = lines[index];
    return FENCE.test(line) || RULE.test(line) || HEADING.test(line) || QUOTE.test(line) || LIST.test(line) ||
      (index + 1 < lines.length && line.includes('|') && isTableDivider(lines[index + 1]));
  }
  function renderMarkdown(target, source) {
    target.replaceChildren();
    renderBlocks(target, String(source).replace(/\r\n?/g, '\n').split('\n'));
  }
  function renderBlocks(target, lines) {
    let index = 0;
    while (index < lines.length) {
      const line = lines[index];
      if (!line.trim()) { index++; continue; }
      let match;
      if ((match = FENCE.exec(line))) {
        const close = new RegExp('^\\s{0,3}' + (match[1][0] === '`' ? '`' : '~') + '{' + match[1].length + ',}\\s*$');
        const block = [];
        index++;
        while (index < lines.length && !close.test(lines[index])) block.push(lines[index++]);
        if (index < lines.length) index++;
        const pre = element('pre'), code = element('code', '', block.join('\n'));
        if (match[2]) pre.dataset.lang = match[2];
        pre.appendChild(code); target.appendChild(pre);
        continue;
      }
      if (RULE.test(line)) { target.appendChild(element('hr')); index++; continue; }
      if ((match = HEADING.exec(line))) {
        const node = element('h' + Math.min(4, match[1].length));
        inline(node, match[2]); target.appendChild(node); index++;
        continue;
      }
      if (QUOTE.test(line)) {
        const quoted = [];
        while (index < lines.length && (match = QUOTE.exec(lines[index]))) { quoted.push(match[1]); index++; }
        const node = element('blockquote');
        renderBlocks(node, quoted); target.appendChild(node);
        continue;
      }
      if (index + 1 < lines.length && line.includes('|') && isTableDivider(lines[index + 1])) {
        const align = tableCells(lines[index + 1]).map(cell => /^:-+:$/.test(cell) ? 'center' : /-:$/.test(cell) ? 'right' : '');
        const wrap = element('div', 'table-wrap'), table = element('table');
        const head = element('thead'), headRow = element('tr');
        tableCells(line).forEach((cell, column) => { const th = element('th', align[column] ? 'align-' + align[column] : ''); inline(th, cell); headRow.appendChild(th); });
        head.appendChild(headRow); table.appendChild(head);
        const body = element('tbody');
        index += 2;
        while (index < lines.length && lines[index].includes('|') && lines[index].trim()) {
          const row = element('tr');
          tableCells(lines[index++]).forEach((cell, column) => { const td = element('td', align[column] ? 'align-' + align[column] : ''); inline(td, cell); row.appendChild(td); });
          body.appendChild(row);
        }
        table.appendChild(body); wrap.appendChild(table); target.appendChild(wrap);
        continue;
      }
      if (LIST.test(line)) { index = renderList(target, lines, index); continue; }
      const paragraph = element('p');
      inline(paragraph, line.trim());
      index++;
      while (index < lines.length && lines[index].trim() && !startsBlock(lines, index)) {
        paragraph.appendChild(element('br'));
        inline(paragraph, lines[index++].trim());
      }
      target.appendChild(paragraph);
    }
  }
  function renderList(target, lines, index) {
    const stack = [];
    const width = text => text.replace(/\t/g, '    ').length;
    const openList = (ordered, start, parent) => {
      const list = element(ordered ? 'ol' : 'ul');
      if (ordered && start !== 1) list.start = start;
      parent.appendChild(list);
      return list;
    };
    while (index < lines.length) {
      const line = lines[index];
      const match = LIST.exec(line);
      if (match) {
        const indent = width(match[1]), ordered = /\d/.test(match[2]), start = ordered ? parseInt(match[2], 10) : 1;
        while (stack.length && indent < stack[stack.length - 1].indent - 1) stack.pop();
        let top = stack[stack.length - 1];
        if (!top || (indent >= top.indent + 2 && top.item)) {
          top = { indent, ordered, list: openList(ordered, start, top ? top.item : target), item: null };
          stack.push(top);
        } else if (top.ordered !== ordered) {
          const parent = stack.length > 1 ? stack[stack.length - 2].item : target;
          top.list = openList(ordered, start, parent); top.ordered = ordered;
        }
        const item = element('li');
        inline(item, match[3]);
        top.list.appendChild(item); top.item = item;
        index++;
        continue;
      }
      if (!line.trim()) {
        let next = index + 1;
        while (next < lines.length && !lines[next].trim()) next++;
        if (next < lines.length && (LIST.test(lines[next]) || /^\s{2,}\S/.test(lines[next]))) { index = next; continue; }
        break;
      }
      if (/^\s{2,}\S/.test(line) && stack.length && stack[stack.length - 1].item && !startsBlock(lines, index)) {
        const item = stack[stack.length - 1].item;
        item.appendChild(element('br')); inline(item, line.trim());
        index++;
        continue;
      }
      break;
    }
    return index;
  }

  // ---------------------------------------------------------------- scrolling and batched rendering
  function nearEnd() { return conversation.scrollHeight - conversation.scrollTop - conversation.clientHeight < 80; }
  function scrollEnd() { conversation.scrollTop = conversation.scrollHeight; }
  function withScroll(action, forceEnd = false) {
    // Follow the tail only when the reader is already there; CSS scroll anchoring keeps
    // the position of a reader who scrolled up to earlier text.
    const follow = forceEnd || nearEnd();
    action();
    if (follow) scrollEnd();
  }
  function flushMarkdown() {
    clearTimeout(renderTimer); renderTimer = 0;
    cancelAnimationFrame(renderFrame); renderFrame = 0;
    if (!markdownUpdates.size) return;
    withScroll(() => {
      markdownUpdates.forEach((text, node) => { if (node.isConnected) renderMarkdown(node, text); });
      markdownUpdates.clear();
    });
  }
  function queueMarkdown(node, text) {
    markdownUpdates.set(node, text);
    if (!renderTimer && !renderFrame) renderTimer = setTimeout(() => {
      renderTimer = 0;
      renderFrame = requestAnimationFrame(flushMarkdown);
    }, 45);
  }

  // ---------------------------------------------------------------- composer
  function autosize() {
    prompt.style.height = 'auto';
    prompt.style.height = Math.min(prompt.scrollHeight, 180) + 'px';
  }
  function applyBusy() {
    prompt.readOnly = busy && awaitingClear;
    const send = $('send'), stop = $('stop');
    send.hidden = busy; stop.hidden = !busy;
    send.disabled = busy || !prompt.value.trim();
    stop.disabled = !canStop;
    $('status').classList.toggle('working', busy);
    ['new-chat', 'rebind', 'settings'].forEach(id => { $(id).disabled = busy; });
    document.querySelectorAll('[data-prompt]').forEach(button => { button.disabled = busy; });
  }
  function submit() {
    const text = prompt.value.trim();
    if (busy || !text) return;
    awaitingClear = true;
    post('send', text);
  }
  $('send').addEventListener('click', submit);
  $('stop').addEventListener('click', () => post('stop'));
  $('new-chat').addEventListener('click', () => post('new_chat'));
  $('rebind').addEventListener('click', () => post('rebind'));
  $('settings').addEventListener('click', () => post('settings'));
  prompt.addEventListener('input', () => { autosize(); applyBusy(); });
  prompt.addEventListener('keydown', event => {
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) { event.preventDefault(); submit(); }
  });
  document.querySelectorAll('[data-prompt]').forEach(button => button.addEventListener('click', () => {
    prompt.value = button.dataset.prompt;
    autosize(); applyBusy(); prompt.focus();
  }));

  // ---------------------------------------------------------------- conversation messages
  function hideWelcome() { $('welcome').hidden = true; }
  function appendMessage(who, text) {
    hideWelcome();
    let node;
    if (who === 'Вы') {
      currentRun = null;
      node = element('div', 'message user');
      node.appendChild(element('div', 'bubble', text));
    } else if (/^Ошибка/.test(who)) {
      node = element('div', 'message error');
      node.appendChild(icon('alert', 'message-icon'));
      const copy = element('div', 'message-copy');
      if (who !== 'Ошибка') copy.appendChild(element('strong', '', who + '. '));
      copy.appendChild(document.createTextNode(text));
      node.appendChild(copy);
    } else if (who === 'Система') {
      node = element('div', 'message system', text);
    } else {
      node = element('div', 'message assistant markdown');
      renderMarkdown(node, text);
    }
    messages.appendChild(node);
    return node;
  }

  // ---------------------------------------------------------------- runs: one chronological timeline per request
  const TOOLS = {
    excel_target: ['Обзор книги', 'book'],
    excel_read_range: ['Чтение диапазона', 'grid'],
    excel_write_values: ['Запись значений', 'pencil'],
    excel_write_formula: ['Запись формулы', 'formula'],
    excel_recalculate: ['Пересчёт', 'retry'],
    excel_find: ['Поиск', 'search'],
    excel_names: ['Именованные диапазоны', 'tag'],
    excel_add_sheet: ['Добавление листа', 'plus'],
    excel_format: ['Оформление диапазона', 'brush'],
    excel_operation_status: ['Состояние операции', 'pulse']
  };
  const STATE_TEXT = {
    generating: 'формируется', queued: 'в очереди', running: 'выполняется', done: 'готово', error: 'ошибка',
    stopped: 'остановлено', not_run: 'не исполнен', unknown: 'нет результата'
  };
  function ensureRun() {
    if (currentRun) return currentRun;
    hideWelcome();
    const node = element('article', 'run');
    node.dataset.run = String(++runSequence);
    const header = element('button', 'run-header');
    header.type = 'button';
    header.setAttribute('aria-expanded', 'true');
    const title = element('span', 'run-title');
    const meta = element('span', 'run-meta');
    header.append(icon('chevron', 'run-chevron'), element('span', 'spinner'), title, meta);
    const body = element('div', 'run-body');
    const footer = element('div', 'run-footer');
    footer.hidden = true;
    node.append(header, body, footer);
    messages.appendChild(node);
    const run = {
      node, header, title, meta, body, footer, tools: new Map(), models: [], model: null, lastGroup: null,
      startedAt: Date.now(), finishedAt: 0, finished: false, status: 'running', current: 'Начинаю работу',
      modelNames: [], outputTokens: 0, streamMs: 0, pendingSequence: 0
    };
    header.addEventListener('click', () => {
      const collapsed = !node.classList.contains('collapsed');
      node.classList.toggle('collapsed', collapsed);
      header.setAttribute('aria-expanded', String(!collapsed));
    });
    currentRun = run;
    updateRun(run);
    return run;
  }
  function updateRun(run) {
    const actions = run.tools.size;
    const time = elapsed((run.finishedAt || Date.now()) - run.startedAt);
    run.node.dataset.state = run.finished ? run.status : 'running';
    run.title.textContent = run.finished
      ? ({ done: 'Ход работы', stopped: 'Остановлено', error: 'Завершено с ошибкой' }[run.status] || 'Ход работы')
      : run.current;
    run.title.title = run.title.textContent;
    run.meta.textContent = [actions ? plural(actions, ['действие', 'действия', 'действий']) : '', time].filter(Boolean).join(' · ');
  }
  function setCurrent(run, text) { run.current = text; updateRun(run); }
  function place(run, node, kind, model, index) {
    // Steps between text form one compact trace group; text and errors stay full width.
    const reference = model && Number.isInteger(index) ? nextPartNode(model, index) : null;
    const flowing = kind === 'text' || kind === 'error';
    if (!reference) {
      if (flowing) { run.body.appendChild(node); return; }
      let group = run.body.lastElementChild;
      if (!group || !group.classList.contains('trace')) { group = element('div', 'trace'); run.body.appendChild(group); }
      group.appendChild(node);
      return;
    }
    const group = reference.parentElement.classList.contains('trace') ? reference.parentElement : null;
    if (flowing) {
      if (!group) { reference.before(node); return; }
      if (reference === group.firstElementChild) { group.before(node); return; }
      const tail = element('div', 'trace');
      for (let item = reference; item;) { const next = item.nextElementSibling; tail.appendChild(item); item = next; }
      group.after(tail); tail.before(node);
      return;
    }
    if (group) { group.insertBefore(node, reference); return; }
    const previous = reference.previousElementSibling;
    if (previous && previous.classList.contains('trace')) { previous.appendChild(node); return; }
    const created = element('div', 'trace');
    reference.before(created); created.appendChild(node);
  }
  function nextPartNode(model, index) {
    let best = null;
    model.parts.forEach((part, key) => {
      if (Number.isInteger(key) && key > index && part.node.isConnected && (!best || key < best.key)) best = { key, node: part.node };
    });
    return best && best.node;
  }
  function removeNode(node) {
    const group = node.parentElement;
    node.remove();
    if (group && group.classList.contains('trace') && !group.firstElementChild) group.remove();
  }
  function setState(item, state) {
    item.state = state;
    item.node.dataset.state = state;
    item.stateNode.replaceChildren(
      state === 'running' || state === 'generating' ? element('span', 'spinner') :
      icon({ done: 'check', error: 'cross', not_run: 'dash', unknown: 'question', stopped: 'dash', queued: 'dash' }[state] || 'dash', 'state-icon'));
    item.stateNode.title = STATE_TEXT[state] || state;
  }
  function stepNode(kind, iconName, label) {
    const node = element('details', 'step ' + kind);
    const summary = element('summary');
    const labelNode = element('span', 'step-label', label);
    const context = element('span', 'step-context');
    const meta = element('span', 'step-meta');
    const stateNode = element('span', 'step-state');
    summary.append(icon(iconName, 'step-icon'), labelNode, context, meta, stateNode);
    const detail = element('div', 'step-detail');
    node.append(summary, detail);
    return { node, labelNode, context, meta, stateNode, detail };
  }

  // Model messages: each content part (reasoning, text, tool call) is a step at its own position.
  function startModel(message) {
    const run = ensureRun();
    const model = { parts: new Map(), startedAt: Date.now() };
    run.models.push(model); run.model = model;
    if (message) noteModel(run, message);
    setCurrent(run, 'Думаю');
    return model;
  }
  function currentModel() { const run = ensureRun(); return run.model || startModel(null); }
  function noteModel(run, message) {
    const name = message.responseModel || message.model;
    if (name && !run.modelNames.includes(name)) run.modelNames.push(name);
  }
  function partIndex(model, update, kind) {
    if (Number.isInteger(update.contentIndex)) return update.contentIndex;
    let last = 'auto-' + kind;
    model.parts.forEach((part, key) => { if (part.kind === kind) last = key; });
    return last;
  }
  function reasoningPart(model, index) {
    let part = model.parts.get(index);
    if (part && part.kind === 'reasoning') return part;
    const run = ensureRun();
    const step = stepNode('reasoning', 'spark', 'Рассуждение');
    step.detail.classList.add('markdown');
    part = Object.assign(step, { kind: 'reasoning', text: '', startedAt: Date.now(), endedAt: 0 });
    step.node.addEventListener('toggle', () => { if (step.node.open) queueMarkdown(step.detail, part.text); });
    place(run, step.node, 'reasoning', model, index);
    model.parts.set(index, part);
    setState(part, 'running');
    return part;
  }
  function setReasoning(model, index, text, redacted, complete) {
    const existing = model.parts.get(index);
    if (redacted || text === '') {
      // Redacted or empty provider reasoning is never displayed, including earlier partial text.
      if (existing && existing.kind === 'reasoning') { markdownUpdates.delete(existing.detail); removeNode(existing.node); model.parts.delete(index); }
      return;
    }
    if (typeof text !== 'string') return;
    const part = reasoningPart(model, index);
    part.text = text;
    part.context.textContent = plainLine(text, !complete);
    if (complete && part.state !== 'done') {
      part.endedAt = Date.now();
      // Reasoning that arrived whole (no stream) has no meaningful duration.
      part.meta.textContent = part.endedAt - part.startedAt >= 100 ? seconds(part.endedAt - part.startedAt) : '';
      setState(part, 'done');
    }
    if (part.node.open) queueMarkdown(part.detail, text);
  }
  function textPart(model, index) {
    let part = model.parts.get(index);
    if (part && part.kind === 'text') return part;
    const run = ensureRun();
    const node = element('div', 'step text markdown');
    part = { kind: 'text', node, text: '' };
    place(run, node, 'text', model, index);
    model.parts.set(index, part);
    return part;
  }
  function setText(model, index, text) {
    const existing = model.parts.get(index);
    if (!text) {
      if (existing && existing.kind === 'text') { markdownUpdates.delete(existing.node); removeNode(existing.node); model.parts.delete(index); }
      return;
    }
    const part = textPart(model, index);
    part.text = text;
    queueMarkdown(part.node, text);
  }

  // Tool calls are keyed by toolCallId across formation (toolcall_*) and execution callbacks.
  function toolLabel(rawName) {
    const short = String(rawName || '').split('__').pop();
    return Object.prototype.hasOwnProperty.call(TOOLS, short) ? TOOLS[short] : [rawName || 'Инструмент', 'tool'];
  }
  function ensureTool(run, id, name, previous) {
    let tool = id ? run.tools.get(id) : null;
    if (tool && previous && previous !== tool) {
      // A late final block revealed an ID already seen in execution callbacks: keep that card.
      run.tools.delete(previous.key);
      removeNode(previous.node);
      run.models.forEach(model => model.parts.forEach((part, key) => { if (part === previous) model.parts.set(key, tool); }));
    }
    if (!tool) tool = previous && previous.kind === 'tool' ? previous : null;
    if (!tool) {
      const step = stepNode('tool', 'tool', 'Инструмент');
      tool = Object.assign(step, { kind: 'tool', key: id || 'pending-' + (++run.pendingSequence), executed: false, argumentText: '', updates: 0 });
      tool.info = element('p', 'step-info');
      tool.argsSection = detailSection('Аргументы');
      tool.outputSection = detailSection('Результат');
      tool.detail.append(tool.info, tool.argsSection.section, tool.outputSection.section);
      tool.node.addEventListener('toggle', () => renderTool(tool));
      run.tools.set(tool.key, tool);
      setToolState(tool, 'generating');
    }
    if (id && tool.key !== id) { run.tools.delete(tool.key); tool.key = id; run.tools.set(id, tool); }
    if (id) { tool.id = id; tool.node.dataset.toolCallId = id; }
    if (name) tool.rawName = name;
    const [label, iconName] = toolLabel(tool.rawName);
    tool.labelNode.textContent = label;
    tool.labelNode.title = tool.rawName || 'Имя формируется';
    tool.node.querySelector('summary > .step-icon').replaceWith(icon(iconName, 'step-icon'));
    tool.info.textContent = (tool.rawName || 'Имя формируется') + ' · toolCallId: ' + (tool.id || 'ещё не получен');
    updateRun(run);
    return tool;
  }
  function detailSection(title) {
    const section = element('section');
    const content = element('pre');
    section.append(element('h4', '', title), content);
    section.hidden = true;
    return { section, content };
  }
  function toolContext(args) {
    if (!args || typeof args !== 'object') return '';
    const address = args.address || args.cell || args.startCell || args.range;
    const parts = [];
    if (typeof args.sheet === 'string' && typeof address === 'string') parts.push(quoteSheet(args.sheet) + '!' + address);
    else if (typeof args.sheet === 'string') parts.push(args.sheet);
    else if (typeof address === 'string') parts.push(address);
    if (typeof args.text === 'string') parts.push('«' + args.text + '»');
    if (typeof args.name === 'string' && !parts.length) parts.push(args.name);
    return parts.join(' · ');
  }
  function quoteSheet(sheet) { return /^[\p{L}\p{N}_]+$/u.test(sheet) ? sheet : "'" + sheet.replace(/'/g, "''") + "'"; }
  function setToolArguments(tool, args) {
    if (args === undefined) return;
    tool.args = args;
    let parsed = args;
    if (typeof args === 'string') { try { parsed = JSON.parse(args); } catch (error) { parsed = null; } }
    const context = toolContext(parsed);
    if (context || typeof args !== 'string') tool.context.textContent = context;
  }
  function formatPayload(value) {
    let text;
    if (typeof value === 'string') text = value;
    else if (value && Array.isArray(value.content) && value.content.every(part => part && (part.type === 'text' || part.type === 'image'))) {
      text = value.content.map(part => part.type === 'text' ? part.text : '[Изображение не показано в текстовой ленте]').join('\n');
      try { text = JSON.stringify(JSON.parse(text), null, 2); } catch (error) { /* plain text result */ }
    } else {
      // Tool images/binary payloads are not useful in a JSON preview. Never embed them.
      text = JSON.stringify(value, (key, item) => item && typeof item === 'object' && item.type === 'image'
        ? { type: 'image', mimeType: item.mimeType, preview: 'Изображение не показано в текстовой ленте' } : item, 2) || '';
    }
    const limit = 40000;
    return text.length > limit ? text.slice(0, limit) + '\n… Показаны первые ' + limit + ' символов результата.' : text;
  }
  function renderTool(tool) {
    if (!tool.node.open) return; // Large Excel results stay lazy while collapsed.
    tool.argsSection.section.hidden = tool.args === undefined;
    tool.outputSection.section.hidden = tool.output === undefined;
    if (tool.args !== undefined) tool.argsSection.content.textContent = formatPayload(tool.args);
    if (tool.output !== undefined) tool.outputSection.content.textContent = formatPayload(tool.output);
  }
  function setToolState(tool, state, durationMs) {
    setState(tool, state);
    tool.meta.textContent = state === 'done' ? (durationMs >= 0 ? seconds(durationMs) : '') :
      state === 'running' ? '' : STATE_TEXT[state] || '';
  }
  function formedTool(model, update) {
    const run = ensureRun();
    const index = partIndex(model, update, 'tool');
    const call = update.toolCall;
    const previous = model.parts.get(index);
    const tool = ensureTool(run, call ? call.id : update.id, call ? call.name : update.toolName, previous && previous.kind === 'tool' ? previous : null);
    if (!tool.node.isConnected) place(run, tool.node, 'tool', model, index);
    model.parts.set(index, tool);
    if (update.type === 'toolcall_delta' && !tool.executed && tool.state === 'generating') {
      tool.argumentText += update.delta || '';
      setToolArguments(tool, tool.argumentText);
    }
    if (call) {
      setToolArguments(tool, call.arguments);
      if (tool.state === 'generating') setToolState(tool, 'queued');
    }
    renderTool(tool);
    refreshCurrent(run);
  }
  function refreshCurrent(run) {
    const tools = Array.from(run.tools.values());
    const describe = item => item.labelNode.textContent + (item.context.textContent ? ' · ' + item.context.textContent : '');
    const running = tools.filter(item => item.state === 'running');
    const generating = tools.find(item => item.state === 'generating');
    const queued = tools.find(item => item.state === 'queued');
    setCurrent(run, running.length ? describe(running[0]) + (running.length > 1 ? ' · ещё ' + (running.length - 1) : '')
      : generating ? 'Формирую вызов: ' + generating.labelNode.textContent
      : queued ? 'Ожидает: ' + describe(queued)
      : 'Обрабатываю результат');
  }
  function toolEvent(event) {
    if (typeof event.toolCallId !== 'string') return;
    const run = ensureRun();
    const tool = ensureTool(run, event.toolCallId, event.toolName);
    if (!tool.node.isConnected) place(run, tool.node, 'tool');
    setToolArguments(tool, event.args);
    if (event.parentToolCallId && !tool.parentInfo) {
      tool.parentInfo = element('p', 'step-info', 'Родительский вызов: ' + event.parentToolCallId);
      tool.detail.prepend(tool.parentInfo);
    }
    tool.executed = true;
    if (['generating', 'queued'].includes(tool.state)) setToolState(tool, 'running');
    if (event.type === 'tool_execution_update') {
      tool.output = event.partialResult; tool.updates++;
      tool.outputSection.section.querySelector('h4').textContent = 'Последнее обновление · ' + tool.updates;
    }
    if (event.type === 'tool_execution_end') {
      tool.output = event.result;
      tool.outputSection.section.querySelector('h4').textContent = 'Результат';
      setToolState(tool, event.isError ? 'error' : 'done', event.durationMs);
    }
    renderTool(tool);
    refreshCurrent(run);
  }
  function finishModel(message) {
    if (!message || message.role !== 'assistant') return;
    const run = ensureRun();
    const model = currentModel();
    noteModel(run, message);
    const content = Array.isArray(message.content) ? message.content : [];
    const kinds = new Map(content.map((part, index) => [index, part && part.type]));
    // The authoritative message replaces streamed parts; stale partial reasoning/text disappears.
    Array.from(model.parts.entries()).forEach(([key, part]) => {
      if (part.kind === 'reasoning' && kinds.get(key) !== 'thinking') setReasoning(model, key, '', false, true);
      if (part.kind === 'text' && kinds.get(key) !== 'text') setText(model, key, '');
    });
    const finalIds = new Set();
    content.forEach((part, index) => {
      if (!part) return;
      if (part.type === 'thinking') setReasoning(model, index, part.thinking, part.redacted, true);
      if (part.type === 'text') setText(model, index, part.text);
      if (part.type === 'toolCall') { finalIds.add(part.id); formedTool(model, { type: 'toolcall_end', contentIndex: index, toolCall: part }); }
    });
    model.parts.forEach(part => {
      if (part.kind === 'tool' && !part.executed && !finalIds.has(part.id) && ['generating', 'queued'].includes(part.state)) setToolState(part, 'not_run');
    });
    if (message.usage && typeof message.usage.output === 'number') {
      run.outputTokens += message.usage.output;
      run.streamMs += Date.now() - model.startedAt;
    }
    if (message.errorMessage || message.stopReason === 'error') {
      const node = element('div', 'step error');
      node.append(icon('alert', 'step-icon'), element('span', '', message.errorMessage || 'Провайдер вернул ошибку.'));
      place(run, node, 'error');
    }
    run.model = null;
    if (!run.finished) setCurrent(run, 'Думаю');
  }
  function retryNotice(event, start) {
    const run = ensureRun();
    const node = element('div', 'step notice');
    const text = start
      ? 'Повтор запроса к провайдеру' + (event.attempt ? ' · попытка ' + event.attempt + (event.maxAttempts ? ' из ' + event.maxAttempts : '') : '') + (event.errorMessage ? ' · ' + event.errorMessage : '')
      : 'Повтор не удался' + (event.finalError ? ' · ' + event.finalError : '');
    node.append(icon('retry', 'step-icon'), element('span', 'step-label', text));
    place(run, node, start ? 'notice' : 'error');
    setCurrent(run, start ? 'Повтор запроса к провайдеру' : 'Повтор не удался');
  }
  function finishRun(status) {
    const run = currentRun;
    if (!run) return;
    run.finished = true; run.status = status;
    if (!run.finishedAt) run.finishedAt = Date.now();
    run.tools.forEach(tool => {
      if (['generating', 'queued'].includes(tool.state)) setToolState(tool, 'not_run');
      else if (tool.state === 'running') {
        setToolState(tool, 'unknown');
        if (!tool.outcomeNote) {
          tool.outcomeNote = element('p', 'step-info warning', 'Ожидание прервано без результата. Операция в Excel могла завершиться; её итог не подтверждён.');
          tool.detail.appendChild(tool.outcomeNote);
        }
      }
    });
    run.models.forEach(model => model.parts.forEach(part => {
      if (part.kind === 'reasoning' && part.state === 'running') {
        part.endedAt = Date.now(); part.meta.textContent = seconds(part.endedAt - part.startedAt); setState(part, 'done');
      }
    }));
    flushMarkdown();
    run.body.querySelectorAll(':scope > .final').forEach(node => node.classList.remove('final'));
    const last = run.body.lastElementChild;
    if (last && last.classList.contains('text')) last.classList.add('final');
    updateRun(run);
    renderFooter(run, last && last.classList.contains('text') ? last : null);
  }
  function renderFooter(run, finalNode) {
    const facts = [];
    if (run.modelNames.length) facts.push(run.modelNames.join(', '));
    if (run.outputTokens) facts.push(tokens(run.outputTokens) + ' ток.');
    if (run.outputTokens && run.streamMs > 500) facts.push(Math.round(run.outputTokens / (run.streamMs / 1000)) + ' ток/с');
    run.footer.replaceChildren(element('span', 'run-facts', facts.join(' · ')));
    if (finalNode) {
      const text = run.models.flatMap(model => Array.from(model.parts.values())).filter(part => part.node === finalNode).map(part => part.text)[0] || finalNode.textContent;
      const copy = element('button', 'copy-button');
      copy.type = 'button';
      copy.append(icon('copy'), element('span', '', 'Копировать'));
      copy.addEventListener('click', () => copyText(text, copy));
      run.footer.appendChild(copy);
    }
    run.footer.hidden = !facts.length && !finalNode;
  }
  async function copyText(text, button) {
    try { await navigator.clipboard.writeText(text); }
    catch (error) {
      const area = element('textarea'); area.value = text; document.body.appendChild(area);
      area.select(); document.execCommand('copy'); area.remove();
    }
    const label = button.querySelector('span');
    label.textContent = 'Скопировано';
    setTimeout(() => { label.textContent = 'Копировать'; }, 1500);
  }

  function activity(event) {
    if (!event || typeof event.type !== 'string') return;
    switch (event.type) {
      case 'agent_start':
        if (currentRun && currentRun.finished) currentRun = null;
        ensureRun(); break;
      case 'turn_start': ensureRun(); break;
      case 'message_start':
        if (event.message && event.message.role === 'assistant') startModel(event.message); break;
      case 'message_update': {
        const update = event.assistantMessageEvent;
        if (!update || typeof update.type !== 'string') break;
        const run = ensureRun(), model = currentModel();
        if (update.type.startsWith('thinking_')) {
          const index = partIndex(model, update, 'reasoning');
          const previous = model.parts.get(index);
          const text = update.type === 'thinking_end' ? update.content
            : (previous && previous.kind === 'reasoning' ? previous.text : '') + (update.delta || '');
          setReasoning(model, index, text, false, update.type === 'thinking_end');
          if (update.type !== 'thinking_end') setCurrent(run, 'Рассуждаю');
        } else if (update.type.startsWith('text_')) {
          const index = partIndex(model, update, 'text');
          const previous = model.parts.get(index);
          if (update.type === 'text_end') setText(model, index, update.content);
          else if (update.type === 'text_delta') setText(model, index, (previous && previous.kind === 'text' ? previous.text : '') + (update.delta || ''));
          setCurrent(run, 'Пишу ответ');
        } else if (update.type.startsWith('toolcall_')) formedTool(model, update);
        break;
      }
      case 'message_end': finishModel(event.message); break;
      case 'tool_execution_start': case 'tool_execution_update': case 'tool_execution_end': toolEvent(event); break;
      case 'auto_retry_start': retryNotice(event, true); break;
      case 'auto_retry_end': if (event.success === false) retryNotice(event, false); break;
      // agent_end may precede an automatic retry. Only agent_settled is terminal.
      case 'agent_settled': finishRun(event.aborted ? 'stopped' : 'done'); break;
      case 'aperture_run_end': finishRun(event.status === 'error' ? 'error' : event.status === 'stopped' ? 'stopped' : 'done'); break;
      case 'process_exit': case 'error': if (currentRun && !currentRun.finished) finishRun('error'); break;
    }
  }

  function receive(message) {
    if (!message || typeof message.type !== 'string') return;
    withScroll(() => {
      switch (message.type) {
        case 'binding':
          $('workbook-name').textContent = message.name || 'Книга не привязана';
          $('workbook-name').title = message.name || 'Книга не привязана'; break;
        case 'status':
          $('status-text').textContent = message.text || '';
          $('status-text').title = message.text || '';
          $('status').classList.toggle('error', !!message.error); break;
        case 'busy':
          busy = !!message.busy; canStop = message.canStop !== false;
          if (!busy) awaitingClear = false;
          applyBusy(); break;
        case 'message': appendMessage(message.who || 'Aperture', message.text || ''); break;
        case 'activity': activity(message.event); break;
        case 'clear_transcript':
          clearTimeout(renderTimer); cancelAnimationFrame(renderFrame); renderTimer = renderFrame = 0; markdownUpdates.clear();
          currentRun = null;
          messages.replaceChildren(); $('welcome').hidden = false; conversation.scrollTop = 0; break;
        case 'clear_prompt':
          awaitingClear = false; prompt.value = ''; autosize(); applyBusy(); prompt.focus(); break;
      }
    }, message.type === 'message' && message.who === 'Вы');
  }
  if (bridge) bridge.addEventListener('message', event => receive(event.data));
  // One ticker for the live elapsed time of the active request.
  setInterval(() => { if (currentRun && !currentRun.finished) updateRun(currentRun); }, 1000);
  applyBusy(); autosize(); post('ready');
})();
