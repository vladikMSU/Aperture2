(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const conversation = $('conversation');
  const messages = $('messages');
  const prompt = $('prompt');
  let busy = false;
  let activeAssistant = null;
  let activeText = '';
  let userHasMessages = false;
  let currentRun = null;
  let runSequence = 0;
  let renderTimer = 0;
  let renderFrame = 0;
  const markdownUpdates = new Map();

  function post(command, text = '') {
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage({ command, text });
  }
  function scrollEnd() { conversation.scrollTo({ top: conversation.scrollHeight, behavior: 'instant' }); }
  function nearEnd() { return conversation.scrollHeight - conversation.scrollTop - conversation.clientHeight < 90; }
  function preserveScroll(action, forceEnd = false) {
    const follow = forceEnd || nearEnd();
    const top = conversation.scrollTop;
    const boundary = conversation.getBoundingClientRect().top;
    const anchor = !follow && Array.from(messages.querySelectorAll('.activity-entry, .message-label, .message-body > *'))
      .find(node => node.getBoundingClientRect().bottom > boundary);
    const anchorTop = anchor && anchor.getBoundingClientRect().top;
    action();
    if (follow) scrollEnd();
    else conversation.scrollTop = top + (anchor && anchor.isConnected ? anchor.getBoundingClientRect().top - anchorTop : 0);
  }
  function flushMarkdown() {
    clearTimeout(renderTimer); renderTimer = 0;
    cancelAnimationFrame(renderFrame); renderFrame = 0;
    preserveScroll(() => {
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
  function updateSend() { $('send').disabled = busy || !prompt.value.trim(); }
  function submit() {
    if (busy || !prompt.value.trim()) return;
    post('send', prompt.value.trim());
  }
  $('send').addEventListener('click', submit);
  $('stop').addEventListener('click', () => post('stop'));
  $('new-chat').addEventListener('click', () => post('new_chat'));
  $('rebind').addEventListener('click', () => post('rebind'));
  $('settings').addEventListener('click', () => post('settings'));
  prompt.addEventListener('input', updateSend);
  prompt.addEventListener('keydown', event => {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey) && !event.isComposing) {
      event.preventDefault(); submit();
    }
  });
  document.querySelectorAll('[data-prompt]').forEach(button => button.addEventListener('click', () => {
    prompt.value = button.dataset.prompt;
    updateSend(); prompt.focus();
  }));

  // Build DOM nodes, never HTML from model text. This small Markdown subset intentionally
  // has no images, executable markup or clickable URLs that could navigate the pane.
  function inline(target, text) {
    const pattern = /(`[^`\n]+`|\*\*[^*\n]+\*\*)/g;
    let start = 0, match;
    while ((match = pattern.exec(text))) {
      target.appendChild(document.createTextNode(text.slice(start, match.index)));
      const code = match[0][0] === '`';
      const node = document.createElement(code ? 'code' : 'strong');
      node.textContent = match[0].slice(code ? 1 : 2, code ? -1 : -2);
      target.appendChild(node); start = pattern.lastIndex;
    }
    target.appendChild(document.createTextNode(text.slice(start)));
  }
  function tableCells(line) { return line.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map(s => s.trim()); }
  function isTableDivider(line) { return /^\s*\|?\s*:?-{3,}:?\s*(\|\s*:?-{3,}:?\s*)+\|?\s*$/.test(line); }
  function renderMarkdown(target, text) {
    target.replaceChildren();
    const lines = text.replace(/\r\n?/g, '\n').split('\n');
    let index = 0;
    while (index < lines.length) {
      const line = lines[index];
      if (!line.trim()) { index++; continue; }
      if (/^\s*```/.test(line)) {
        const code = document.createElement('code'), pre = document.createElement('pre'), block = [];
        index++;
        while (index < lines.length && !/^\s*```/.test(lines[index])) block.push(lines[index++]);
        if (index < lines.length) index++;
        code.textContent = block.join('\n'); pre.appendChild(code); target.appendChild(pre); continue;
      }
      if (index + 1 < lines.length && line.includes('|') && isTableDivider(lines[index + 1])) {
        const wrap = document.createElement('div'), table = document.createElement('table');
        wrap.className = 'table-wrap';
        const row = document.createElement('tr');
        tableCells(line).forEach(cell => { const th = document.createElement('th'); inline(th, cell); row.appendChild(th); });
        const head = document.createElement('thead'); head.appendChild(row); table.appendChild(head);
        const body = document.createElement('tbody'); index += 2;
        while (index < lines.length && lines[index].includes('|') && lines[index].trim()) {
          const tr = document.createElement('tr');
          tableCells(lines[index++]).forEach(cell => { const td = document.createElement('td'); inline(td, cell); tr.appendChild(td); });
          body.appendChild(tr);
        }
        table.appendChild(body); wrap.appendChild(table); target.appendChild(wrap); continue;
      }
      const heading = /^(#{1,4})\s+(.+)$/.exec(line);
      if (heading) { const node = document.createElement('h' + Math.min(4, heading[1].length + 1)); inline(node, heading[2]); target.appendChild(node); index++; continue; }
      const list = /^\s*(?:([-*])|\d+\.)\s+(.+)$/.exec(line);
      if (list) {
        const ordered = !list[1], node = document.createElement(ordered ? 'ol' : 'ul');
        while (index < lines.length) {
          const item = /^\s*(?:([-*])|\d+\.)\s+(.+)$/.exec(lines[index]);
          if (!item || (!item[1]) !== ordered) break;
          const li = document.createElement('li'); inline(li, item[2]); node.appendChild(li); index++;
        }
        target.appendChild(node); continue;
      }
      const paragraph = document.createElement('p'), paragraphLines = [line]; index++;
      while (index < lines.length && lines[index].trim() && !/^\s*(?:```|#{1,4}\s|[-*]\s|\d+\.\s)/.test(lines[index]) && !(index + 1 < lines.length && isTableDivider(lines[index + 1]))) paragraphLines.push(lines[index++]);
      paragraphLines.forEach((item, i) => { if (i) paragraph.appendChild(document.createElement('br')); inline(paragraph, item); });
      target.appendChild(paragraph);
    }
  }
  function makeMessage(who, text, streaming) {
    const wasNearEnd = nearEnd();
    const type = who === 'Вы' ? 'user' : /^Ошибка/.test(who) ? 'error' : /инструмент|tool/i.test(who) ? 'tool' : who === 'Pi' || who === 'Aperture2' || who === 'Aperture' ? 'assistant' : 'system';
    if (type === 'user' || streaming) userHasMessages = true;
    $('welcome').hidden = userHasMessages;
    const article = document.createElement('article'); article.className = 'message ' + type;
    const label = document.createElement('div'); label.className = 'message-label';
    if (type === 'assistant') { const dot = document.createElement('span'); dot.className = 'assistant-dot'; label.appendChild(dot); }
    label.appendChild(document.createTextNode(type === 'assistant' ? 'Aperture' : who));
    const body = document.createElement('div'); body.className = 'message-body';
    if (streaming) { body.classList.add('streaming'); renderMarkdown(body, text); }
    else if (type === 'assistant') renderMarkdown(body, text);
    else body.textContent = text;
    article.append(label, body); messages.appendChild(article);
    if (wasNearEnd || type === 'user') scrollEnd();
    return body;
  }
  function endAssistant() {
    if (!activeAssistant) return;
    queueMarkdown(activeAssistant, activeText);
    flushMarkdown();
    activeAssistant.classList.remove('streaming');
    activeAssistant = null; activeText = '';
  }

  function element(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  }
  function ensureRun() {
    if (currentRun) return currentRun;
    userHasMessages = true; $('welcome').hidden = true;
    const details = element('details', 'activity-run');
    details.open = true;
    details.dataset.run = String(++runSequence);
    const summary = element('summary');
    const title = element('span', 'activity-title', 'Агент работает');
    const count = element('span', 'activity-count', '0 шагов');
    summary.append(element('span', 'activity-dot'), title, count);
    const list = element('div', 'activity-list');
    details.append(summary, list); messages.appendChild(details);
    currentRun = { details, title, count, list, tools: new Map(), models: [], model: null, finished: false, status: 'running', current: 'Агент работает', turns: 0 };
    updateRun(currentRun);
    return currentRun;
  }
  const stateLabels = { generating: 'Формируется', queued: 'Ожидает', running: 'В работе', done: 'Готово', error: 'Ошибка', stopped: 'Остановлено', not_run: 'Не исполнен', unknown: 'Нет результата' };
  function updateRun(run) {
    run.details.dataset.state = run.status;
    const total = run.models.length + run.tools.size;
    run.count.textContent = total + ' шаг. · ' + run.tools.size + ' инстр.';
    run.title.textContent = run.finished
      ? ({ done: 'Работа завершена', stopped: 'Работа остановлена', error: 'Работа завершилась с ошибкой' }[run.status] || 'Работа завершена')
      : run.current;
    run.title.title = run.title.textContent;
  }
  function newEntry(run, type, title) {
    const details = element('details', 'activity-entry ' + type);
    const summary = element('summary');
    const copy = element('span', 'activity-entry-copy');
    const label = element('span', 'activity-entry-title', title);
    const caption = element('span', 'activity-entry-caption', '');
    const state = element('span', 'activity-entry-state', 'В работе');
    copy.append(label, caption);
    summary.append(element('span', 'activity-dot'), copy, state);
    const body = element('div', 'activity-detail');
    details.append(summary, body); run.list.appendChild(details);
    const entry = { details, label, caption, state, body, status: 'running', type };
    setEntryState(entry, 'running');
    return entry;
  }
  function setEntryState(entry, state) {
    entry.status = state;
    entry.details.dataset.state = state;
    entry.state.textContent = stateLabels[state] || state;
  }
  function ensureModel(message, start = false) {
    const run = ensureRun();
    if (!run.model || (start && run.model.started)) {
      const model = newEntry(run, 'model-step', 'Ответ модели · ' + (run.models.length + 1));
      model.reasoning = new Map(); model.toolCalls = new Map(); model.started = false;
      model.empty = element('p', 'activity-empty', 'Рассуждение пока не получено.');
      model.body.appendChild(model.empty);
      model.details.addEventListener('toggle', () => {
        if (model.details.open) model.reasoning.forEach(part => queueMarkdown(part.body, part.text));
      });
      run.models.push(model); run.model = model;
    }
    if (start) run.model.started = true;
    if (message) {
      const modelName = message.responseModel || message.model;
      if (modelName) run.model.caption.textContent = modelName;
    }
    run.current = 'Ответ модели · шаг ' + run.models.length;
    updateRun(run);
    return run.model;
  }
  function setThinking(model, index, text, redacted) {
    if (redacted || text === '') {
      const old = model.reasoning.get(index);
      if (old) { markdownUpdates.delete(old.body); old.section.remove(); model.reasoning.delete(index); }
      model.empty.hidden = model.reasoning.size !== 0;
      if (!model.reasoning.size) model.caption.textContent = '';
      return;
    }
    if (typeof text !== 'string' || !text) return;
    let part = model.reasoning.get(index);
    if (!part) {
      const section = element('section', 'reasoning-section');
      const body = element('div', 'reasoning-body message-body');
      section.append(element('h4', '', 'Рассуждение модели'), body);
      model.body.appendChild(section);
      part = { section, body, text: '' }; model.reasoning.set(index, part);
    }
    part.text = text;
    model.empty.hidden = true;
    model.caption.textContent = 'Рассуждение получено' + (model.reasoning.size > 1 ? ' · блоков: ' + model.reasoning.size : '');
    if (model.details.open) queueMarkdown(part.body, text);
  }
  function finishModel(message) {
    if (!message || message.role !== 'assistant') return;
    const model = ensureModel(message);
    const previousTools = Array.from(new Set(model.toolCalls.values()));
    const finalToolIds = new Set();
    const finalIndices = new Set((message.content || []).map((part, index) => part.type === 'thinking' ? index : -1));
    model.reasoning.forEach((part, index) => { if (!finalIndices.has(index)) setThinking(model, index, '', false); });
    (message.content || []).forEach((part, index) => {
      if (part.type === 'thinking') setThinking(model, index, part.thinking, part.redacted);
      if (part.type === 'toolCall') {
        finalToolIds.add(part.id);
        formedTool(model, { type: 'toolcall_end', contentIndex: index, toolCall: part });
      }
    });
    previousTools.forEach(tool => {
      if (!tool.executed && !finalToolIds.has(tool.id) && ['generating', 'queued'].includes(tool.status)) setEntryState(tool, 'not_run');
    });
    const state = message.stopReason === 'error' ? 'error' : message.stopReason === 'aborted' ? 'stopped' : 'done';
    setEntryState(model, state);
    model.empty.textContent = 'Рассуждение модели не передано.';
    if (message.errorMessage) {
      if (!model.error) { model.error = element('pre'); model.body.appendChild(model.error); }
      model.error.textContent = message.errorMessage;
    }
  }
  function formatPayload(value) {
    let text;
    if (typeof value === 'string') text = value;
    else {
      // Tool images/binary payloads are not useful in a JSON preview. Never embed them.
      text = JSON.stringify(value, (key, item) => {
        if (item && typeof item === 'object' && item.type === 'image') return { type: 'image', mimeType: item.mimeType, preview: 'Изображение не показано в текстовой ленте' };
        return item;
      }, 2) || '';
    }
    const limit = 40000;
    return text.length > limit ? text.slice(0, limit) + '\n… Показаны первые ' + limit + ' символов результата.' : text;
  }
  function toolSection(title) {
    const section = element('section');
    const content = element('pre');
    section.append(element('h4', '', title), content);
    return { section, content };
  }
  function renderTool(entry) {
    if (!entry.details.open) return; // Large Excel results stay lazy while collapsed.
    entry.argsSection.section.hidden = entry.args === undefined;
    entry.outputSection.section.hidden = entry.output === undefined;
    if (entry.args !== undefined) entry.argsSection.content.textContent = formatPayload(entry.args);
    if (entry.output !== undefined) entry.outputSection.content.textContent = formatPayload(entry.output);
  }
  function ensureTool(run, id, name, previous) {
    let tool = id && run.tools.get(id);
    if (tool && previous && previous !== tool) {
      // A late final block can reveal an ID already seen in execution callbacks.
      // Retain the authoritative execution card and remove only its placeholder.
      run.tools.delete(previous.key);
      previous.details.remove();
      run.models.forEach(model => model.toolCalls.forEach((item, index) => {
        if (item === previous) model.toolCalls.set(index, tool);
      }));
    }
    if (!tool) tool = previous;
    if (!tool) {
      tool = newEntry(run, 'tool-step', 'Инструмент');
      tool.key = id || 'pending:' + run.models.length + ':' + run.tools.size;
      tool.executed = false;
      tool.argumentText = '';
      tool.argsSection = toolSection('Аргументы');
      tool.outputSection = toolSection('Результат / последнее обновление');
      tool.meta = element('p', 'activity-meta');
      tool.body.append(tool.meta, tool.argsSection.section, tool.outputSection.section);
      tool.updates = 0;
      tool.details.addEventListener('toggle', () => renderTool(tool));
      run.tools.set(tool.key, tool);
      setEntryState(tool, 'generating');
    }
    if (id && tool.key !== id) {
      run.tools.delete(tool.key);
      tool.key = id; run.tools.set(id, tool);
    }
    if (id) { tool.id = id; tool.details.dataset.toolCallId = id; }
    if (name) tool.rawName = name;
    const names = {
      excel_target: 'Обзор книги', excel_read_range: 'Чтение диапазона', excel_write_values: 'Запись значений',
      excel_write_formula: 'Запись формулы', excel_recalculate: 'Пересчёт', excel_find: 'Поиск',
      excel_names: 'Именованные диапазоны', excel_add_sheet: 'Добавление листа', excel_format: 'Оформление диапазона',
      excel_operation_status: 'Состояние операции'
    };
    const shortName = (tool.rawName || '').split('__').pop();
    tool.label.textContent = Object.prototype.hasOwnProperty.call(names, shortName) ? names[shortName] : tool.rawName || 'Инструмент';
    tool.label.title = tool.rawName || 'Имя формируется';
    tool.meta.textContent = (tool.rawName || 'Имя формируется') + '\ntoolCallId: ' + (tool.id || 'ещё не получен');
    return tool;
  }
  function setToolArguments(tool, args) {
    if (args !== undefined) {
      tool.args = args;
      tool.context = [args && args.sheet, args && (args.address || args.cell || args.startCell)]
        .filter(value => typeof value === 'string').join(' · ');
      tool.caption.textContent = tool.context;
    }
  }
  function formedTool(model, update) {
    const run = ensureRun();
    const index = Number.isInteger(update.contentIndex) ? update.contentIndex : 0;
    const call = update.toolCall;
    const id = call ? call.id : update.id;
    const name = call ? call.name : update.toolName;
    const tool = ensureTool(run, id, name, model.toolCalls.get(index));
    model.toolCalls.set(index, tool);
    if (update.type === 'toolcall_delta' && !tool.executed && tool.status === 'generating') {
      tool.argumentText += update.delta || '';
      setToolArguments(tool, tool.argumentText);
    }
    if (call) {
      setToolArguments(tool, call.arguments);
      if (tool.status === 'generating') setEntryState(tool, 'queued');
    }
    renderTool(tool);
    run.current = (tool.status === 'generating' ? 'Формируется: ' : 'Вызов: ') + tool.label.textContent;
    updateRun(run);
  }
  function toolEvent(event) {
    const run = ensureRun();
    if (typeof event.toolCallId !== 'string') return;
    const tool = ensureTool(run, event.toolCallId, event.toolName);
    setToolArguments(tool, event.args);
    if (event.parentToolCallId && !tool.parentMeta) {
      tool.parentMeta = element('p', 'activity-meta', 'Родительский вызов: ' + event.parentToolCallId);
      tool.body.prepend(tool.parentMeta);
    }
    tool.executed = true;
    if (['generating', 'queued'].includes(tool.status)) setEntryState(tool, 'running');
    if (event.type === 'tool_execution_update') {
      tool.output = event.partialResult; tool.updates++;
      tool.caption.textContent = [tool.context, 'Обновлений: ' + tool.updates].filter(Boolean).join(' · ');
    }
    if (event.type === 'tool_execution_end') {
      tool.output = event.result;
      setEntryState(tool, event.isError ? 'error' : 'done');
      tool.caption.textContent = [tool.context, typeof event.durationMs === 'number' ? (event.durationMs / 1000).toFixed(1) + ' с' : 'Вызов завершён'].filter(Boolean).join(' · ');
    }
    renderTool(tool);
    const pending = Array.from(run.tools.values()).filter(item => ['running', 'generating', 'queued'].includes(item.status));
    const current = pending.find(item => item.status === 'running') || pending.find(item => item.status === 'generating') || pending[0];
    const prefix = current && (current.status === 'generating' ? 'Формируется: ' : current.status === 'queued' ? 'Ожидает: ' : '');
    run.current = current ? prefix + current.label.textContent + (pending.length > 1 ? ' · ещё ' + (pending.length - 1) : '') : 'Результаты инструментов получены';
    updateRun(run);
  }
  function finishRun(status) {
    if (!currentRun) return;
    const run = currentRun;
    run.finished = true; run.status = status;
    [...run.models, ...run.tools.values()].forEach(entry => {
      if (entry.type === 'tool-step' && ['generating', 'queued'].includes(entry.status)) { setEntryState(entry, 'not_run'); return; }
      if (entry.type === 'tool-step' && entry.status === 'running') {
        setEntryState(entry, 'unknown');
        if (!entry.outcomeNote) {
          entry.outcomeNote = element('p', 'activity-empty', 'Ожидание прервано без результата. Операция в Excel могла завершиться; её итог не подтверждён.');
          entry.body.appendChild(entry.outcomeNote);
        }
        return;
      }
      if (entry.status === 'running') setEntryState(entry, status === 'done' ? 'unknown' : status);
    });
    flushMarkdown();
    updateRun(run);
  }
  function activity(event) {
    if (!event || typeof event.type !== 'string') return;
    preserveScroll(() => {
      switch (event.type) {
        case 'agent_start':
          if (currentRun && currentRun.finished) currentRun = null;
          ensureRun(); break;
        case 'turn_start': {
          const run = ensureRun(); run.turns++; run.model = null;
          run.current = 'Начинается шаг модели'; updateRun(run); break;
        }
        case 'message_start':
          if (event.message && event.message.role === 'assistant') ensureModel(event.message, true); break;
        case 'message_update': {
          const update = event.assistantMessageEvent;
          if (!update) break;
          if (/^toolcall_/.test(update.type)) { formedTool(ensureModel(event.message), update); break; }
          if (!/^thinking_/.test(update.type)) break;
          const model = ensureModel(event.message);
          const index = Number.isInteger(update.contentIndex) ? update.contentIndex : 0;
          const part = update.partial && update.partial.content && update.partial.content[index];
          const previous = model.reasoning.get(index);
          const text = update.type === 'thinking_end' ? update.content
            : part && typeof part.thinking === 'string' ? part.thinking
              : (previous ? previous.text : '') + (update.delta || '');
          setThinking(model, index, text, !!(part && part.redacted)); break;
        }
        case 'message_end': finishModel(event.message); break;
        case 'turn_end':
          // A completed model/tool turn is not the end of an agent run.
          if (currentRun) { currentRun.current = 'Шаг завершён'; updateRun(currentRun); } break;
        case 'tool_execution_start': case 'tool_execution_update': case 'tool_execution_end': toolEvent(event); break;
        case 'auto_retry_start': {
          const run = ensureRun(); run.current = 'Повтор после ошибки провайдера'; updateRun(run); break;
        }
        case 'auto_retry_end':
          if (currentRun) { currentRun.current = 'Продолжаю работу'; updateRun(currentRun); } break;
        case 'agent_settled': finishRun(event.aborted ? 'stopped' : 'done'); break;
        case 'aperture_run_end': finishRun(event.status === 'error' ? 'error' : event.status === 'stopped' ? 'stopped' : 'done'); break;
        case 'process_exit': case 'error':
          if (currentRun && !currentRun.finished) finishRun('error'); break;
        // agent_end may precede an automatic retry. Only agent_settled is terminal.
      }
    });
  }
  function receive(message) {
    if (!message || typeof message.type !== 'string') return;
    switch (message.type) {
      case 'binding':
        $('workbook-name').textContent = message.name || 'Книга не привязана';
        $('workbook-name').title = message.name || 'Книга не привязана'; break;
      case 'status':
        $('status-text').textContent = message.text || '';
        $('status').classList.toggle('error', !!message.error); break;
      case 'busy':
        busy = !!message.busy;
        prompt.readOnly = busy;
        $('status').classList.toggle('working', busy);
        $('stop').hidden = !busy; $('stop').disabled = !message.canStop;
        ['new-chat', 'rebind', 'settings'].forEach(id => { $(id).disabled = busy; });
        document.querySelectorAll('[data-prompt]').forEach(button => { button.disabled = busy; });
        updateSend(); break;
      case 'message':
        endAssistant();
        // The welcome state already explains the purpose; keep any controller greeting
        // as an ordinary message once conversation exists rather than duplicate it.
        if (!userHasMessages && message.who === 'Aperture2' && /^Задайте вопрос/.test(message.text || '')) break;
        if (message.who === 'Вы') currentRun = null;
        makeMessage(message.who || 'Aperture', message.text || '', false); break;
      case 'assistant_delta': {
        if (!activeAssistant) activeAssistant = makeMessage('Aperture', '', true);
        activeText += message.text || ''; queueMarkdown(activeAssistant, activeText); break;
      }
      case 'assistant_replace':
        if (!activeAssistant && !message.text) break;
        if (activeAssistant && !message.text) {
          markdownUpdates.delete(activeAssistant);
          preserveScroll(() => activeAssistant.closest('.message').remove());
          activeAssistant = null; activeText = ''; break;
        }
        if (!activeAssistant) activeAssistant = makeMessage('Aperture', '', true);
        activeText = message.text || ''; queueMarkdown(activeAssistant, activeText); break;
      case 'assistant_end': endAssistant(); break;
      case 'activity': activity(message.event); break;
      case 'clear_transcript':
        clearTimeout(renderTimer); cancelAnimationFrame(renderFrame); renderTimer = renderFrame = 0; markdownUpdates.clear();
        activeAssistant = null; activeText = ''; currentRun = null; userHasMessages = false;
        messages.replaceChildren(); $('welcome').hidden = false; conversation.scrollTo({ top: 0, behavior: 'instant' }); break;
      case 'clear_prompt': prompt.value = ''; updateSend(); prompt.focus(); break;
    }
  }
  if (window.chrome && window.chrome.webview) window.chrome.webview.addEventListener('message', event => receive(event.data));
  updateSend(); post('ready');
})();
