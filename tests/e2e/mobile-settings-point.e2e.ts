import { test } from '@e2e-dev/mobile';
import { expect } from 'e2e';
import { assertForeground } from './mobile-support';

// Explicit Pixel 4a diagnostic at the avatar's observed physical position.
// Passing does not repair or supersede the semantic settings navigation test.
test('Pixel 4a visible avatar opens settings and Android Back returns home', { skip: process.env.MPT_E2E_DIAGNOSTICS !== '1' }, async ({ app, screen, device }) => {
  await app.open();
  await assertForeground();
  await expect(screen.getByRole('button', '打开设置')).toBeVisible();
  console.log('Diagnostic avatar bounds:', await screen.getByRole('button', '打开设置').boundingBox());
  await app.screenshot('home-before-avatar');
  await screen.tapAt({ x: 975, y: 225 });
  await expect(screen.getByRole('button', { name: '设备权限与审计', exact: true })).toBeVisible();
  await expect(screen.getByRole('button', { name: '文件同步', exact: true })).toBeVisible();
  await app.screenshot('settings-at-observed-position');
  await device.back();
  await assertForeground();
  await expect(screen.getByRole('button', '打开文件传输助手')).toBeVisible();
  await app.screenshot('home-after-settings-back');
});
