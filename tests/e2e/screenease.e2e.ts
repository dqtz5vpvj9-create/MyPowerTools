import { test, expect } from 'e2e';
import { runDotnetTests } from './helpers/dotnet.ts';

const runtime = 'MyPowerTools.Tests.RuntimeAcceptanceTests.';
const filter = (names: string[]) => names.map(name => `FullyQualifiedName=${runtime}${name}`).join('|');

test('ScreenEase editor, reminders, schedule, import, overlay and navigation workflows', { tags: ['screenease', 'workflow'], timeout: 180_000 }, async () => {
  const result = await runDotnetTests('FullyQualifiedName~PersonalUx.Tests.ScreenEaseWorkflowTests|FullyQualifiedName~PersonalUx.Tests.PersonalUxDraftTests', {
    project: 'tests/PersonalUx.Tests/PersonalUx.Tests.csproj', minimumTests: 12,
  });
  expect(result.counters.passed).toBeGreaterThanOrEqual(12);
});

test('ScreenEase timer and logical effect persist across module recreation', { tags: ['screenease', 'runtime'] }, async () => {
  const result = await runDotnetTests(filter([
    'ScreenEase_rest_timer_state_survives_module_recreation_without_display_writes',
    'ScreenEase_pause_resume_and_reset_are_persisted_module_actions',
    'ScreenEase_logical_effect_applies_without_display_hardware_and_survives_recreation',
    'ScreenEase_disable_changes_only_the_logical_effect',
    'ScreenEase_manual_apply_creates_the_original_manual_effect_without_overwriting_a_saved_profile',
    'ScreenEase_schedule_selects_the_original_night_profile_values',
    'ScreenEase_schedule_change_immediately_applies_the_current_night_values',
  ]), { minimumTests: 7 });
  expect(result.counters.passed).toBe(7);
});

test('ScreenEase profiles, backups, migration and concurrent writes remain correct', { tags: ['screenease', 'persistence'] }, async () => {
  const result = await runDotnetTests(filter([
    'ScreenEase_new_install_uses_the_original_profile_ids_order_and_default_effect',
    'ScreenEase_imports_the_original_settings_contract_without_losing_manual_profiles',
    'ScreenEase_serializes_concurrent_profile_updates_without_lost_state',
    'ScreenEase_store_imports_the_original_file_on_first_run',
    'ScreenEase_store_replaces_generated_ids_from_the_original_file',
    'ScreenEase_store_recovers_a_valid_backup_and_quarantines_corruption',
    'ScreenEase_legacy_ini_import_maps_all_original_contract_fields_and_applies_them',
  ]), { minimumTests: 7 });
  expect(result.counters.passed).toBe(7);
});

test('ScreenEase hotkeys, overlay and hardware failures clean up through fake drivers', { tags: ['screenease', 'runtime'] }, async () => {
  const result = await runDotnetTests(filter([
    'ScreenEase_gamma_ramp_matches_the_original_driver_math',
    'ScreenEase_settings_schema_describes_all_eight_source_hotkeys',
    'ScreenEase_resets_the_gamma_ramp_once_when_an_applied_module_is_disposed',
    'ScreenEase_reports_a_hardware_reset_warning_after_logical_disable',
    'ScreenEase_dispose_resets_after_a_partial_hardware_apply',
    'ScreenEase_hotkey_commands_match_the_original_delta_toggle_and_profile_semantics',
    'ScreenEase_overlay_normalizes_applies_toggles_and_cleans_up_with_a_cancelled_dispose_token',
    'ScreenEase_apply_settings_updates_extended_values_preserves_partial_state_and_syncs_only_changed_hotkeys',
  ]), { minimumTests: 8 });
  expect(result.counters.passed).toBe(8);
});
