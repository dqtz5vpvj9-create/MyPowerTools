import { test, describe, beforeAll, afterAll } from '@e2e-dev/mobile';
import { expect } from 'e2e';
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { Peer, adb, packageName, phoneState, waitUntil } from './mobile-support';

describe('Background attachment recovery', { serial: true, skip: process.env.MPT_E2E_POWER !== '1' }, () => {
  const peer = new Peer();
  beforeAll(async () => { await peer.start(); });
  afterAll(async () => { await peer.stop(); });
  async function screenOff() {
    await waitUntil(() => adb('shell', 'dumpsys', 'power'), power =>
      /mWakefulness=(Asleep|Dozing)/.test(power) && power.includes('mHalInteractiveModeEnabled=false'), 10000);
  }

  for (const mode of ['background', 'locked', 'doze', 'doze-network-loss']) {
    test(`${mode}: receive exactly once without reopening the app`, async ({ app }) => {
      await app.open();
      const initial = await phoneState();
      expect(initial.identity.conversationId).toBe((await peer.call('assistant.inspect')).identity.conversationId);
      const read = async () => JSON.parse(await adb('shell', 'run-as', packageName, 'cat',
        'files/MyPowerTools/state/modules/file-transfer/data/assistant/assistant.json'));
      const powerBefore = await adb('shell', 'dumpsys', 'power');
      expect(powerBefore.includes('mWakefulness=Awake')).toBe(true);
      const doze = mode.startsWith('doze');
      let restoreDeepDisabled = false;
      let dropRule: string[] | undefined;
      async function restoreNetwork() {
        if (!dropRule) return;
        await adb('shell', 'su', '0', 'iptables', '-D', 'OUTPUT', ...dropRule);
        dropRule = undefined;
      }
      try {
        await adb('shell', 'input', 'keyevent', '3');
        if (mode !== 'background') await adb('shell', 'input', 'keyevent', '223');
        if (mode !== 'background') await screenOff();
        if (doze) {
          // Some QA phones have Doze disabled globally. Exercise real deep idle,
          // then restore that original device setting rather than claiming a pass in ACTIVE.
          restoreDeepDisabled = (await adb('shell', 'dumpsys', 'deviceidle', 'enabled', 'deep')).trim() === '0';
          if (restoreDeepDisabled) await adb('shell', 'dumpsys', 'deviceidle', 'enable', 'deep');
          await adb('shell', 'dumpsys', 'battery', 'unplug');
          await adb('shell', 'dumpsys', 'deviceidle', 'force-idle');
          expect(await adb('shell', 'dumpsys', 'deviceidle', 'get', 'deep')).toContain('IDLE');
        }
        if (mode === 'doze-network-loss') {
          const packageRow = await adb('shell', 'cmd', 'package', 'list', 'packages', '-U', packageName);
          const uid = packageRow.match(/uid:(\d+)/)?.[1];
          if (!uid) throw new Error('QA application UID is unavailable');
          const rule = ['-m', 'owner', '--uid-owner', uid, '-m', 'comment', '--comment', 'mpt-e2e-doze-' + Date.now(), '-j', 'DROP'];
          await adb('shell', 'su', '0', 'iptables', '-I', 'OUTPUT', ...rule);
          dropRule = rule;
        }
        const body = `MPT-E2E-${mode}\n`.repeat(65536);
        const path = join(peer.root, `MPT-E2E-${mode}-${Date.now()}.txt`);
        await writeFile(path, body);
        const id = (await peer.call('assistant.send', { paths: [path] })).itemIds[0];
        await waitUntil(() => peer.call('assistant.inspect'), state => state.items.some((x: any) => x.id === id && x.state === 'stored'));
        let recoveredAt = Date.now();
        if (doze) {
          // A restricted app may wait for Android's maintenance window. Restore network
          // eligibility while keeping the screen off and without launching the Activity.
          await new Promise(resolve => setTimeout(resolve, 5000));
          console.log('Received during forced Doze:', (await read()).items.some((x: any) => x.id === id && x.state === 'available'));
          if (dropRule) expect((await read()).items.some((x: any) => x.id === id && x.state === 'available')).toBe(false);
          await restoreNetwork();
          await adb('shell', 'dumpsys', 'deviceidle', 'unforce');
          recoveredAt = Date.now();
        }
        const received = await waitUntil(read, state => state.items.some((x: any) => x.id === id && x.state === 'available'), 90000);
        console.log(`${mode}: receive after publication/network restoration (ms):`, Date.now() - recoveredAt);
        if (mode !== 'background') await screenOff();
        expect(received.items.filter((x: any) => x.id === id).length).toBe(1);
        const item = received.items.find((x: any) => x.id === id);
        expect(item.bytesDone).toBe(Buffer.byteLength(body));
        expect(await adb('shell', 'run-as', packageName, 'cat', item.localPath)).toBe(body);
        await waitUntil(() => peer.call('assistant.inspect'), state => state.items.some((x: any) =>
          x.id === id && x.receipts?.some((receipt: any) => receipt.deviceId === initial.identity.deviceId)));
      } finally {
        await restoreNetwork();
        if (doze) {
          await adb('shell', 'dumpsys', 'deviceidle', 'unforce');
          await adb('shell', 'dumpsys', 'battery', 'reset');
          if (restoreDeepDisabled) await adb('shell', 'dumpsys', 'deviceidle', 'disable', 'deep');
        }
        await adb('shell', 'input', 'keyevent', '224');
        await adb('shell', 'wm', 'dismiss-keyguard');
        await app.open();
      }
    });
  }
});
