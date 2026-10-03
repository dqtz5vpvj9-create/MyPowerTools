import { test, expect } from 'e2e';
import { runDotnetTests } from './helpers/dotnet.ts';

test('Paste Image destination safety, configuration, inspect and read-only clipboard probe',
  { tags: ['paste-image', 'service-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests([
      'Settings_reject_hosts_that_OpenSSH_treats_as_options',
      'Settings_reject_unsafe_remote_directories',
      'Configure_inspect_and_probe_use_platform_provider_without_writing_clipboard',
      'Configured_upload_timeout_is_respected_by_command_deadline',
    ].map(method => `FullyQualifiedName~MyPowerTools.Tests.PasteImageWorkflowTests.${method}`).join('|'), { minimumTests: 8 });
    expect(result.counters.passed).toBe(8);
  });

test('Paste Image persisted history, absent capabilities and corrupt preview recovery',
  { tags: ['paste-image', 'service-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests([
      'History_reads_existing_records_and_preserves_corrupt_file',
      'Missing_capabilities_fail_upload_and_notification_with_structured_results',
      'Corrupt_preview_is_optional_and_does_not_abort_history_loading',
    ].map(method => `FullyQualifiedName=MyPowerTools.Tests.PasteImageWorkflowTests.${method}`).join('|'), { minimumTests: 3 });
    expect(result.counters.passed).toBe(3);
  });

test('Paste Image history display, path copy, empty clipboard error and busy recovery',
  { tags: ['paste-image', 'viewmodel-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests([
      'Surface_loads_latest_five_records_and_copies_selected_path_to_injected_writer',
      'Surface_reports_empty_clipboard_and_releases_busy_state',
    ].map(method => `FullyQualifiedName=MyPowerTools.Tests.PasteImageWorkflowTests.${method}`).join('|'), { minimumTests: 2 });
    expect(result.counters.passed).toBe(2);
  });
