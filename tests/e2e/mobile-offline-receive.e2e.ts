import { test, describe, beforeAll, afterAll } from '@e2e-dev/mobile';
import { expect } from 'e2e';
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { Peer, adb, packageName, assertForeground, phoneState, tap, waitUntil } from './mobile-support';

describe('Public relay offline reception', { serial: true, skip: process.env.MPT_E2E_DIAGNOSTICS !== '1' }, () => {
  const peer = new Peer();
  beforeAll(async () => { await peer.start(); });
  afterAll(async () => { await peer.stop(); });

  test('A stopped phone receives an intact attachment after the sending process has exited', async ({ app, screen }) => {
    await app.open();
    await assertForeground();
    const initial = await phoneState();
    expect(initial.identity.conversationId).toBe((await peer.call('assistant.inspect')).identity.conversationId);
    const phoneId = initial.identity.deviceId;
    // Only the dedicated QA app is stopped; no device network or application data is changed.
    await adb('shell', 'am', 'force-stop', packageName);
    try {
      const body = 'MPT-E2E-offline-sender\n'.repeat(49152);
      const file = join(peer.root, 'MPT-E2E-offline-' + Date.now() + '.txt');
      await writeFile(file, body);
      const sent = await peer.call('assistant.send', { paths: [file] });
      const id = sent.itemIds[0];
      const published = await waitUntil(() => peer.call('assistant.inspect'), state =>
        state.items.some((item: any) => item.id === id && item.state === 'stored'));
      expect(published.items.find((item: any) => item.id === id).receipts
        ?.some((receipt: any) => receipt.deviceId === phoneId) ?? false).toBe(false);
      await peer.stop(); // Wait for close, not merely for the stop request.
      await app.open();
      const received = await waitUntil(phoneState, state =>
        state.items.some((item: any) => item.id === id && item.state === 'available'));
      const item = received.items.find((item: any) => item.id === id);
      expect(item.bytesDone).toBe(Buffer.byteLength(body));
      expect((await adb('shell', 'run-as', packageName, 'cat', item.localPath)) === body).toBe(true);
      await tap(screen, screen.getByRole('button', '打开文件传输助手'));
      await tap(screen, screen.getByRole('button', '打开会话 文件传输助手'));
      await app.screenshot('offline-sender-received');
    } finally {
      // Restore the QA app even when publication or download fails.
      await adb('shell', 'monkey', '-p', packageName, '-c', 'android.intent.category.LAUNCHER', '1');
    }
  });
});
