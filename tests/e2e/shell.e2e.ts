import { test, expect } from 'e2e';
import { runDotnetTests } from './helpers/dotnet.ts';

test('Shell dashboard and command catalog survive module budget cancellation', async () => {
  await runDotnetTests('FullyQualifiedName~ModuleHealthIsolationTests', { minimumTests: 3 });
});

test('Shell navigation, resident lifecycle and reconnect behavior', async () => {
  const result = await runDotnetTests(
    'FullyQualifiedName~ShellBackNavigationUxTests|FullyQualifiedName~HostConnectionRecoveryTests|FullyQualifiedName~ShellActivationTests',
  );
  expect(result.counters.failed).toBe(0);
});

test('Shell renders three distinct opaque Avalonia pages', async () => {
  const result = await runDotnetTests(
    'FullyQualifiedName~ToolProductVisualAcceptanceTests.Product_foundation_entry_renders_three_real_shell_pages',
  );
  expect(result.counters.passed).toBe(1);
});
