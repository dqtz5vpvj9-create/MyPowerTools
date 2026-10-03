import { test, expect } from 'e2e';
import { runDotnetTests } from './helpers/dotnet.ts';
import { spawn } from 'node:child_process';
import { resolve } from 'node:path';
import { repositoryRoot } from './helpers/process.ts';

async function runtimeRequest(request: string) {
  const runtime = resolve(repositoryRoot, 'tools/ime-manager/sdk-tool/src/ImeManager.Runtime/bin',
    process.env.MPT_E2E_CONFIGURATION ?? 'Debug', 'net10.0/ImeManager.Runtime.dll');
  return await new Promise<any>((done, reject) => {
    const process = spawn('dotnet', [runtime], { cwd: repositoryRoot, windowsHide: true, shell: false, stdio: ['pipe', 'pipe', 'pipe'] });
    let output = '';
    let errors = '';
    process.stdout.on('data', chunk => { output += chunk; });
    process.stderr.on('data', chunk => { errors += chunk; });
    const timer = setTimeout(() => { process.kill(); reject(new Error('IME runtime request timed out')); }, 20_000);
    process.on('error', error => { clearTimeout(timer); reject(error); });
    process.on('close', code => {
      clearTimeout(timer);
      if (code !== 0) { reject(new Error(`Runtime exited ${code}: ${errors}`)); return; }
      try { done(JSON.parse(output.trim())); } catch (error) { reject(error); }
    });
    process.stdin.on('error', reject);
    process.stdin.end(request + '\n');
  });
}

test('IME runtime process responds to health and rejects malformed or unsafe requests before mutation',
  { tags: ['ime-manager', 'process-workflow'], timeout: 180_000 }, async () => {
    const request = (commandId: string, args = {}) => JSON.stringify({ jsonrpc: '2.0', id: 'ime-e2e', commandId, args });
    const health = await runtimeRequest(request('ime-manager.health'));
    expect(health.id).toBe('ime-e2e');
    expect(health.result.state).toBe('ready');
    expect(health.result.payload.toolId).toBe('ime-manager');
    const snapshot = await runtimeRequest(request('ime-manager.snapshot'));
    expect(snapshot.result.state).toBe('ready');
    expect(snapshot.result.payload.snapshot.platform).toBe('windows');
    expect(Array.isArray(snapshot.result.payload.snapshot.enabled)).toBe(true);
    expect(Array.isArray(snapshot.result.payload.snapshot.available)).toBe(true);
    for (const [body, code] of [
      ['{broken', 'request.invalid'],
      [request('ime-manager.unknown'), 'command.not-found'],
      [request('ime-manager.health', { unexpected: true }), 'validation.failed'],
      [request('ime-manager.snapshot', { includeAllKeyboardLayouts: 'true' }), 'validation.failed'],
      [request('ime-manager.apply', { enabledTipStrings: [] }), 'validation.failed'],
      [request('ime-manager.apply', { enabledTipStrings: ['bad-tip'] }), 'validation.failed'],
      ['{"jsonrpc":"2.0","id":"x","id":"y","commandId":"ime-manager.health"}', 'request.invalid'],
    ]) {
      const response = await runtimeRequest(body);
      expect(response.result.state).toBe('failed');
      expect(response.result.error.code).toBe(code);
    }
  });

const methods = [
  'Tip_strings_canonicalize_keyboard_and_text_service_forms',
  'Assembly_item_values_round_trip_for_keyboard_and_text_service',
  'Planner_adds_reorders_and_rejects_an_empty_switch_list',
  'Catalog_apply_enables_disables_and_restores_order_through_the_platform',
  'Enabled_order_ignores_preload_language_placeholders_already_covered_by_user_profile',
  'Managed_scope_keeps_disabled_inputs_visible_after_apply',
];

test('IME identifiers, planner edits and platform apply preserve valid switching state',
  { tags: ['ime-manager', 'service-workflow'], timeout: 180_000 }, async () => {
    const filter = methods.map(method => `FullyQualifiedName=MyPowerTools.Tests.ImeManagerProductTests.${method}`).join('|');
    const result = await runDotnetTests(filter, { minimumTests: methods.length });
    expect(result.counters.passed).toBe(methods.length);
  });

test('IME draft add/filter/default/hotkeys/apply/discard/storage and error recovery',
  { tags: ['ime-manager', 'service-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests('FullyQualifiedName~MyPowerTools.Tests.ImeManagerWorkflowTests', { minimumTests: 6 });
    expect(result.counters.executed).toBe(result.counters.passed);
  });
