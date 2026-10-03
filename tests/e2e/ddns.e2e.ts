import { test, expect } from 'e2e';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { mkdtemp, realpath, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

const execute = promisify(execFile);
async function scenario(name: string) {
  const root = await realpath(await mkdtemp(join(tmpdir(), 'mpt-ddns-e2e-')));
  try {
    const { stdout } = await execute('pwsh.exe', ['-NoLogo', '-NoProfile', '-NonInteractive', '-File',
      resolve('tests/e2e/helpers/ddns.ps1'), '-Scenario', name, '-DataRoot', root], { windowsHide: true, timeout: 30_000 });
    return JSON.parse(stdout.trim());
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

test('DDNS build distributes the current scripts into package and service', async () => {
  const actual = await scenario('distribution');
  expect(actual.packageHash).toBe(actual.sourceHash);
  expect(actual.serviceHash).toBe(actual.sourceHash);
});

test('DDNS initial status reports that no update has run', async () => {
  const actual = await scenario('fresh-status');
  expect(actual.error).toBe('');
  expect(actual.result.checkedAt).toBeNull();
  expect(actual.calls).toHaveLength(0);
});
test('DDNS status reads persisted state without querying DNSPod', async () => {
  const actual = await scenario('saved-status');
  expect(actual.error).toBe('');
  expect(actual.result).toMatchObject({ wanIp: '203.0.113.8', updated: true });
  expect(actual.calls).toHaveLength(0);
});
test('DDNS watch records API failure and reaches its next interval', async () => {
  const actual = await scenario('watch-error');
  expect(actual.error).toBe('TEST-WATCH-STOP');
  expect(actual.log).toContain('ERROR: DNSPod Record.List failed: test denied');
  expect(actual.saved).toBeNull();
});
test('DDNS creates a missing A record and persists its status', async () => {
  const actual = await scenario('create');
  expect(actual.error).toBe('');
  expect(actual.calls.map((x: any) => x.action)).toEqual(['Record.List', 'Record.Create']);
  expect(actual.saved).toMatchObject({ updated: true, wanIp: '203.0.113.4', recordId: 'created' });
  expect(actual.calls[1].body).toMatchObject({ value: '203.0.113.4', record_type: 'A', sub_domain: 'qa' });
});
test('DDNS updates an existing changed address', async () => {
  const actual = await scenario('update');
  expect(actual.error).toBe('');
  expect(actual.calls.map((x: any) => x.action)).toEqual(['Record.List', 'Record.Modify']);
  expect(actual.calls[1].body.record_id).toBe('1');
  expect(actual.saved.updated).toBe(true);
});
test('DDNS unchanged address leaves duplicate records untouched', async () => {
  const actual = await scenario('unchanged');
  expect(actual.error).toBe('');
  expect(actual.calls.map((x: any) => x.action)).toEqual(['Record.List']);
  expect(actual.saved.updated).toBe(false);
});
test('DDNS force update retains the matching record and cleans the extra', async () => {
  const actual = await scenario('force-cleanup');
  expect(actual.error).toBe('');
  expect(actual.calls.map((x: any) => x.action)).toEqual(['Record.List', 'Record.Modify', 'Record.Remove']);
  expect(actual.calls[1].body.record_id).toBe('2');
  expect(actual.calls[2].body.record_id).toBe('1');
});
test('DDNS read-only listing preserves all records without writing state', async () => {
  const actual = await scenario('list');
  expect(actual.error).toBe('');
  expect(actual.result).toHaveLength(2);
  expect(actual.calls.map((x: any) => x.action)).toEqual(['Record.List']);
  expect(actual.saved).toBeNull();
});
test('DDNS API failure surfaces provider error and writes no successful status', async () => {
  const actual = await scenario('api-error');
  expect(actual.error).toContain('test denied');
  expect(actual.saved).toBeNull();
});
test('DDNS rejects invalid IPv4 before calling DNSPod', async () => {
  const actual = await scenario('invalid-ip');
  expect(actual.error).toContain('IPv4');
  expect(actual.calls).toHaveLength(0);
  expect(actual.saved).toBeNull();
});
test('DDNS scheduled task retains its explicitly selected configuration', async () => {
  const actual = await scenario('task');
  expect(actual.error).toBe('');
  expect(actual.result.Action.Argument).toContain(`-ConfigPath "${actual.configPath}"`);
  expect(actual.result.Trigger.Interval).toBe(2);
});
