import { test, expect } from 'e2e';
import { runDotnetTests } from './helpers/dotnet.ts';

// Exercise the production collector/storage pipeline with synthetic capture
// adapters and temporary databases; never install global input hooks.
const workflows = [
  {
    title: 'Input Monitor loads a native SQLite version containing the CVE-2025-6965 fix',
    filter: 'FullyQualifiedName~MyPowerTools.Tests.InputMonitorSqliteVersionTests',
    minimum: 1,
  },
  {
    title: 'Input Monitor collection: stop flush, restart, pause/resume, clear, settings, recovery and calendar history',
    filter: 'FullyQualifiedName~MyPowerTools.Tests.InputMonitorWorkflowTests',
    minimum: 8,
  },
  {
    title: 'Input Monitor statistics: SQLite, privacy, daily payloads and multi-monitor heatmaps',
    filter: 'FullyQualifiedName~MyPowerTools.Tests.InputMonitorProductTests&' +
      '(FullyQualifiedName~Sqlite_round_trip|FullyQualifiedName~Stats_payload|' +
      'FullyQualifiedName~Track_heat|FullyQualifiedName~Host_strips)',
    minimum: 5,
  },
  {
    title: 'Input Monitor rest workflows: remind, pause, manual rest, skip and stop overlay',
    filter: 'FullyQualifiedName~MyPowerTools.Tests.InputMonitorProductTests&' +
      '(FullyQualifiedName~Fatigue_|FullyQualifiedName~Manual_rest|' +
      'FullyQualifiedName~Host_skip_rest|FullyQualifiedName~Host_stop)',
    minimum: 6,
  },
  {
    title: 'Input Monitor activity: sampling, repeat suppression, foreground identity and categories',
    filter: 'FullyQualifiedName~MyPowerTools.Tests.InputMonitorProductTests&' +
      '(FullyQualifiedName~Event_sampler|FullyQualifiedName~Aggregator_|' +
      'FullyQualifiedName~Foreground_app|FullyQualifiedName~Unresolved_|FullyQualifiedName~Category_map)',
    minimum: 5,
  },
];

for (const workflow of workflows) {
  test(workflow.title, { tags: ['input-monitor', 'service-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests(workflow.filter, { minimumTests: workflow.minimum });
    expect(result.exitCode).toBe(0);
    expect(result.counters.executed).toBeGreaterThanOrEqual(workflow.minimum);
    expect(result.counters.passed).toBe(result.counters.total);
  });
}
