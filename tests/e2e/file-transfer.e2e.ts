import { test, expect } from 'e2e';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { createHash } from 'node:crypto';

const execute = promisify(execFile);
const core = 'tools/file-transfer/tests/FileTransfer.Core.Tests/FileTransfer.Core.Tests.csproj';
const surface = 'tests/FileTransfer.Surface.Tests/FileTransfer.Surface.Tests.csproj';

async function workflow(project: string, filter: string, minimum: number) {
  // The relay appends namespace/item/upload names. Keep the test prefix short
  // enough for Windows Python installations without long-path support.
  const directory = await mkdtemp(join(tmpdir(), 'ft-'));
  try {
    try { await execute('dotnet', ['test', project, '--no-build', '--no-restore', '-c', 'Debug',
      '--nologo', '--filter', filter, '--logger', 'trx;LogFileName=results.trx',
      '--results-directory', directory], { windowsHide: true, timeout: 220_000, maxBuffer: 8 * 1024 * 1024,
        env: { ...process.env, MPT_TEST_TEMP: directory, MPT_INBOX_TEST_ROOT: join(directory, 'i'),
          MPT_REAL_INBOX_TEST_ROOT: join(directory, 'r') } }); }
    catch (error) {
      const failure = error as Error & { stdout?: string; stderr?: string };
      throw new Error(`${failure.message}\n${failure.stdout ?? ''}\n${failure.stderr ?? ''}`);
    }
    const trx = await readFile(resolve(directory, 'results.trx'), 'utf8');
    const counter = trx.match(/<Counters\s+([^>]+)\/?\s*>/);
    if (!counter) throw new Error('The native workflow produced no test results');
    const counts = Object.fromEntries([...counter[1].matchAll(/(\w+)="(\d+)"/g)].map(m => [m[1], Number(m[2])]));
    expect(counts.total).toBeGreaterThanOrEqual(minimum);
    expect(counts.executed).toBe(counts.total);
    expect(counts.passed).toBe(counts.total);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

test('Direct file transfer: exact bytes, collisions, cancellation and malformed offers', async () => {
  await workflow(core, 'FullyQualifiedName~FileTransfer.Tests.TransferTests', 10);
});

test('Public inbox: registration, uploads, receipts, cancellation and restart', async () => {
  await workflow(core, 'FullyQualifiedName~PublicInboxClientTests', 12);
});

test('Paired devices exchange files through the real relay without Tailscale or manual sync', async () => {
  await workflow(core, 'FullyQualifiedName~RealPublicInboxModuleTests', 3);
});

test('Shared files: fallback, bounded metadata and sender/relay restart', async () => {
  await workflow(core, 'FullyQualifiedName~SharedLocatorTests', 15);
});

test('Assistant: durable queue, failed downloads, cancellation and delivery receipts', async () => {
  await workflow(core, 'FullyQualifiedName~AssistantModuleTests', 20);
});

test('Conversation UI: send, retry, devices, drafts and received files', async () => {
  await workflow(surface, 'FullyQualifiedName~FileTransfer.Surface.Tests', 100);
});

test('Installed development tool matches the build and answers through the running Runner', async () => {
  if (!process.env.LOCALAPPDATA) throw new Error('This installation check requires Windows');
  const installed = join(process.env.LOCALAPPDATA, 'Programs/MyPowerTools');
  const hash = async (path: string) => createHash('sha256').update(await readFile(path)).digest('hex');
  expect(await hash(join(installed, 'modules/file-transfer/ui/surface/FileTransfer.Surface.dll')))
    .toBe(await hash('tools/file-transfer/artifacts/package/ui/surface/FileTransfer.Surface.dll'));
  expect(await hash(join(installed, 'Shell/MyPowerTools.AvaloniaSdk.dll')))
    .toBe(await hash('artifacts/build/bin/MyPowerTools.AvaloniaSdk/debug/MyPowerTools.AvaloniaSdk.dll'));
  const { stdout } = await execute('dotnet', ['artifacts/build/bin/MyPowerTools.Cli/debug/MyPowerTools.Cli.dll',
    'transfer', 'status', '--data-root', join(process.env.LOCALAPPDATA, 'MyPowerTools')],
    { windowsHide: true, timeout: 30_000 });
  const status = JSON.parse(stdout);
  expect(status.ok).toBe(true);
  expect(status.data.identity.id.length).toBeGreaterThan(0);
  expect(Array.isArray(status.data.items)).toBe(true);
});
