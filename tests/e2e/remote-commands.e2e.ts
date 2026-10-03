import { test, expect } from 'e2e';
import { runDotnetTests } from './helpers/dotnet.ts';

const project = 'tests/PersonalUx.Tests/PersonalUx.Tests.csproj';
const prefix = 'PersonalUx.Tests.PersonalUxRemoteCommandsWorkflowTests';
const selection = (methods: string[]) => methods.map(method => `FullyQualifiedName~${prefix}.${method}`).join('|');

test('Remote Commands all six local text transforms preserve their input contracts',
  { tags: ['remote-commands', 'text-workflow'], timeout: 180_000 }, async () => {
    const filter = [
      'Cpp_comment_transform_preserves_strings_and_line_endings',
      'Latex_comment_transform_drops_full_line_comments_only',
      'Latex_reflow_breaks_after_commas_and_periods_but_not_inside_commands',
      'Extract_prefix_and_rsync_transforms_match_the_original_contract',
      'Host_directory_transform_rewrites_the_legacy_path',
    ].map(method => `FullyQualifiedName=MyPowerTools.Tests.RemoteCommandsProductTests.${method}`).join('|');
    const result = await runDotnetTests(filter, { project, minimumTests: 5 });
    expect(result.counters.passed).toBe(5);
  });

test('Remote Commands controlled SSH upload, execution, invalid hosts, cancellation and cleanup',
  { tags: ['remote-commands', 'transport-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests(selection([
      'Upload_protocol_transmits_both_inputs_streams_output_and_cleans_files',
      'Upload_failure_stops_command_and_still_cleans_files',
      'Invalid_host_never_starts_upload_or_cleanup',
      'Cancellation_cleans_temp_inputs_and_uses_uncancelled_cleanup_token',
    ]), { project, minimumTests: 6 });
    expect(result.counters.passed).toBe(6);
  });

test('Remote Commands local execution, failure state, history restore, rerun and session persistence',
  { tags: ['remote-commands', 'viewmodel-workflow'], timeout: 180_000 }, async () => {
    const filter = selection([
      'Remote_failure_releases_busy_state_and_does_not_record_success_history',
      'Local_transform_history_search_restore_clear_and_session_round_trip',
      'Removed_command_cannot_be_rerun_and_unknown_transform_reports_failure',
    ]) + '|FullyQualifiedName=PersonalUx.Tests.PersonalUxRerunTests.Rerun_restores_the_host_inputs_and_second_pane_from_the_original_execution';
    const result = await runDotnetTests(filter, { project, minimumTests: 4 });
    expect(result.counters.passed).toBe(4);
  });

test('Remote Commands catalog validation, settings normalization, corrupt history and retention',
  { tags: ['remote-commands', 'persistence-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests(selection([
      'Invalid_catalog_preserves_previous_commands_and_settings_are_normalized',
      'Corrupt_history_is_preserved_until_explicit_clear_and_retention_is_enforced',
    ]), { project, minimumTests: 2 });
    expect(result.counters.passed).toBe(2);
  });
