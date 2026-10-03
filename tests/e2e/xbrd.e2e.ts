import { test, expect } from 'e2e';
import { runDotnetTests } from './helpers/dotnet.ts';

const project = 'tests/e2e/xbrd-workflows/Xbrd.Workflows.Tests.csproj';

test('XBRD reads publisher health, sorted sources, expiry and panel preview', { tags: ['xbrd', 'workflow'] }, async () => {
  const result = await runDotnetTests('FullyQualifiedName~ReadHealthSourcesAndPanel', { project, minimumTests: 1 });
  expect(result.counters.passed).toBe(1);
});

test('XBRD refresh, disable, enable and delete send encoded control requests with execution evidence', { tags: ['xbrd', 'workflow'] }, async () => {
  const result = await runDotnetTests('FullyQualifiedName~ControlRefreshEnableDisableDeleteAndEvidence|FullyQualifiedName~OldPublisherControlIsUnsupported', { project, minimumTests: 3 });
  expect(result.counters.passed).toBe(3);
});

test('XBRD logs preserve content and reject HTTP errors even when body claims success', { tags: ['xbrd', 'regression'] }, async () => {
  const result = await runDotnetTests('FullyQualifiedName~HttpErrorCannotReportSuccessfulControlOrLog|FullyQualifiedName~LogReadClampsLimitAndPreservesLines', { project, minimumTests: 4 });
  expect(result.counters.passed).toBe(4);
});

test('XBRD malformed payloads fail safely and next refresh recovers from connection loss', { tags: ['xbrd', 'recovery'] }, async () => {
  const result = await runDotnetTests('FullyQualifiedName~MalformedOrRejectedResponsesShowFailure|FullyQualifiedName~UnreachablePublisherRecoversOnNextRead', { project, minimumTests: 4 });
  expect(result.counters.passed).toBe(4);
});

test('XBRD memory and Codex units publish evidence and reject failed HTTP statuses', { tags: ['xbrd', 'regression'] }, async () => {
  const result = await runDotnetTests('FullyQualifiedName~OwnedUnitPublishReturnsEvidence|FullyQualifiedName~HttpErrorCannotReportSuccessfulUnitPublish', { project, minimumTests: 4 });
  expect(result.counters.passed).toBe(4);
});
