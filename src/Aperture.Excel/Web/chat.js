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

  function post(command, text = '') {
    if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage({ command, text });
  }
  function scrollEnd() { conversation.scrollTo({ top: conversation.scrollHeight, behavior: 'instant' }); }
  function nearEnd() { return conversation.scrollHeight - conversation.scrollTop - conversation.clientHeight < 90; }
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
    if (streaming) { body.classList.add('streaming'); body.textContent = text; }
    else if (type === 'assistant') renderMarkdown(body, text);
    else body.textContent = text;
    article.append(label, body); messages.appendChild(article);
    if (wasNearEnd || type === 'user') scrollEnd();
    return body;
  }
  function endAssistant() {
    if (!activeAssistant) return;
    const follow = nearEnd();
    activeAssistant.classList.remove('streaming'); renderMarkdown(activeAssistant, activeText);
    activeAssistant = null; activeText = ''; if (follow) scrollEnd();
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
        makeMessage(message.who || 'Aperture', message.text || '', false); break;
      case 'assistant_delta': {
        const follow = nearEnd();
        if (!activeAssistant) activeAssistant = makeMessage('Aperture', '', true);
        activeText += message.text || ''; activeAssistant.textContent = activeText;
        if (follow) scrollEnd(); break;
      }
      case 'assistant_end': endAssistant(); break;
      case 'clear_transcript': activeAssistant = null; activeText = ''; userHasMessages = false; messages.replaceChildren(); $('welcome').hidden = false; conversation.scrollTo({ top: 0, behavior: 'instant' }); break;
      case 'clear_prompt': prompt.value = ''; updateSend(); prompt.focus(); break;
    }
  }
  if (window.chrome && window.chrome.webview) window.chrome.webview.addEventListener('message', event => receive(event.data));
  updateSend(); post('ready');
})();
