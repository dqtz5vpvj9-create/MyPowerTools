import { test, expect } from 'e2e';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, realpath, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { repositoryRoot } from './helpers/process.ts';
import { runDotnetTests } from './helpers/dotnet.ts';

const runtime = resolve(repositoryRoot, 'tools/local-lag-cleaner/sdk-tool/src/LocalLagCleaner.Runtime/bin', process.env.MPT_E2E_CONFIGURATION ?? 'Debug', 'net10.0/LocalLagCleaner.Runtime.exe');

async function invoke(root: string, command: string, args: Record<string, unknown> = {}) {
  return await new Promise<any>((done, reject) => {
    const child = spawn(runtime, [], { windowsHide: true, shell: false, env: { ...process.env, MPT_DATA_ROOT: root }, stdio: ['pipe', 'pipe', 'pipe'] });
    let output = '', error = '';
    const timer = setTimeout(() => { child.kill(); reject(new Error(`Runtime timed out: ${command}`)); }, 110_000);
    child.stdout.on('data', chunk => { output += chunk; });
    child.stderr.on('data', chunk => { error += chunk; });
    child.on('error', failure => { clearTimeout(timer); reject(failure); });
    child.on('close', code => {
      clearTimeout(timer);
      if (code !== 0) return reject(new Error(`Runtime exit ${code}: ${error}`));
      try { const response = JSON.parse(output); expect(response.id).toBe('e2e'); done(response.result); }
      catch (failure) { reject(failure); }
    });
    child.stdin.end(JSON.stringify({ jsonrpc: '2.0', id: 'e2e', method: 'executeCommand', commandId: `local-lag-cleaner.${command}`, args }) + '\n');
  });
}

async function isolated(body: (root: string) => Promise<void>) {
  const root = await realpath(await mkdtemp(join(tmpdir(), 'mpt-lag-e2e-')));
  try { await body(root); }
  finally { await rm(root, { recursive: true, force: true }); }
}

test('local-lag-cleaner: real stdio health, empty persisted state and safe undo', async () => {
  await isolated(async root => {
    const health = await invoke(root, 'health');
    expect(health.state).toBe('ready');
    expect(health.payload.toolId).toBe('local-lag-cleaner');
    expect(health.payload.state).toBe('ready');
    expect((await invoke(root, 'care.last')).payload).toEqual({});
    expect((await invoke(root, 'cleanup.pending')).payload).toBeNull();
    expect((await invoke(root, 'care.restore')).payload.items).toEqual([]);
  });
});

test('local-lag-cleaner: real quick/deep scan exports reports and retained history', { timeout: 180_000 }, async () => {
  await isolated(async root => {
    for (const command of ['scan.quick', 'scan.deep']) {
      const result = await invoke(root, command);
      expect(result.state).toBe('ready');
      expect(result.payload.snapshot.processCount).toBeGreaterThan(0);
      expect(result.payload.snapshot.sampleSeconds).toBeGreaterThan(0);
      for (const key of ['jsonReportPath', 'markdownReportPath', 'historyJsonPath']) {
        const path = result.payload[key];
        expect(path.startsWith(root)).toBe(true);
        const report = await readFile(path, 'utf8');
        expect(report.length).toBeGreaterThan(100);
        if (key !== 'markdownReportPath') expect(JSON.parse(report).capturedAtUtc).toBe(result.payload.snapshot.capturedAtUtc);
      }
    }
  });
});

test('local-lag-cleaner: six remediation plans persist and invalid confirmations preserve the pending plan', { timeout: 360_000 }, async () => {
  await isolated(async root => {
    for (const action of ['mcp', 'weflow', 'delivery-optimization', 'nvidia-container', 'remote-desktop', 'windows-search']) {
      const result = await invoke(root, `plan.${action}`);
      // Real machine candidates vary; the absence of eligible MCP/WeFlow
      // processes must reject planning without creating a pending operation.
      if (result.state === 'failed' && ['mcp', 'weflow'].includes(action)) {
        expect(result.error.code).toBe('plan.rejected');
        expect(result.error.message).toContain(action === 'mcp' ? 'MCP' : 'WeFlow');
        expect((await invoke(root, 'cleanup.pending')).payload).toBeNull();
        continue;
      }
      expect(result.state).toBe('ready');
      const plan = result.payload;
      expect(plan.confirmationToken).toMatch(/^[0-9A-F]{8}$/);
      expect((await invoke(root, 'cleanup.pending')).payload.planId).toBe(plan.planId);
      const incorrectToken = (plan.confirmationToken[0] === 'A' ? 'B' : 'A') + plan.confirmationToken.slice(1);
      for (const args of [
        { planId: plan.planId, expectedAction: plan.action, confirmationToken: incorrectToken },
        { planId: 'A'.repeat(32), expectedAction: plan.action, confirmationToken: plan.confirmationToken },
        { planId: plan.planId, expectedAction: plan.action === 'WindowsSearch' ? 'DeliveryOptimization' : 'WindowsSearch', confirmationToken: plan.confirmationToken },
        ...(plan.requiresAdministrator ? [{ planId: plan.planId, expectedAction: plan.action, confirmationToken: plan.confirmationToken, allowServiceRestart: false }] : []),
      ]) {
        const rejected = await invoke(root, 'cleanup.apply', args);
        expect(rejected.state).toBe('failed');
        expect(rejected.error.code).toBe('plan.rejected');
        expect((await invoke(root, 'cleanup.pending')).payload.planId).toBe(plan.planId);
      }
    }
  });
});

test('local-lag-cleaner: runtime rejects malformed arguments without system changes', async () => {
  await isolated(async root => {
    for (const [command, args] of [
      ['health', { unexpected: true }],
      ['cleanup.apply', { planId: 'A'.repeat(32), expectedAction: 0, confirmationToken: 'A1B2C3D4' }],
      ['cleanup.apply', { planId: 'A'.repeat(32), expectedAction: 'McpResidue', confirmationToken: 'A1B2C3D4', allowDisconnect: 'yes' }],
    ] as const) {
      const response = await invoke(root, command, args);
      expect(response.state).toBe('failed');
      expect(response.error.code).toBe('validation.failed');
    }
  });
});

test('local-lag-cleaner: automatic memory/kernel checks perform three rounds and persist their outcome', { timeout: 180_000 }, async () => {
  await isolated(async root => {
    for (const scope of ['memory', 'kernel']) {
      const result = await invoke(root, `care.${scope}`);
      expect(result.state).toBe('ready');
      expect(result.payload.canUndo).toBe(false);
      expect(result.payload.before.processCount).toBeGreaterThan(0);
      expect(result.payload.after.processCount).toBeGreaterThan(0);
      expect(result.payload.items.length).toBeGreaterThanOrEqual(2);
      expect((await invoke(root, 'care.last')).payload.summary).toBe(result.payload.summary);
    }
  });
});

test('local-lag-cleaner: MPT discovery, real runtime routing and finding navigation', async () => {
  await runDotnetTests('FullyQualifiedName~Mpt_host_routes_health_from_development_tool_through_real_stdio_runtime|FullyQualifiedName~Finding_opens_its_own_solution_and_routes_the_action_to_the_matching_tool', { minimumTests: 2 });
});

test('local-lag-cleaner: cleanup confirmation, expiry, concurrency and stale process identity', async () => {
  await runDotnetTests('FullyQualifiedName~Cleanup_plan_requires_a_matching_unexpired_confirmation_token_before_execution|FullyQualifiedName~Cleanup_apply_binds_confirmation_to_plan_id_and_expected_action|FullyQualifiedName~Concurrent_plan_writers_leave_one_complete_pending_plan|FullyQualifiedName~Claimed_process_plan_is_consumed_when_live_identity_preflight_fails|FullyQualifiedName~LocalLagCleanerElevationPreflightTests', { minimumTests: 8 });
});

test('local-lag-cleaner: temporary file cleanup and native hard fault identity', async () => {
  await runDotnetTests('FullyQualifiedName~LocalLagCleanerAutomaticCareTests', { minimumTests: 3 });
});

test('local-lag-cleaner: measurement, paging, trend, remediation and report privacy regressions', async () => {
  await runDotnetTests('FullyQualifiedName~LocalLagCleanerMeasurementTests|FullyQualifiedName~LocalLagCleanerTrendTests|FullyQualifiedName~LocalLagCleanerRemediationTests|FullyQualifiedName~Mcp_analyzer|FullyQualifiedName~Diagnostic_thresholds_normalize|FullyQualifiedName~Health_score_caps|FullyQualifiedName~Reports_exclude_command_lines', { minimumTests: 25 });
});
