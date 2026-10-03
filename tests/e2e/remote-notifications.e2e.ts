import { test, expect } from 'e2e';
import { runDotnetTests } from './helpers/dotnet.ts';

// All transports, stores and toast sinks here are isolated test doubles.
// Exact names exclude source-scanning and real platform-notification tests.
function product(names: string[]) {
  return names.map(name => `FullyQualifiedName=MyPowerTools.Tests.RemoteNotificationsProductTests.${name}`).join('|');
}

test('Remote Notifications signed HTTP pull maps messages and handles empty, invalid, auth, transport and cancellation outcomes', {
  tags: ['remote-notifications', 'http'], timeout: 180_000,
}, async () => {
  const result = await runDotnetTests([
    'FullyQualifiedName~RemoteNotificationWorkflow_',
    'FullyQualifiedName~Dotnet_signer_preserves_the_original_ed25519_hello_protocol',
    'FullyQualifiedName~Dotnet_poller_sends_the_signed_pull_contract_and_maps_notifications',
    'FullyQualifiedName~Dotnet_poller_reports_auth_and_missing_key_failures_without_external_processes',
  ].join('|'), { minimumTests: 10 });
  expect(result.counters.passed).toBe(result.counters.total);
});

test('Remote Notifications restores and filters history, searches text and navigates session messages', {
  tags: ['remote-notifications', 'history'], timeout: 180_000,
}, async () => {
  const result = await runDotnetTests(product([
    'Search_filters_message_content_immediately_and_respects_the_topic_filter',
    'Restored_history_uses_original_newest_first_feed_and_session_chip_filter',
    'Session_chain_resolves_the_message_position_oldest_first',
    'Session_chain_navigation_clamps_at_both_ends',
    'Independent_detail_window_resolves_server_and_fallback_message_ids',
    'Feed_preview_removes_the_repeated_topic_prefix',
  ]), { minimumTests: 6 });
  expect(result.counters.passed).toBe(6);
});

test('Remote Notifications resumes cursor, refreshes settings, deduplicates arrivals and persists clear and banner preference', {
  tags: ['remote-notifications', 'receive'], timeout: 180_000,
}, async () => {
  const result = await runDotnetTests(product([
    'Empty_pull_uses_the_original_idle_status_treatment',
    'Startup_without_a_persisted_server_cursor_backfills_the_recent_window',
    'Startup_resumes_from_the_persisted_server_cursor',
    'Saving_product_settings_rebuilds_the_poller_and_refreshes_immediately',
    'Polling_deduplicates_persists_and_marks_the_active_session_chip_unread',
    'Visible_message_arrival_keeps_the_inbox_items_source_instance',
    'Persisted_seen_ring_drops_a_replayed_message_outside_the_visible_history',
    'Persistent_toggle_is_forwarded_to_the_single_accepted_windows_toast',
    'Background_message_event_presents_each_persisted_notification_once',
    'Persistent_and_clear_actions_write_the_legacy_Page1_store_contract',
  ]), { minimumTests: 10 });
  expect(result.counters.passed).toBe(10);
});

test('Remote Notifications preserves quoted requests in history and renders concise banners and reference appendices', {
  tags: ['remote-notifications', 'message-format'], timeout: 180_000,
}, async () => {
  const result = await runDotnetTests(product([
    'Toast_contract_maps_persistent_to_reminder_and_targets_the_exact_message',
    'Toast_omits_the_quoted_user_request_from_the_banner_body',
    'Toast_omits_a_trailing_for_reference_quoted_request',
    'Feed_keeps_the_quoted_user_request_in_the_stored_message',
    'Display_moves_a_leading_quoted_user_request_to_a_reference_appendix',
    'Display_does_not_double_wrap_an_existing_for_reference_appendix',
    'Display_keeps_an_assistant_blockquote_that_is_not_a_reference_appendix',
  ]), { minimumTests: 7 });
  expect(result.counters.passed).toBe(7);
});

test('Remote Notifications retries failures, routes encoded activation IDs and filters read state through HostControl', {
  tags: ['remote-notifications', 'recovery'], timeout: 180_000,
}, async () => {
  const result = await runDotnetTests([
    product([
      'Sync_errors_use_a_compact_summary_with_expandable_technical_details_and_retry',
      'Activation_protocol_recovers_the_exact_encoded_message_id',
      'Activation_pipe_forwards_the_exact_message_to_the_running_shell_endpoint',
    ]),
    'FullyQualifiedName~Notification_center_updates_read_state_idempotently',
    'FullyQualifiedName~HostControl_filters_notifications_and_sets_read_state',
    'FullyQualifiedName~HostControl_rejects_unknown_notification_read_state_updates',
  ].join('|'), { minimumTests: 6 });
  expect(result.counters.passed).toBe(6);
});
