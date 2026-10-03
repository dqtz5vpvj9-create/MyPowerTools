import type { E2EConfig } from 'e2e';

// Avalonia's native controls are exercised by its headless host; socket and
// relay tests start the production modules and relay in isolated directories.
export default {
  tests: 'tests/e2e/**/*.e2e.ts',
  targets: [{ name: 'windows-file-transfer', platform: 'windows' }],
  workers: 1,
  timeout: 240_000,
  retries: 0,
  reporters: ['list', 'junit', 'markdown'],
  output: '.e2e/file-transfer',
} satisfies E2EConfig;
