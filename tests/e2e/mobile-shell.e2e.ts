import { test } from '@e2e-dev/mobile';
import { expect } from 'e2e';
import { assertForeground, tap } from './mobile-support';

// Read-only navigation: no settings, favorites, pairing, or tool actions are changed.
test('Mobile root navigation stays usable across two complete tab cycles', async ({ app, screen }) => {
  await app.open();
  await assertForeground();
  for (let round = 0; round < 2; round++) {
    for (const [tab, content] of [
      ['工具', '每一种本领，都有用武之地。'],
      ['设备', '各有所长，彼此相连。'],
      ['动态', '每件小事，都有着落。'],
    ]) {
      await tap(screen, screen.getByRole('button', { name: tab, exact: true }));
      await expect(screen.getByText(content, { exact: true })).toBeVisible();
      await assertForeground();
      await app.screenshot('tab-' + tab + '-' + round);
    }
    await tap(screen, screen.getByRole('button', { name: '常用', exact: true }));
    await expect(screen.getByRole('button', '打开文件传输助手')).toBeVisible();
  }
});

test('Settings opens from home and Android Back restores the usable home page', async ({ app, screen, device }) => {
  await app.open();
  await assertForeground();
  await tap(screen, screen.getByRole('button', '打开设置'));
  await expect(screen.getByRole('button', { name: '设备权限与审计', exact: true })).toBeVisible();
  await expect(screen.getByRole('button', { name: '文件同步', exact: true })).toBeVisible();
  await app.screenshot('settings');
  await device.back();
  await assertForeground();
  await expect(screen.getByRole('button', '打开文件传输助手')).toBeVisible();
  await app.screenshot('settings-back-home');
});
