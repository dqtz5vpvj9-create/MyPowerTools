// Run with node --test tests/remote-notifications-detail-bridge.test.cjs.
// MPT_DETAIL_TEMPLATE_PATH allows the exact source to be tested outside a checkout.
const { readFileSync } = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const test = require('node:test');
const assert = require('node:assert/strict');
const source = process.env.MPT_DETAIL_TEMPLATE_PATH || path.join(__dirname,
  '../tools/remote-notifications/current-integration/src/RemoteNotifications.Surface/Services/RemoteNotificationHtmlDocument.cs');
const script = readFileSync(source, 'utf8').match(/<script>([\s\S]*?)<\/script>/)[1];
function create(backend = 'mac') {
  const received = [], events = {};
  const send = message => received.push(message);
  const window = backend === 'mac' ? { webkit: { messageHandlers: { mptBridge: { postMessage: send } } } }
    : backend === 'win' ? { chrome: { webview: { postMessage: send } } }
    : { invokeCSharpAction: send };
  const context = { window, URL, document: { baseURI: 'file:///tmp/message.html',
    addEventListener(name, fn) { events[name] = fn; } } };
  vm.runInNewContext(script, context);
  const key = (name, extras = {}) => events.keydown({ key: name,
    target: { tagName: 'BODY' }, preventDefault() {}, ...extras });
  return { received, events, context, key };
}
for (const backend of ['mac', 'win', 'legacy']) {
  test(`${backend}: close and both navigation directions use the active bridge`, () => {
    const c = create(backend); c.key('Escape'); c.key('ArrowLeft'); c.key('ArrowRight');
    assert.deepEqual(c.received, ['close', 'previous', 'next']);
  });
}
test('two exposed bridges do not duplicate messages', () => {
  const c = create('mac');
  c.context.window.chrome = { webview: { postMessage: message => c.received.push(message) } };
  c.key('ArrowRight'); assert.deepEqual(c.received, ['next']);
});
for (const modifier of ['ctrlKey', 'metaKey', 'altKey', 'shiftKey']) {
  test(`${modifier}: navigation preserves modified shortcuts`, () => {
    const c = create(); c.key('ArrowRight', { [modifier]: true }); assert.deepEqual(c.received, []);
  });
}
for (const target of [{ tagName: 'INPUT' }, { tagName: 'TEXTAREA' }, { tagName: 'DIV', isContentEditable: true }]) {
  test(`${target.tagName}: editable content keeps arrow keys`, () => {
    const c = create(); c.key('ArrowLeft', { target }); assert.deepEqual(c.received, []);
  });
}
function link(c, href, options = {}) {
  let prevented = false;
  const anchor = { href, getAttribute() { return href; } };
  c.events.click({ isTrusted: true, button: 0, target: { closest() { return anchor; } },
    preventDefault() { prevented = true; }, ...options });
  return prevented;
}
test('trusted HTTPS click opens externally and suppresses embedded navigation', () => {
  const c = create(); assert.equal(link(c, 'https://example.test/page'), true);
  assert.deepEqual(c.received, ['open-external:https://example.test/page']);
});
test('middle click opens externally exactly once', () => {
  const c = create(); assert.equal(link(c, 'https://example.test/', { button: 1 }), true);
  assert.equal(c.received.length, 1);
});
test('synthetic clicks do not launch applications', () => {
  const c = create(); link(c, 'https://example.test/', { isTrusted: false }); assert.deepEqual(c.received, []);
});
test('same-document anchors stay in the document', () => {
  const c = create(); assert.equal(link(c, '#heading'), false); assert.deepEqual(c.received, []);
});
test('non-web schemes are never forwarded to the system browser', () => {
  const c = create(); link(c, 'file:///tmp/a'); link(c, 'javascript:alert(1)'); assert.deepEqual(c.received, []);
});
