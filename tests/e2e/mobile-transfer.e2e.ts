import { test, beforeAll, afterAll } from '@e2e-dev/mobile';
import { expect } from 'e2e';
import { writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { Peer, adb, packageName, assertForeground, phoneState, tap, waitUntil } from './mobile-support';

const peer = new Peer();
let connected = false;
let phoneId = '';

beforeAll(async () => { await peer.start(); });
afterAll(async () => { await peer.stop(); });

// Real phone and production peer use their normal relay; no transport is mocked.
test('Confirm a connection invitation and receive an actual relay message', async ({ app, screen }) => {
  await app.open();
  await expect(screen.getByRole('button', '打开文件传输助手')).toBeVisible();
  await assertForeground();
  const invitation = await peer.call('assistant.link.export');
  // Keep the credential-bearing intent outside the framework action log.
  await adb('shell', 'am', 'start', '-a', 'android.intent.action.VIEW', '-d', invitation.code, '-p', packageName);
  const joinButton = screen.getByRole('button', '加入这台设备');
  await expect(joinButton).toBeVisible();
  await tap(screen, joinButton);
  const expected = (await peer.call('assistant.inspect')).identity.conversationId;
  const state = await waitUntil(phoneState, state => state.identity.conversationId === expected);
  phoneId = state.identity.deviceId;
  connected = true;
  const marker = 'MPT-E2E 接收 ' + Date.now();
  const result = await peer.call('assistant.send', { text: marker });
  const id = result.itemIds[0];
  await waitUntil(phoneState, state => state.items.some((item: any) => item.id === id && item.state === 'available'));
  const receipt = await waitUntil(() => peer.call('assistant.inspect'), state =>
    state.items.some((item: any) => item.id === id && item.receipts?.some((receipt: any) => receipt.deviceId === phoneId)));
  expect(receipt.items.find((item: any) => item.id === id).receipts.length).toBeGreaterThan(0);
});

async function openChat(app: any, screen: any) {
  if (!connected) throw new Error('Connection prerequisite failed; this case has not been verified');
  await app.open();
  await expect(screen.getByRole('button', '打开文件传输助手')).toBeVisible();
  await tap(screen, screen.getByRole('button', '打开文件传输助手'));
  await expect(screen.getByRole('button', '打开会话 文件传输助手')).toBeVisible();
  await tap(screen, screen.getByRole('button', '打开会话 文件传输助手'));
  await expect(screen.getByRole('button', '发送')).toBeVisible();
}

test('Phone text reaches the peer once and finishes with a receipt', async ({ app, screen }) => {
  await openChat(app, screen);
  const marker = 'MPT-E2E phone ' + Date.now();
  await screen.getByLabel('消息内容').fill(marker);
  await expect(screen.getByRole('button', '发送')).toBeEnabled();
  await tap(screen, screen.getByRole('button', '发送'));
  const received = await waitUntil(() => peer.call('assistant.inspect'), state =>
    state.items.some((item: any) => item.text === marker && item.state === 'available'));
  expect(received.items.filter((item: any) => item.text === marker).length).toBe(1);
  await waitUntil(phoneState, state => state.items.some((item: any) =>
    item.text === marker && item.receipts?.some((receipt: any) => receipt.deviceId === 'mobile-e2e-peer')));
  await app.screenshot('phone-send-completed');
});

test('Draft survives process restart without sending itself', async ({ app, screen }) => {
  await openChat(app, screen);
  const marker = 'MPT-E2E unsent draft ' + Date.now();
  await screen.getByLabel('消息内容').fill(marker);
  await tap(screen, screen.getByRole('button', '返回会话列表'));
  await openChat(app, screen);
  await expect(screen.getByLabel('消息内容')).toHaveValue(marker);
  expect((await peer.call('assistant.inspect')).items.some((item: any) => item.text === marker)).toBe(false);
  await screen.getByLabel('消息内容').fill('');
  await app.screenshot('draft-after-restart');
});

test('Cancelling the Android file picker preserves the composer and adds no attachment', async ({ app, screen, device }) => {
  await openChat(app, screen);
  const original = await phoneState();
  await tap(screen, screen.getByRole('button', '添加附件'));
  // Android's system picker is expected here, so do not apply the MPT foreground guard to Back.
  await device.back();
  await expect(screen.getByRole('button', '发送')).toBeVisible();
  await assertForeground();
  expect((await phoneState()).items.length).toBe(original.items.length);
  await app.screenshot('picker-cancelled');
});

test('Background reception and five resume cycles preserve live controls', async ({ app, screen, device }) => {
  await openChat(app, screen);
  for (let round = 0; round < 5; round++) {
    await device.home();
    const marker = 'MPT-E2E background ' + round + ' ' + Date.now();
    const sent = await peer.call('assistant.send', { text: marker });
    const id = sent.itemIds[0];
    // Receiver receipt must precede foregrounding; reopening cannot manufacture this result.
    await waitUntil(() => peer.call('assistant.inspect'), state => state.items.some((item: any) =>
      item.id === id && item.receipts?.some((receipt: any) => receipt.deviceId === phoneId)));
    await device.openApp(packageName);
    await assertForeground();
    await expect(screen.getByRole('button', '发送')).toBeVisible();
    await screen.getByLabel('消息内容').fill('resume-' + round);
    await expect(screen.getByLabel('消息内容')).toHaveValue('resume-' + round);
    await screen.getByLabel('消息内容').fill('');
    await app.screenshot('resume-' + round);
  }
});

test('Two identically named attachments retain their exact independent bytes', async ({ app, screen }) => {
  await openChat(app, screen);
  const filename = 'MPT-E2E-duplicate-' + Date.now() + '.txt';
  const bodies = ['第一份内容\n', '第二份内容\n'];
  const ids: string[] = [];
  for (const body of bodies) {
    const path = join(peer.root, filename);
    await writeFile(path, body);
    const sent = await peer.call('assistant.send', { paths: [path] });
    ids.push(sent.itemIds[0]);
    await waitUntil(phoneState, state => state.items.some((item: any) => item.id === ids.at(-1) && item.state === 'available'));
  }
  const state = await phoneState();
  for (let i = 0; i < ids.length; i++) {
    const file = state.items.find((item: any) => item.id === ids[i]);
    expect(file.localPath).toBeTruthy();
    expect(await adb('shell', 'run-as', packageName, 'cat', file.localPath)).toBe(bodies[i]);
    expect(file.bytesDone).toBe(file.size);
  }
  expect(state.items.find((item: any) => item.id === ids[0]).localPath)
    .not.toBe(state.items.find((item: any) => item.id === ids[1]).localPath);
  await app.screenshot('received-duplicates');
});

test('Deleting only the test attachment allows receiving that same message again', async ({ app, screen }) => {
  await openChat(app, screen);
  const filename = 'MPT-E2E-redownload-' + Date.now() + '.txt';
  const body = '删除后重新接收，仍然是同一条消息。\n';
  const path = join(peer.root, filename);
  await writeFile(path, body);
  const sent = await peer.call('assistant.send', { paths: [path] });
  const id = sent.itemIds[0];
  const state = await waitUntil(phoneState, state => state.items.some((item: any) => item.id === id && item.state === 'available'));
  const file = state.items.find((item: any) => item.id === id);
  expect(file.localPath.endsWith('/' + filename)).toBe(true);
  await adb('shell', 'run-as', packageName, 'rm', file.localPath);
  await tap(screen, screen.getByRole('button', '消息操作 ' + filename));
  await expect(screen.getByRole('button', '重新下载')).toBeVisible();
  await tap(screen, screen.getByRole('button', '重新下载'));
  await waitUntil(async () => {
    try { return await adb('shell', 'run-as', packageName, 'cat', file.localPath); }
    catch { return null; }
  }, value => value === body);
  expect((await phoneState()).items.filter((item: any) => item.id === id).length).toBe(1);
  await app.screenshot('redownload-completed');
});

test('App-only IPv4 outage queues data and recovers automatically without losing ADB', async ({ app, screen }) => {
  await openChat(app, screen);
  // Opt-in on a dedicated rooted QA device. Never toggle Wi-Fi used by TCP ADB.
  if (process.env.MPT_E2E_NETWORK_FAULTS !== '1') throw new Error('Not verified: set MPT_E2E_NETWORK_FAULTS=1 on a dedicated rooted QA phone');
  const packageInfo = await adb('shell', 'dumpsys', 'package', packageName);
  const uid = packageInfo.match(/userId=(\d+)/)?.[1];
  if (!uid) throw new Error('Cannot identify the test app UID');
  const rule = '-m owner --uid-owner ' + uid + ' -m comment --comment mpt-e2e-' + Date.now() + ' -j REJECT';
  await adb('shell', 'su', '-c', 'iptables -I OUTPUT ' + rule);
  let blocked = true;
  try {
    const sent = await peer.call('assistant.send', { text: 'MPT-E2E offline ' + Date.now() });
    const id = sent.itemIds[0];
    await new Promise(resolve => setTimeout(resolve, 10_000));
    expect((await peer.call('assistant.inspect')).items.find((item: any) => item.id === id)
      .receipts?.some((receipt: any) => receipt.deviceId === phoneId) ?? false).toBe(false);
    await adb('shell', 'su', '-c', 'iptables -D OUTPUT ' + rule);
    blocked = false;
    await waitUntil(phoneState, state => state.items.some((item: any) => item.id === id && item.state === 'available'));
    await waitUntil(() => peer.call('assistant.inspect'), state => state.items.some((item: any) =>
      item.id === id && item.receipts?.some((receipt: any) => receipt.deviceId === phoneId)));
    await app.screenshot('network-recovered');
  } finally {
    if (blocked) await adb('shell', 'su', '-c', 'iptables -D OUTPUT ' + rule);
  }
});
