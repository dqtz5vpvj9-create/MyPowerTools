import type { E2EConfig } from 'e2e';

// Avalonia is a native desktop application. These targets exercise its real
// workflow/runtime APIs; browser/mobile engines do not drive desktop controls.
export default {
  tests: 'tests/e2e/**/*.e2e.ts',
  targets: [{ name: 'windows-workflows', platform: 'windows' }],
  workers: 1,
  timeout: 180_000,
  retries: 0,
  cache: 'off',
  trace: 'off',
  reporters: ['list', 'junit', 'markdown'],
  output: '.e2e',
} satisfies E2EConfig;
