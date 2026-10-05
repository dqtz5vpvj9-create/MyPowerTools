import type { E2EConfig } from 'e2e';
import { mobile, type DeviceProvider } from '@e2e-dev/mobile';

// Select by serial: several attached devices have the same model name.
const connectedPhone: DeviceProvider = {
  name: 'connected-android',
  async acquire() {
    return { id: 'mpt-phone', client: {}, deviceId: process.env.MPT_E2E_SERIAL ?? '10.33.0.155:5555' };
  },
  async release() {},
};

export default {
  tests: 'tests/e2e/mobile-*.e2e.ts',
  targets: [{ name: 'pixel-4a', engine: mobile({ platform: 'android', device: connectedPhone }),
    app: { bundleId: process.env.MPT_E2E_PACKAGE ?? 'com.mypowertools.android' } }],
  workers: 1,
  timeout: 120_000,
  launchTimeout: 120_000,
  assertionTimeout: 30_000,
  retries: 0,
  reporters: ['list', 'junit', 'markdown'],
  output: '.e2e/mobile-availability',
} satisfies E2EConfig;
