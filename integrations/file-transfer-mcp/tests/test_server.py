import asyncio
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

from fastmcp import Client
from fastmcp.exceptions import ToolError


spec = importlib.util.spec_from_file_location("mpt_transfer_mcp", Path(__file__).parents[1] / "server.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class TransferMcpTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(dir="/mnt/cache/data-cache")
        self.root = Path(self.directory.name)
        self.script = self.root / "fake cli.py"
        self.script.write_text('''import sys,json,time,os
from pathlib import Path
args=sys.argv[1:]
if "--probe-sleep" in args:
 Path(__file__).with_suffix(".pid").write_text(str(os.getpid()))
 time.sleep(60)
if "waitfailed" in args:
 print(json.dumps({"ok":False,"error":{"code":"receipt_wait_failed"},"data":{"accepted":True,"itemIds":["durable-accepted-item"]}}))
 sys.exit(1)
if "timedout" in args:
 print("secret-provider-diagnostic",file=sys.stderr)
 print(json.dumps({"ok":False,"error":{"code":"timeout"},"data":{"items":[{"itemId":"pending","confirmed":False}],"timedOut":True}}))
 sys.exit(3)
print(json.dumps({"ok":True,"command":args[1],"data":{"arguments":args}}))
''')
        self.cli = module.TransferCli([sys.executable, str(self.script)], "/data/root with spaces", "/data/socket")

    def tearDown(self):
        self.directory.cleanup()

    async def test_mcp_tools_preserve_paths_and_wait_for_receiver(self):
        server = module.create_server(self.cli)
        async with Client(server) as client:
            result = await client.call_tool("mpt_send_files", {
                "device": "phone-actual", "files": ["/files/安装 包;$(touch injected).apk"],
            })
            args = result.data["data"]["arguments"]
            self.assertIn("--wait", args)
            self.assertEqual(args[args.index("--to") + 1], "phone-actual")
            self.assertEqual(args[args.index("--file") + 1], "/files/安装 包;$(touch injected).apk")
            self.assertEqual(args[args.index("--data-root") + 1], "/data/root with spaces")
            self.assertNotIn("--conversation", args)
            tools = {tool.name: tool for tool in await client.list_tools()}
            self.assertTrue(tools["mpt_devices"].annotations.readOnlyHint)
            self.assertFalse(tools["mpt_send_files"].annotations.idempotentHint)
            self.assertIn("全部共享成员可见", tools["mpt_send_shared"].description)

    async def test_quark_remains_explicit_shared_send_and_receiver_filter(self):
        async with Client(module.create_server(self.cli)) as client:
            result = await client.call_tool("mpt_send_shared", {
                "files": ["/files/app.apk"], "receipt_from": "phone-actual", "via": "quark",
            })
            args = result.data["data"]["arguments"]
            self.assertEqual(args[args.index("--conversation") + 1], "shared")
            self.assertEqual(args[args.index("--receipt-from") + 1], "phone-actual")
            self.assertEqual(args[args.index("--via") + 1], "quark")
            self.assertNotIn("--to", args)

    async def test_timeout_is_error_and_keeps_item_ids_without_secret_stderr(self):
        with self.assertRaises(ToolError) as error:
            await self.cli.call("wait", "--item", "timedout")
        self.assertIn("pending", str(error.exception))
        self.assertNotIn("secret-provider-diagnostic", str(error.exception))

    async def test_failed_receipt_wait_keeps_accepted_send_ids_for_recovery(self):
        with self.assertRaises(ToolError) as error:
            await self.cli.call("send", "--text", "waitfailed")
        result = json.loads(str(error.exception))
        self.assertEqual(result["error"]["code"], "receipt_wait_failed")
        self.assertTrue(result["data"]["accepted"])
        self.assertEqual(result["data"]["itemIds"], ["durable-accepted-item"])

    async def test_client_cancellation_terminates_only_cli_child(self):
        task = asyncio.create_task(self.cli.call("status", "--probe-sleep"))
        marker = self.script.with_suffix(".pid")
        for _ in range(100):
            if marker.exists():
                break
            await asyncio.sleep(0.01)
        self.assertTrue(marker.exists())
        pid = int(marker.read_text())
        task.cancel()
        with self.assertRaises(asyncio.CancelledError):
            await task
        with self.assertRaises(ProcessLookupError):
            os.kill(pid, 0)

    async def test_stdio_protocol_round_trip(self):
        from fastmcp.client.transports import StdioTransport
        transport = StdioTransport(command=sys.executable,
            args=[str(Path(__file__).parents[1] / "server.py")],
            env={"MPT_COMMAND_JSON": json.dumps([sys.executable, str(self.script)])})
        async with Client(transport) as client:
            result = await client.call_tool("mpt_status")
            self.assertTrue(result.data["ok"])


if __name__ == "__main__":
    unittest.main()
