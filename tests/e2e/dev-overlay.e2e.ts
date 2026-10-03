import { test, expect } from 'e2e';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { runProcess } from './helpers/process.ts';

test('Development overlay discovers and stages script-only service units', async () => {
  const root = await mkdtemp(join(tmpdir(), 'mpt-overlay-e2e-'));
  try {
    const result = await runProcess('pwsh.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-File', 'tests/e2e/helpers/dev-overlay.ps1', '-DataRoot', root]);
    expect(result.exitCode, result.stderr).toBe(0);
    expect(JSON.parse(result.stdout)).toEqual({ discovered: 1, payload: true, manifest: true, scriptDescendants: true, partialRollback: true });
  } finally { await rm(root, { recursive: true, force: true }); }
});
