import { test, expect } from 'e2e';
import { runDotnetTests } from './helpers/dotnet.ts';

test('Process Monitor saves, restores, clears and scans an owned Windows process', {
  tags: ['process-monitor', 'service-workflow'], timeout: 180_000,
}, async () => {
  const result = await runDotnetTests('FullyQualifiedName~ProcessMonitorWorkflow_', { minimumTests: 4 });
  expect(result.counters.passed).toBe(result.counters.total);
});

test('Process Monitor empty configuration reports degradation and rejects an empty command save', {
  tags: ['process-monitor', 'validation'], timeout: 180_000,
}, async () => {
  const result = await runDotnetTests('FullyQualifiedName~AndroidTools_process_monitor_reports_actionable_degraded_state_when_watch_list_is_empty');
  expect(result.counters.passed).toBe(1);
});
