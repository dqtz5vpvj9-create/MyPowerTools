const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const raw = fs.readFileSync(path.join(__dirname, '../../src/MyPowerTools.WebSurface.Shared/ShortcutForwarding.inc'), 'utf8');
const script = raw.slice(raw.indexOf('(') + 1, raw.lastIndexOf(')MPTJS"'));
function harness() {
  const messages = [], listeners = {};
  let receive;
  const context = { chrome: { webview: {
    addEventListener(type, handler) { receive = handler; },
    postMessage(message) { messages.push(JSON.parse(JSON.stringify(message))); }
  } }, addEventListener(type, handler) { listeners[type] = handler; } };
  vm.runInNewContext(script, context);
  return {
    messages,
    configure(bindings) { receive({ data: { type: 'shortcut-bindings', bindings } }); },
    press(overrides = {}) {
      const e = { key: 'k', code: 'KeyK', ctrlKey: true, defaultPrevented: false,
        preventDefault() { this.defaultPrevented = true; }, composedPath: () => [], ...overrides };
      listeners.keydown(e); return e;
    }
  };
}
test('nothing is intercepted before host configuration', () => {
  const h=harness(); assert.equal(h.press().defaultPrevented,false); assert.equal(h.messages.length,0);
});
test('the current configurable key is forwarded exactly once', () => {
  const h=harness(); h.configure([{gesture:'Ctrl+K',allowInTextInput:true}]);
  assert.equal(h.press().defaultPrevented,true);
  assert.deepEqual(h.messages,[{__mptShortcut:{gesture:'Ctrl+K',textInput:false}}]);
});
test('rebind and disable immediately release the old shortcut', () => {
  const h=harness(); h.configure([{gesture:'Ctrl+K'}]);
  h.configure([{gesture:'Ctrl+J'}]); assert.equal(h.press().defaultPrevented,false);
  assert.equal(h.press({key:'j',code:'KeyJ'}).defaultPrevented,true);
  h.configure([]); assert.equal(h.press({key:'j',code:'KeyJ'}).defaultPrevented,false);
});
for (const property of ['isComposing','defaultPrevented','repeat']) test(`${property} does not dispatch`, () => {
  const h=harness(); h.configure([{gesture:'Ctrl+K',allowInTextInput:true}]);
  h.press({[property]:true}); assert.equal(h.messages.length,0);
});
test('IME compatibility key and AltGraph do not dispatch', () => {
  const h=harness();h.configure([{gesture:'Ctrl+K',allowInTextInput:true}]);
  h.press({keyCode:229});h.press({getModifierState: () => true});assert.equal(h.messages.length,0);
});
for (const element of [{tagName:'INPUT'},{tagName:'TEXTAREA'},{tagName:'SELECT'},{isContentEditable:true}])
  test(`text input ${JSON.stringify(element)} requires explicit opt-in`, () => {
    const h=harness();h.configure([{gesture:'Ctrl+K',allowInTextInput:false}]);
    assert.equal(h.press({composedPath:()=>[element]}).defaultPrevented,false);
    h.configure([{gesture:'Ctrl+K',allowInTextInput:true}]);h.press({composedPath:()=>[element]});
    assert.equal(h.messages[0].__mptShortcut.textInput,true);
  });
test('macOS Meta and punctuation use the same canonical representation as native dispatch', () => {
  const h=harness(); h.configure([{gesture:'Shift+Win+OemComma',allowInTextInput:true}]);
  h.press({key:'<',code:'Comma',ctrlKey:false,metaKey:true,shiftKey:true});
  assert.equal(h.messages[0].__mptShortcut.gesture,'Shift+Win+OemComma');
});
test('plain Enter remains a normal editor input without an explicit binding', () => {
  const h=harness();h.configure([{gesture:'Ctrl+Enter',allowInTextInput:true}]);
  assert.equal(h.press({key:'Enter',code:'Enter',ctrlKey:false,composedPath:()=>[{tagName:'TEXTAREA'}]}).defaultPrevented,false);
});
