import { test, expect } from 'e2e';
import { runDotnetTests } from './helpers/dotnet.ts';

// Service-level workflows run the shipped C# implementation. ADB, SSH and
// privileged networking are controlled adapters, except explicit process tests.
const cases = [
  {
    title: 'ADB forwarding: wired and WiFi preflight, forwarding, retry, cancellation, SSH and cleanup',
    filter: 'FullyQualifiedName~MyPowerTools.Tests.AdbForwardingWorkflowTests',
    minimum: 19,
  },
  {
    title: 'ADB settings: atomic save/reload, duplicate rejection, wired/WiFi editors and preservation',
    filter: 'FullyQualifiedName~MyPowerTools.Tests.AdbForwarderEnvironmentSettingsTests',
    minimum: 4,
  },
  {
    title: 'ADB shared-port boundaries: prevent derived TCP overflow and preserve saved settings',
    filter: 'FullyQualifiedName~MyPowerTools.Tests.AdbForwarderPortBoundsTests',
    minimum: 4,
  },
  {
    title: 'ADB product commands: mapping, preview invalidation, revert, approval, selection and timeout',
    filter: 'FullyQualifiedName~MyPowerTools.Tests.AdbForwarderProductTests&FullyQualifiedName!~Shell_wires&FullyQualifiedName!~Manifest_and_shell_route',
    minimum: 10,
  },
  {
    title: 'ADB broker workflows: tamper protection, single-use approval, state conflict and rollback',
    filter: 'FullyQualifiedName~MyPowerTools.Tests.AdbBrokerSecurityTests&FullyQualifiedName!~Source_gates&FullyQualifiedName!~Installed_resolver',
    minimum: 13,
  },
];

for (const scenario of cases) {
  test(scenario.title, { tags: ['adb-forwarder', 'service-workflow'], timeout: 180_000 }, async () => {
    const result = await runDotnetTests(scenario.filter, { minimumTests: scenario.minimum });
    expect(result.exitCode).toBe(0);
    expect(result.counters.passed).toBe(result.counters.total);
    expect(result.counters.executed).toBeGreaterThanOrEqual(scenario.minimum);
  });
}
