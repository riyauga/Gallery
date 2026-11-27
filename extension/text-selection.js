// Shared by Gallery WebViews and extension pages. Keep native selection, then refine words/lines.
(() => {
  if (window.__galleryTextSelection) return;
  window.__galleryTextSelection = true;
  const separator = c => /[\s\p{P}\p{S}]/u.test(c);
  function bounds(text, index, clicks, from, to) {
    index = Math.max(0, Math.min(text.length - 1, index));
    let start = index, end = index + 1;
    if (clicks === 3) {
      while (start > 0 && !/[\r\n]/.test(text[start - 1])) start--;
      while (end < text.length && !/[\r\n]/.test(text[end])) end++;
      if (/[\r\n]/.test(text[index])) end = index;
    } else {
      const same = c => separator(text[index])
        ? /\s/.test(text[index]) ? /\s/.test(c) : c === text[index]
        : !separator(c);
      while (start > from && same(text[start - 1])) start--;
      while (end < to && same(text[end])) end++;
    }
    return [start, end];
  }
  function inputIndex(input, x, y, from, to) {
    const style = getComputedStyle(input), rect = input.getBoundingClientRect();
    const mirror = document.createElement('div');
    for (const key of ['font', 'letterSpacing', 'wordSpacing', 'lineHeight', 'textTransform', 'textIndent', 'textAlign', 'direction', 'boxSizing', 'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft', 'borderTopWidth', 'borderRightWidth', 'borderBottomWidth', 'borderLeftWidth', 'tabSize', 'overflowWrap', 'wordBreak']) mirror.style[key] = style[key];
    Object.assign(mirror.style, { position: 'fixed', left: `${rect.left}px`, top: `${rect.top}px`, width: `${rect.width}px`, height: `${rect.height}px`, margin: '0', visibility: 'hidden', pointerEvents: 'none', borderStyle: 'solid', overflow: 'hidden', whiteSpace: input instanceof HTMLTextAreaElement ? 'pre-wrap' : 'pre' });
    const node = document.createTextNode(input.value); mirror.append(node); document.body.append(mirror);
    mirror.scrollLeft = input.scrollLeft; mirror.scrollTop = input.scrollTop;
    let index = from, best = Infinity;
    const range = document.createRange();
    for (let i = from; i < to; i++) {
      range.setStart(node, i); range.setEnd(node, i + 1);
      for (const bounds of range.getClientRects()) {
        const dx = Math.max(bounds.left - x, x - bounds.right, 0);
        const dy = input instanceof HTMLTextAreaElement ? Math.max(bounds.top - y, y - bounds.bottom, 0) : 0;
        const distance = dx * dx + dy * dy;
        if (distance < best) { index = i; best = distance; }
      }
      if (best === 0) break;
    }
    mirror.remove();
    return index;
  }
  let pressed;
  document.addEventListener('mousedown', event => {
    pressed = event.button === 0 && (event.detail === 2 || event.detail === 3)
      ? { target: event.target, x: event.clientX, y: event.clientY, clicks: event.detail } : null;
  }, true);
  document.addEventListener('mouseup', event => {
    const down = pressed; pressed = null;
    if (!down || event.button !== 0 || Math.abs(event.clientX - down.x) > 4 || Math.abs(event.clientY - down.y) > 4) return;
    const target = down.target;
    if (!(target instanceof Element)) return;
    const input = target.closest('input,textarea');
    if (input && (input.type === 'password' || input.selectionStart === null)) return;
    if (!input && (target.closest('button,select,[role="button"]') || getComputedStyle(target).userSelect === 'none')) return;
    const caret = input ? null : document.caretRangeFromPoint(down.x, down.y);
    setTimeout(() => {
      if (!target.isConnected) return;
      if (input) {
        const text = input.value;
        if (!text) return;
        const from = down.clicks === 2 ? input.selectionStart : 0;
        const to = down.clicks === 2 ? input.selectionEnd : text.length;
        if (from === to) return;
        const index = inputIndex(input, down.x, down.y, from, to);
        const [start, end] = bounds(text, index, down.clicks, from, to);
        input.setSelectionRange(start, end);
        return;
      }
      if (!caret || caret.startContainer.nodeType !== Node.TEXT_NODE) return;
      const node = caret.startContainer, text = node.textContent;
      if (!text) return;
      let index = Math.min(caret.startOffset, text.length - 1);
      if (caret.startOffset > 0) {
        const preceding = document.createRange();
        preceding.setStart(node, caret.startOffset - 1); preceding.setEnd(node, caret.startOffset);
        if ([...preceding.getClientRects()].some(r => down.x >= r.left && down.x <= r.right && down.y >= r.top && down.y <= r.bottom)) index = caret.startOffset - 1;
      }
      const selection = getSelection();
      const native = selection.rangeCount ? selection.getRangeAt(0) : null;
      const sameNode = down.clicks === 2 && native && native.startContainer === node && native.endContainer === node && !native.collapsed;
      const [start, end] = bounds(text, index, down.clicks, sameNode ? native.startOffset : 0, sameNode ? native.endOffset : text.length);
      const range = document.createRange(); range.setStart(node, start); range.setEnd(node, end);
      selection.removeAllRanges(); selection.addRange(range);
    }, 0);
  }, true);
})();
