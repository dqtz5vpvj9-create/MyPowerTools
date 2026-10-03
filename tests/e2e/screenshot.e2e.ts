import { test, expect } from 'e2e';
import { runDotnetTests } from './helpers/dotnet.ts';

test('Screenshot backend discovery, portable config, capture, ownership and lifecycle',
  { tags: ['screenshot', 'service-workflow'], timeout: 180_000 }, async () => {
    // Excludes the source/manifest inspection test: these cases execute product behavior.
    const result = await runDotnetTests('FullyQualifiedName~ScreenshotProductTests&FullyQualifiedName!~Screenshot_manifest', { minimumTests: 21 });
    expect(result.counters.passed).toBe(21);
  });

test('Screenshot verified installation, rejection, cleanup, concurrency and retry',
  { tags: ['screenshot', 'service-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests('FullyQualifiedName~ScreenshotInstallationTests', { minimumTests: 9 });
    expect(result.counters.passed).toBe(9);
  });

test('Screenshot changing installations, stale inspection and recoverable failures',
  { tags: ['screenshot', 'service-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests('FullyQualifiedName~ScreenshotWorkflowTests', { minimumTests: 5 });
    expect(result.counters.passed).toBe(5);
  });
