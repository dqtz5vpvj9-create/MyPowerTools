R"MPTJS(
(() => {
  let bindings = [];
  const bridge = globalThis.chrome?.webview;
  if (!bridge) return;
  bridge.addEventListener('message', event => {
    if (event.data?.type === 'shortcut-bindings' && Array.isArray(event.data.bindings)) {
      bindings = event.data.bindings;
    }
  });
  const names = { ' ': 'Space', ArrowLeft: 'Left', ArrowRight: 'Right', ArrowUp: 'Up', ArrowDown: 'Down', Esc: 'Escape' };
  const punctuation = { Comma: 'OemComma', Period: 'OemPeriod', Minus: 'OemMinus', Equal: 'OemPlus',
    BracketLeft: 'OemOpenBrackets', BracketRight: 'OemCloseBrackets', Semicolon: 'OemSemicolon', Quote: 'OemQuotes',
    Slash: 'OemQuestion', Backslash: 'OemPipe', Backquote: 'OemTilde' };
  addEventListener('keydown', event => {
    if (event.defaultPrevented || event.isComposing || event.keyCode === 229 || event.repeat || event.getModifierState?.('AltGraph')) return;
    if (['Control', 'Alt', 'Shift', 'Meta', 'Dead', 'Unidentified', 'Process'].includes(event.key)) return;
    let key = names[event.key] || event.key;
    if (/^[a-z]$/i.test(key)) key = key.toUpperCase();
    else if (/^Digit[0-9]$/.test(event.code)) key = event.code.slice(-1);
    else if (punctuation[event.code]) key = punctuation[event.code];
    const gesture = [event.ctrlKey && 'Ctrl', event.altKey && 'Alt', event.shiftKey && 'Shift', event.metaKey && 'Win', key].filter(Boolean).join('+');
    const textInput = (event.composedPath?.() || [event.target]).some(element =>
      element?.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(element?.tagName));
    const match = bindings.find(binding => binding.gesture.toLowerCase() === gesture.toLowerCase() && (!textInput || binding.allowInTextInput));
    if (!match) return;
    event.preventDefault();
    bridge.postMessage({ __mptShortcut: { gesture: match.gesture, textInput } });
  });
})();
)MPTJS"
