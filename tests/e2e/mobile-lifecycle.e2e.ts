import { test, describe, beforeAll, afterAll } from '@e2e-dev/mobile';
import { expect } from 'e2e';
import { Peer, adb, packageName, assertForeground, phoneState, tap, waitUntil } from './mobile-support';

// This diagnostic uses Android keyboard events after a visible UI tap. No send/draft API
// is substituted for a user action. Keep normal semantic-click and fill tests unchanged.
describe('Phone keyboard and lifecycle diagnostics', { serial: true, skip: process.env.MPT_E2E_DIAGNOSTICS !== '1' }, () => {
  const peer = new Peer();
  let phoneId = '';
  beforeAll(async () => { await peer.start(); });
  afterAll(async () => { await peer.stop(); });

  async function chat(app: any, screen: any) {
    await app.open();
    await assertForeground();
    const state = await phoneState();
    expect(state.identity.conversationId).toBe((await peer.call('assistant.inspect')).identity.conversationId);
    phoneId = state.identity.deviceId;
    await tap(screen, screen.getByRole('button', '打开文件传输助手'));
    await expect(screen.getByRole('button', '打开会话 文件传输助手')).toBeVisible();
    await tap(screen, screen.getByRole('button', '打开会话 文件传输助手'));
    await expect(screen.getByRole('button', '发送')).toBeVisible();
  }
  const draft = (state: any) => state.drafts[state.activeConversationKey]?.draftText ?? '';
  async function enter(screen: any, text: string) {
    await tap(screen, screen.getByRole('textbox', '写点文字，或添加文件…'));
    // Controlled ASCII markers avoid shell quoting and keyboard Unicode limitations.
    if (!/^[A-Za-z0-9-]+$/.test(text)) throw new Error('Only controlled ASCII diagnostic markers are allowed');
    await adb('shell', 'input', 'text', text);
    await waitUntil(phoneState, state => draft(state) === text, 15_000);
    await adb('shell', 'input', 'keyevent', 'KEYCODE_BACK');
    await assertForeground();
  }
  async function clear(screen: any) {
    await tap(screen, screen.getByRole('textbox', '写点文字，或添加文件…'));
    await adb('shell', 'input', 'keycombination', '113', '29'); // Ctrl+A
    await adb('shell', 'input', 'keyevent', 'KEYCODE_DEL');
    await waitUntil(phoneState, state => draft(state) === '', 15_000);
    await adb('shell', 'input', 'keyevent', 'KEYCODE_BACK');
  }

  test('Keyboard text sends once with the receiving peer receipt', async ({ app, screen }) => {
    await chat(app, screen);
    expect(draft(await phoneState())).toBe(''); // Do not overwrite an existing draft.
    const text = 'MPT-E2E-keyboard-' + Date.now();
    await enter(screen, text);
    await app.screenshot('keyboard-entered');
    await expect(screen.getByRole('button', '发送')).toBeEnabled();
    await tap(screen, screen.getByRole('button', '发送'));
    const peerState = await waitUntil(() => peer.call('assistant.inspect'), state =>
      state.items.some((item: any) => item.text === text && item.state === 'available'));
    expect(peerState.items.filter((item: any) => item.text === text).length).toBe(1);
    await waitUntil(phoneState, state => state.items.some((item: any) => item.text === text &&
      item.receipts?.some((receipt: any) => receipt.deviceId === 'mobile-e2e-peer')));
    await app.screenshot('keyboard-send-received');
  });

  test('Unsent draft survives a real process restart without automatic sending', async ({ app, screen }) => {
    await chat(app, screen);
    expect(draft(await phoneState())).toBe('');
    const text = 'MPT-E2E-draft-' + Date.now();
    await enter(screen, text);
    await app.restart();
    await assertForeground();
    await tap(screen, screen.getByRole('button', '打开文件传输助手'));
    await tap(screen, screen.getByRole('button', '打开会话 文件传输助手'));
    await expect(screen.getByRole('button', '发送')).toBeEnabled();
    expect(draft(await phoneState())).toBe(text);
    expect((await peer.call('assistant.inspect')).items.some((item: any) => item.text === text)).toBe(false);
    await app.screenshot('draft-after-process-restart');
    await clear(screen);
  });

  test('Five background receptions complete before foregrounding and controls stay live', async ({ app, screen, device }) => {
    await chat(app, screen);
    for (let round = 0; round < 5; round++) {
      await device.home();
      const text = 'MPT-E2E-background-' + round + '-' + Date.now();
      const sent = await peer.call('assistant.send', { text });
      await waitUntil(() => peer.call('assistant.inspect'), state => state.items.some((item: any) =>
        item.id === sent.itemIds[0] && item.receipts?.some((receipt: any) => receipt.deviceId === phoneId)));
      await device.openApp(packageName);
      await assertForeground();
      await expect(screen.getByRole('button', '发送')).toBeVisible();
      await enter(screen, 'MPT-E2E-resume-' + round);
      await expect(screen.getByRole('button', '发送')).toBeEnabled();
      await clear(screen);
      await app.screenshot('background-resume-' + round);
    }
  });
});
