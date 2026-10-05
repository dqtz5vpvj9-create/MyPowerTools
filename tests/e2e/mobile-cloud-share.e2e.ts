import { test, describe, beforeAll, afterAll } from '@e2e-dev/mobile';
import { expect } from 'e2e';
import { readFile } from 'node:fs/promises';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { Peer, adb, packageName, serial, phoneState, waitUntil } from './mobile-support';

describe('Independent cloud share download', { serial: true, skip: process.env.MPT_E2E_SHARE !== '1' }, () => {
  const peer = new Peer();
  beforeAll(async () => { await peer.start(); });
  afterAll(async () => { await peer.stop(); });

  test('A phone downloads from Quark after the sender exits, without provider login', async ({ app }) => {
    await app.open();
    const initial = await phoneState();
    expect(initial.identity.conversationId).toBe((await peer.call('assistant.inspect')).identity.conversationId);
    const accounts = await adb('shell', 'run-as', packageName, 'cat',
      'files/MyPowerTools/state/modules/file-transfer/data/cloud-accounts.json').catch(error => {
        if (String(error.stderr).includes('No such file')) return '{"accounts":[]}';
        throw error;
      });
    expect(JSON.parse(accounts).accounts.length).toBe(0);
    await adb('shell', 'am', 'force-stop', packageName);
    try {
      const { id } = await peer.call('e2e.share.publish');
      await peer.stop();
      await app.open();
      const state = await waitUntil(phoneState, state => state.items.some((x: any) => x.id === id && x.state === 'available'), 90000);
      const item = state.items.find((x: any) => x.id === id);
      expect(item.transportRoute).toBe('cloud-quark');
      expect(item.bytesDone).toBe(item.size);
      expect(state.items.filter((x: any) => x.id === id).length).toBe(1);
      const bytes = await promisify(execFile)('adb', ['-s', serial, 'exec-out', 'run-as', packageName, 'cat', item.localPath],
        { encoding: 'buffer', maxBuffer: 8 * 1024 * 1024, timeout: 30000 });
      expect(bytes.stdout.equals(await readFile(process.env.MPT_E2E_SHARE_PAYLOAD!))).toBe(true);
      console.log('Quark independent receipt:', { bytes: item.size, route: item.transportRoute, senderStopped: true });
    } finally {
      await app.open();
    }
  });
});
