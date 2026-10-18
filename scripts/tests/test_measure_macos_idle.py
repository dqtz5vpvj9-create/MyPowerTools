"""Portable regressions for the CPU gate; no live processes or services are changed."""
import contextlib
import importlib.util
import io
import json
from pathlib import Path
import plistlib
import tempfile
import unittest
from unittest import mock


SCRIPT = Path(__file__).resolve().parents[1] / 'measure-macos-idle.py'
SPEC = importlib.util.spec_from_file_location('measure_macos_idle', SCRIPT)
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class FakeClock:
    def __init__(self):
        self.now = 0.0

    def monotonic(self):
        return self.now

    def sleep(self, seconds):
        self.now += seconds


def processes(cpu_seconds=(0.0, 0.0, 0.0)):
    names = sorted(gate.REQUIRED)
    return {pid: dict(pid=pid, parent=1, name=name, rss_kib=1024, cpu_seconds=cpu)
            for pid, (name, cpu) in enumerate(zip(names, cpu_seconds), start=10)}


class CpuGateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        root = Path(self.temporary.name)
        self.app = root / 'MyPowerTools.app'
        self.units = self.app / 'Contents/MacOS/ServiceUnits/units'
        self.units.mkdir(parents=True)
        with (self.app / 'Contents/Info.plist').open('wb') as stream:
            plistlib.dump({'CFBundleShortVersionString': '1.2.3'}, stream)
        self.output = root / 'reports/idle.json'

    def run_gate(self, options=(), before=None, after=None):
        before = processes() if before is None else before
        after = before if after is None else after
        clock = FakeClock()
        snapshots = iter([before])

        def snapshot(_app):
            return next(snapshots, after)

        argv = [str(SCRIPT), '--app', str(self.app), '--output', str(self.output), *options]
        with mock.patch.object(gate.platform, 'system', return_value='Darwin'), \
                mock.patch.object(gate.platform, 'mac_ver', return_value=('15.0', (), '')), \
                mock.patch.object(gate.time, 'monotonic', side_effect=clock.monotonic), \
                mock.patch.object(gate.time, 'sleep', side_effect=clock.sleep), \
                mock.patch.object(gate, 'snapshot', side_effect=snapshot), \
                mock.patch('sys.argv', argv), \
                contextlib.redirect_stdout(io.StringIO()), \
                contextlib.redirect_stderr(io.StringIO()):
            code = gate.main()
        return code, json.loads(self.output.read_text())

    def test_idle_complete_installation_passes(self):
        code, report = self.run_gate()
        self.assertEqual(0, code)
        self.assertTrue(report['cpu_gate_passed'])
        self.assertEqual(120, report['elapsed_seconds'])
        self.assertEqual('1.2.3', report['app_version'])
        self.assertEqual(24, len(report['intervals']))

    def test_combined_cpu_above_budget_fails(self):
        code, report = self.run_gate(after=processes((0.6, 0.6, 0.6)))
        self.assertEqual(1, code)
        self.assertFalse(report['cpu_gate_passed'])
        self.assertAlmostEqual(1.5, report['total_cpu_percent'])

    def test_gate_compares_unrounded_combined_cpu(self):
        # ps supplies hundredths of a CPU second; monotonic time is fractional.
        # 1.30 / 129.99 * 100 is over 1%, but rounding each host first gives 1%.
        code, report = self.run_gate(('--duration', '129.99', '--interval', '130'),
                                     after=processes((0.45, 0.45, 0.40)))
        self.assertEqual(1, code)
        self.assertFalse(report['cpu_gate_passed'])
        self.assertTrue(any('exceeds' in issue for issue in report['issues']))

    def test_exact_cpu_budget_passes(self):
        code, report = self.run_gate(after=processes((0.4, 0.4, 0.4)))
        self.assertEqual(0, code)
        self.assertTrue(report['cpu_gate_passed'])
        self.assertEqual(1.0, report['total_cpu_percent'])

    def test_missing_core_host_fails(self):
        before = processes()
        missing = before.pop(10)['name']
        code, report = self.run_gate(before=before)
        self.assertEqual(1, code)
        self.assertIn('Missing required hosts: ' + missing, report['issues'])

    def test_explicit_required_host_fails_when_missing(self):
        code, report = self.run_gate(('--required-host', 'Enabled.Service'))
        self.assertEqual(1, code)
        self.assertIn('Missing required hosts: Enabled.Service', report['issues'])

    def test_autostart_service_must_be_running(self):
        (self.units / 'enabled.json').write_text(json.dumps({
            'exec': '/Applications/MyPowerTools.app/Contents/MacOS/Enabled.Service',
            'autostart': True,
        }))
        code, report = self.run_gate()
        self.assertEqual(1, code)
        self.assertIn('Missing required hosts: Enabled.Service', report['issues'])

    def test_optional_service_need_not_be_running(self):
        (self.units / 'optional.json').write_text(json.dumps({
            'exec': '/Applications/MyPowerTools.app/Contents/MacOS/Optional.Service',
            'autostart': False,
        }))
        self.assertEqual(0, self.run_gate()[0])

    def test_process_churn_fails(self):
        after = processes()
        after[20] = dict(after[10], pid=20, name='New.Service')
        code, report = self.run_gate(after=after)
        self.assertEqual(1, code)
        self.assertIn('Process group changed during observation; repeat after startup settles.',
                      report['issues'])

    def test_short_observation_is_diagnostic(self):
        code, report = self.run_gate(('--duration', '119'))
        self.assertEqual(1, code)
        self.assertTrue(any('Diagnostic' in issue for issue in report['issues']))

    def test_nonfinite_arguments_are_rejected_before_measurement(self):
        # A NaN budget used to accept arbitrarily high CPU because all its
        # comparisons are false. Other nonfinite arguments can hang or crash.
        for option in ('--duration', '--interval', '--budget', '--warmup'):
            for value in ('nan', 'inf', '-inf'):
                with self.subTest(option=option, value=value):
                    argv = [str(SCRIPT), '--app', str(self.app),
                            '--output', str(self.output), f'{option}={value}']
                    with mock.patch.object(gate.platform, 'system', return_value='Darwin'), \
                            mock.patch.object(gate.time, 'sleep') as sleep, \
                            mock.patch('sys.argv', argv), \
                            contextlib.redirect_stderr(io.StringIO()), \
                            contextlib.redirect_stdout(io.StringIO()):
                        # Fail fast in the regression test if validation lets an
                        # invalid value reach measurement; do not wait or loop.
                        sleep.side_effect = AssertionError('measurement began for nonfinite input')
                        with self.assertRaises(SystemExit) as raised:
                            gate.main()
                        self.assertEqual(2, raised.exception.code)
                        sleep.assert_not_called()

    def test_nonpositive_duration_interval_budget_are_rejected(self):
        for option in ('--duration', '--interval', '--budget'):
            for value in ('0', '-1'):
                with self.subTest(option=option, value=value):
                    with self.assertRaises(SystemExit) as raised:
                        self.run_gate((f'{option}={value}',))
                    self.assertEqual(2, raised.exception.code)


class SnapshotTests(unittest.TestCase):
    def test_cpu_time_formats(self):
        for value, expected in [('00:00.03', 0.03), ('02:03.45', 123.45),
                                ('01:02:03.45', 3723.45), ('2-01:02:03', 176523)]:
            with self.subTest(value=value):
                self.assertAlmostEqual(expected, gate.cpu_seconds(value))

    def test_snapshot_counts_bundle_and_transitive_children_only(self):
        app = Path('/Applications/MyPowerTools.app')
        output = '''  PID  PPID      TIME   RSS COMM
  10     1   0:01.00  1024 /Applications/MyPowerTools.app/Contents/MacOS/Host
  11    10   0:02.00  2048 /usr/bin/child
  12    11   0:03.00  4096 /usr/bin/grandchild
  20     1   0:04.00  8192 /Applications/MyPowerTools.app.other/Host
  21     1   0:05.00  8192 /usr/bin/unrelated
'''
        with mock.patch.object(gate.subprocess, 'check_output', return_value=output):
            result = gate.snapshot(app)
        self.assertEqual({10, 11, 12}, set(result))
        self.assertEqual('Host', result[10]['name'])
        self.assertEqual(3.0, result[12]['cpu_seconds'])


if __name__ == '__main__':
    unittest.main()
