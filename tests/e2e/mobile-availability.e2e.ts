import { test } from '@e2e-dev/mobile';
import { expect } from 'e2e';
import { assertForeground } from './mobile-support';

test('Home opens the file assistant through its visible button', async ({ app, screen }) => {
  await app.open();
  await assertForeground();
  await expect(screen.getByRole('button', { name: '打开文件传输助手', exact: true })).toBeVisible();
  await app.screenshot('home');
  await screen.getByRole('button', { name: '打开文件传输助手', exact: true }).tap();
  await assertForeground();
  await expect(screen.getByRole('button', { name: '打开会话 文件传输助手', exact: true })).toBeVisible({ timeout: 20_000 });
  await screen.getByRole('button', { name: '打开会话 文件传输助手', exact: true }).tap();
  await assertForeground();
  await expect(screen.getByRole('button', { name: '发送', exact: true })).toBeVisible({ timeout: 20_000 });
  await app.screenshot('assistant');
});
