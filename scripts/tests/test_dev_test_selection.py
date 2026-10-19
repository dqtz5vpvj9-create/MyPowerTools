"""Exercise selector orchestration with an inert fake dotnet; never run product code."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
PWSH = shutil.which("pwsh")
FAKE_DOTNET = r'''
import json, os, sys
from pathlib import Path
args = sys.argv[1:]
with open(os.environ['FAKE_DOTNET_LOG'], 'a') as log:
    log.write(json.dumps(args) + '\n')
if args[0] == 'msbuild':
    print(os.environ['FAKE_CONSOLE_DLL'])
elif args[0] == 'run':
    if '--list' in args:
        print('MPT_TEST_CASES_V1')
        count = 10 if 'screenshot' in ' '.join(args) else 11
        for i in range(count): print('console fixture ' + str(i))
    else: print('PASS fake console')
elif args[0] == 'test' and '--list-tests' in args:
    if os.environ.get('FAKE_DISCOVERY_FAIL'): sys.exit(17)
    print('The following Tests are available:')
    if not os.environ.get('FAKE_EMPTY'):
        print('    Fixture.Expected.first')
        print('    Fixture.Expected.second')
elif args[0] == 'test':
    sys.exit(int(os.environ.get('FAKE_TEST_EXIT', '0')))
elif args[0] != 'build':
    raise SystemExit('Unexpected fake command: ' + repr(args))
'''


@unittest.skipUnless(PWSH, "PowerShell 7 is required for selector orchestration tests")
class DevTestSelectionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="mpt-selector-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.log = self.directory / "commands.jsonl"
        executable = self.directory / ("dotnet.cmd" if os.name == "nt" else "dotnet")
        implementation = self.directory / "fake_dotnet.py"
        implementation.write_text(FAKE_DOTNET, encoding="utf-8")
        if os.name == "nt":
            executable.write_text(f'@"{sys.executable}" "{implementation}" %*\r\n', encoding="utf-8")
        else:
            executable.write_text(f'#!{sys.executable}\n' + FAKE_DOTNET, encoding="utf-8")
            executable.chmod(0o755)
        self.dll = self.directory / "fixture.dll"
        self.dll.write_bytes(b"inert fixture" + "MPT_TEST_CASES_V1".encode("utf-16le"))
        self.environment = dict(os.environ, PATH=str(self.directory) + os.pathsep + os.environ.get("PATH", ""),
                                FAKE_DOTNET_LOG=str(self.log), FAKE_CONSOLE_DLL=str(self.dll))

    def invoke(self, *arguments, **environment):
        self.log.unlink(missing_ok=True)
        result = subprocess.run([PWSH, "-NoLogo", "-NoProfile", "-NonInteractive", "-File",
                                 str(ROOT / "scripts/test-dev.ps1"), *arguments],
                                cwd=ROOT, env=dict(self.environment, **environment),
                                text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)
        commands = [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []
        return result, commands

    def test_default_plan_keeps_full_solution_and_standalone_suites_and_consoles(self):
        result, commands = self.invoke("-Plan")
        self.assertEqual(0, result.returncode, result.stdout)
        discovery = [c for c in commands if c[0] == "test"]
        self.assertEqual(4, len(discovery))
        self.assertEqual("MyPowerTools.slnx", discovery[0][1])
        self.assertTrue(all("--list-tests" in c and "--no-build" in c for c in discovery))
        cache_property = "-p:RestorePackagesPath=" + str(ROOT / "artifacts/sdk/global-packages")
        self.assertTrue(all(cache_property in c for c in discovery))
        self.assertEqual(3, len([c for c in commands if c[0] == "run"]))
        self.assertTrue(all("--list" in c for c in commands if c[0] == "run"))
        self.assertIn("TOTAL SELECTED CASES: 40", result.stdout)
        self.assertFalse(any(c[0] == "build" for c in commands))

    def test_console_selectors_list_without_execution(self):
        for tool, feature, project, count in (
            ("adb-forwarder", "Runtime", "AdbForwarder.Runtime.Regression", 11),
            ("xbrd", "Runtime", "Xbrd.Runtime.Regression", 11),
            ("screenshot", "Lifecycle", "Screenshot.Lifecycle.Regression", 10),
        ):
            with self.subTest(tool=tool):
                result, commands = self.invoke("-Area", "Tools", "-ToolId", tool,
                                               "-Feature", feature, "-Plan")
                self.assertEqual(0, result.returncode, result.stdout)
                self.assertEqual(["msbuild", "run"], [c[0] for c in commands])
                self.assertIn(project, commands[0][1])
                cache_property = "-p:RestorePackagesPath=" + str(ROOT / "artifacts/sdk/global-packages")
                self.assertEqual(tool == "xbrd", cache_property in commands[0])
                self.assertIn("--list", commands[1])
                self.assertIn(f"TOTAL SELECTED CASES: {count}", result.stdout)

    def test_core_default_builds_then_lists_then_runs_only_the_focused_project(self):
        result, commands = self.invoke("-Area", "Tools", "-ToolId", "input-monitor", "-Feature", "Core",
                                       "-ResultsDirectory", str(self.directory / "results"))
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual(["build", "test", "test"], [c[0] for c in commands])
        self.assertIn("InputMonitor.Core.Tests.csproj", commands[0][1])
        self.assertIn("--list-tests", commands[1])
        self.assertNotIn("--list-tests", commands[2])
        self.assertIn("--no-build", commands[2])
        self.assertIn("--no-restore", commands[2])
        self.assertFalse(any(arg.startswith("-p:RestorePackagesPath=") for c in commands for arg in c))

    def test_empty_discovery_is_a_failure_and_executes_nothing(self):
        result, commands = self.invoke("-Area", "Shell", "-Feature", "Failure", "-NoBuild", FAKE_EMPTY="1")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("Zero tests discovered", result.stdout)
        self.assertEqual(1, len(commands))
        self.assertIn("--list-tests", commands[0])

    def test_discovery_error_is_not_reported_as_an_empty_success(self):
        result, commands = self.invoke("-Area", "Shell", "-Feature", "Failure", "-Plan", FAKE_DISCOVERY_FAIL="1")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("code 17", result.stdout)
        self.assertEqual(1, len(commands))

    def test_test_failure_remains_a_failure(self):
        result, commands = self.invoke("-Area", "Settings", "-ToolId", "remote-notifications", "-NoBuild",
                                       "-ResultsDirectory", str(self.directory / "results"), FAKE_TEST_EXIT="3")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("code 3", result.stdout)
        self.assertEqual(2, len(commands))
        self.assertNotIn("Completed 2 selected cases", result.stdout)

    def test_unknown_tool_feature_is_rejected_before_dotnet(self):
        for tool, feature in (("input-monitor", "Surface"), ("remote-commands", "Execution")):
            with self.subTest(tool=tool, feature=feature):
                result, commands = self.invoke("-Area", "Tools", "-ToolId", tool,
                                               "-Feature", feature, "-Plan")
                self.assertNotEqual(0, result.returncode, result.stdout)
                self.assertIn("Unsupported tool/feature pair", result.stdout)
                self.assertEqual([], commands)

    def test_shell_filters_route_to_canonical_fixture_owners(self):
        for feature, project, classes in (
            ("Failure", "src/MyPowerTools.Tests/MyPowerTools.Tests.csproj",
             ("ShellFailurePresenterTests", "HostConnectionRecoveryTests")),
            ("Navigation", "tests/PersonalUx.Tests/PersonalUx.Tests.csproj",
             ("PersonalUxFavoritesTests", "PersonalUxSearchTests", "ShellBackNavigationUxTests")),
            ("Shortcuts", "tests/PersonalUx.Tests/PersonalUx.Tests.csproj", ()),
        ):
            with self.subTest(feature=feature):
                result, commands = self.invoke("-Area", "Shell", "-Feature", feature, "-List")
                self.assertEqual(0, result.returncode, result.stdout)
                self.assertEqual(1, len(commands))
                self.assertEqual(project, commands[0][1])
                if classes:
                    selected = commands[0][commands[0].index("--filter") + 1]
                    for name in classes:
                        self.assertIn("MyPowerTools.Tests." + name + ".", selected)
                else:
                    self.assertNotIn("--filter", commands[0])
                self.assertIn("-p:RestorePackagesPath=" + str(ROOT / "artifacts/sdk/global-packages"), commands[0])

    def test_unlistable_console_artifacts_are_rejected_before_launch(self):
        self.dll.write_bytes(b"old inert runner without listing protocol")
        for missing, error in ((False, "lacks safe --list support"), (True, "Built console artifact missing")):
            with self.subTest(missing=missing):
                if missing:
                    self.dll.unlink()
                result, commands = self.invoke("-Area", "Tools", "-ToolId", "screenshot", "-Feature", "Lifecycle", "-Plan")
                self.assertNotEqual(0, result.returncode)
                self.assertIn(error, result.stdout)
                self.assertEqual(["msbuild"], [c[0] for c in commands])

    def test_packaging_selects_full_rather_than_a_small_smoke_subset(self):
        result, commands = self.invoke("-Area", "Packaging", "-Plan")
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertIn("using Full coverage", result.stdout)
        self.assertEqual("MyPowerTools.slnx", commands[0][1])


if __name__ == "__main__":
    unittest.main()
