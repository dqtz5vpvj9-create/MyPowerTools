import { test, expect } from 'e2e';
import { runDotnetTests } from './helpers/dotnet.ts';

const project = 'tools/nssm-manager/sdk-tool/tests/NssmManager.Tests/NssmManager.Tests.csproj';

test('NSSM service editor install/search/controls/migration/recovery workflows with isolated runtime',
  { tags: ['nssm-manager', 'service-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests('FullyQualifiedName~NssmManager.Tests.NssmWorkflowTests', { project, minimumTests: 14 });
    expect(result.counters.executed).toBe(result.counters.passed);
  });

test('NSSM edit/delete previews require current explicit approval before submitting',
  { tags: ['nssm-manager', 'service-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests('FullyQualifiedName~NssmManager.Tests.NssmConfirmationTests', { project, minimumTests: 5 });
    expect(result.counters.passed).toBe(5);
  });

test('NSSM logging appends timestamps and rotates real temporary files through the production pumps',
  { tags: ['nssm-manager', 'io-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests('FullyQualifiedName~NssmManager.Tests.NssmIoTests|FullyQualifiedName~NssmManager.Tests.IoCompatibilityTests', { project, minimumTests: 20 });
    expect(result.counters.executed).toBe(result.counters.passed);
  });

test('NSSM hook child processes propagate environment and report exit failures/timeouts',
  { tags: ['nssm-manager', 'process-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests('FullyQualifiedName~NssmManager.Tests.NssmHookTests', { project, minimumTests: 15 });
    expect(result.counters.executed).toBe(result.counters.passed);
  });

test('NSSM environment edits preserve double-null blocks and migration snapshots round-trip',
  { tags: ['nssm-manager', 'service-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests('FullyQualifiedName~NssmManager.Tests.NssmDoubleNullTests|FullyQualifiedName~NssmManager.Tests.NssmEnvironmentTests|FullyQualifiedName~NssmManager.Tests.RuntimeContractTests', { project, minimumTests: 21 });
    expect(result.counters.executed).toBe(result.counters.passed);
  });
