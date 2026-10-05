import { test } from '@e2e-dev/mobile';
import { expect } from 'e2e';
import { assertForeground, tap } from './mobile-support';

// Artifact collection deliberately includes the first visible frame, not just the
// settled picture. Visual acceptance requires reviewing these images separately.
test('Capture immediate and settled conversation frames after three process launches', { skip: process.env.MPT_E2E_DIAGNOSTICS !== '1' }, async ({ app, screen }) => {
  for (let round = 0; round < 3; round++) {
    await app.open();
    await assertForeground();
    await tap(screen, screen.getByRole('button', '打开文件传输助手'));
    await expect(screen.getByRole('button', '打开会话 文件传输助手')).toBeVisible();
    await tap(screen, screen.getByRole('button', '打开会话 文件传输助手'));
    await expect(screen.getByRole('button', '发送')).toBeVisible();
    await app.screenshot('launch-' + round + '-immediate');
    await new Promise(resolve => setTimeout(resolve, 500));
    await app.screenshot('launch-' + round + '-500ms');
    await new Promise(resolve => setTimeout(resolve, 1500));
    await app.screenshot('launch-' + round + '-settled');
    await assertForeground();
  }
});
