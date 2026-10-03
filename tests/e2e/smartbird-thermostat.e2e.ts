import { test, expect } from 'e2e';
import { resolve } from 'node:path';
import { repositoryRoot, runProcess } from './helpers/process.ts';
import { runDotnetTests } from './helpers/dotnet.ts';

const sourceRoot = resolve(repositoryRoot, 'tools/smartbird-thermostat/original-source');
const project = 'tests/PersonalUx.Tests/PersonalUx.Tests.csproj';
const workflow = 'FullyQualifiedName~PersonalUx.Tests.SmartBirdWorkflowTests.';

async function pythonSuite(module: string, count: number) {
  const result = await runProcess(process.env.MPT_E2E_PYTHON ?? 'python', ['-B', '-m', 'unittest', module, '-v'], {
    cwd: sourceRoot, timeout: 90_000,
  });
  expect(result.exitCode).toBe(0);
  expect(result.stderr).toContain(`Ran ${count} tests`);
  expect(result.stderr).toContain('\nOK');
  expect(result.stderr).not.toContain('skipped=');
}

test('SmartBird cooling decisions, ADB fallback and persistent history', async () => {
  await pythonSuite('test_tools.test_smartbird_thermostat', 14);
});

test('SmartBird real HTTP sessions, modes, manual switch, energy and notification lifecycle', async () => {
  await pythonSuite('test_tools.test_smartbird_thermostat_service', 22);
});

test('SmartBird saved loopback endpoint refreshes online, invalid and unavailable status', async () => {
  const result = await runDotnetTests(workflow + 'Saved_endpoint_', { project });
  expect(result.counters.passed).toBe(1);
});

test('SmartBird connection test contacts a fake TCP device without writing settings', async () => {
  const result = await runDotnetTests(workflow + 'Connection_test_', { project });
  expect(result.counters.passed).toBe(1);
});

test('SmartBird configuration and notification recipients persist without plaintext credentials', async () => {
  const result = await runDotnetTests(workflow + 'Saved_notification_', { project });
  expect(result.counters.passed).toBe(1);
});

test('SmartBird rejects invalid timing and nonfinite settings without replacing either file', async () => {
  const result = await runDotnetTests(workflow + 'Invalid_numeric_', { project, minimumTests: 9 });
  expect(result.counters.passed).toBe(9);
});

test('SmartBird settings commands save, reload, validate and return to the console', async () => {
  const result = await runDotnetTests(workflow + 'Settings_commands_', { project });
  expect(result.counters.passed).toBe(1);
});
