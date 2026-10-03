import { test, expect } from 'e2e';
import { runDotnetTests } from './helpers/dotnet.ts';

// Execute product service/view-model code against isolated runtime and HTTP fixtures.
// The allowlist excludes source-text assertions and real desktop/remote actions.
const workflows: Record<string, string[]> = {
  'refresh health, model discovery, and overlay diagnostics': [
    'Refresh_publishes_local_state_immediately_and_coalesces_slow_probes',
    'Local_runtime_snapshot_uses_the_real_three_service_contract',
    'Snapshot_merges_runtime_models_and_preserves_overlay_diagnostics',
  ],
  'submit a planner task, consume its SSE trace, and cancel it': [
    'Planner_stream_preserves_actions_screenshots_and_raw_json',
    'Planner_stream_exposes_retry_grounding_tool_results_and_errors',
    'Planner_trace_text_is_bounded_after_screenshot_payloads_are_removed',
    'Planner_task_cancellation_reaches_the_live_request',
  ],
  'show, update, hide, and validate the overlay': [
    'Tool_health_and_overlay_requests_use_the_private_auth_header_without_exposing_it',
    'Overlay_workflow_maps_every_original_action_and_clamps_show_parameters',
    'Overlay_http_200_requires_running_ready_visible_and_ok_semantics',
  ],
  'start, stop, and restart the managed runtime': [
    'Auto_start_initialization_reuses_an_already_ready_runtime',
    'Auto_start_launches_foundation_services_without_an_ark_key',
    'Runtime_control_uses_the_owned_controller_and_never_executes_legacy_scripts',
    'Wildcard_listener_is_allowed_when_all_real_services_are_healthy',
    'Restart_attempts_verified_legacy_stop_then_starts_managed_runtime',
  ],
  'save configuration, validate authentication, and restore preferences': [
    'Product_session_keeps_model_prompt_auto_start_and_overlay_values_across_navigation',
    'Module_settings_fix_all_internal_endpoints_and_force_canonical_values',
    'Product_configuration_saves_all_secrets_atomically_without_ever_returning_them',
    'Product_configuration_rejects_mismatched_local_authentication_before_writing',
    'Product_configuration_tests_planner_and_model_api_without_exposing_authorization',
    'Product_configuration_test_reports_tool_authentication_and_mcp_failures',
    'Product_configuration_test_uses_process_environment_when_the_secret_file_is_empty',
    'Saving_configuration_does_not_claim_external_runtime_was_restarted',
    'Product_preferences_survive_a_new_service_and_use_a_writable_planner_override',
  ],
};

for (const [workflow, methods] of Object.entries(workflows)) {
  test(`Doubao Computer Use: ${workflow}`, { tags: ['doubao-computer-use', 'workflow'] }, async () => {
    const filter = methods.map(method => `FullyQualifiedName=MyPowerTools.Tests.DoubaoAgentProductTests.${method}`).join('|');
    const actual = await runDotnetTests(filter, { minimumTests: methods.length });
    expect(actual.exitCode).toBe(0);
    expect(actual.counters.total).toBe(methods.length);
    expect(actual.counters.passed).toBe(methods.length);
    expect(actual.counters.failed).toBe(0);
  });
}
