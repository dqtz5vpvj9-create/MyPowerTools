"""Agent interface to the installed MPT Runner through its transfer CLI."""

import asyncio
import json
import os
import subprocess
from typing import Literal

from fastmcp import FastMCP
from fastmcp.exceptions import ToolError


class TransferCli:
    def __init__(self, command: list[str], data_root: str = "", endpoint: str = ""):
        self.command = command
        self.options = ["--json"]
        if data_root:
            self.options += ["--data-root", data_root]
        if endpoint:
            self.options += ["--endpoint-address", endpoint]

    async def call(self, *arguments: str) -> dict:
        try:
            process = await asyncio.create_subprocess_exec(
                *self.command, "transfer", *arguments, *self.options,
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
                **({"creationflags": subprocess.CREATE_NO_WINDOW} if os.name == "nt" else {}),
            )
        except FileNotFoundError as error:
            raise ToolError("MPT CLI 未安装。请配置 MPT_COMMAND_JSON 或将 mpt 加入 PATH。") from error
        try:
            stdout, _ = await process.communicate()
        except asyncio.CancelledError:
            # End only this IPC client. Accepted messages remain owned by the Runner.
            if process.returncode is None:
                process.terminate()
                try:
                    await asyncio.wait_for(process.wait(), 3)
                except asyncio.TimeoutError:
                    process.kill()
                    await process.wait()
            raise
        try:
            result = json.loads(stdout)
        except (json.JSONDecodeError, UnicodeDecodeError) as error:
            raise ToolError("MPT CLI 未返回 JSON。请检查安装版本和 Runner 状态。") from error
        if process.returncode != 0 or not result.get("ok", False):
            # Preserve pending IDs on timeout. Never expose subprocess stderr or credentials.
            raise ToolError(json.dumps(result, ensure_ascii=False))
        return result


def create_server(cli: TransferCli) -> FastMCP:
    mcp = FastMCP("MyPowerTools File Transfer")
    read = {"readOnlyHint": True, "destructiveHint": False}
    send = {"readOnlyHint": False, "destructiveHint": False, "idempotentHint": False, "openWorldHint": True}

    @mcp.tool(annotations=read)
    async def mpt_status() -> dict:
        """查看本机 MPT 传输服务及文件助手状态。不会启动第二个传输实例。"""
        return await cli.call("status")

    @mcp.tool(annotations=read)
    async def mpt_devices() -> dict:
        """列出设备及稳定 deviceId。发送前使用此工具选择真实接收设备。"""
        return await cli.call("devices")

    @mcp.tool(annotations=read)
    async def mpt_conversations() -> dict:
        """列出文件助手共享会话和设备私聊。共享会话对其全部成员可见。"""
        return await cli.call("conversations")

    @mcp.tool(annotations=send)
    async def mpt_send_files(device: str, files: list[str], text: str = "",
                             wait_for_receipt: bool = True, timeout_seconds: int = 600) -> dict:
        """向一个已配对设备私聊发送本机文件/文字。成功等待要求该设备真实接收回执。

        files 是 MCP 所在主机的路径。此私聊通道尚不支持夸克中转；不要把私聊
        自动改成共享会话。超时仍保留待发项，可用 mpt_wait_receipts 查询。
        """
        args = ["send", "--to", device]
        if text:
            args += ["--text", text]
        for path in files:
            args += ["--file", path]
        if wait_for_receipt:
            args += ["--wait", "--timeout", str(timeout_seconds)]
        return await cli.call(*args)

    @mcp.tool(annotations=send)
    async def mpt_send_shared(files: list[str], text: str = "", receipt_from: str = "",
                              via: Literal["auto", "quark"] = "auto",
                              wait_for_receipt: bool = True, timeout_seconds: int = 600) -> dict:
        """向文件助手公屏发送内容，全部共享成员可见；不是指定设备的私聊。

        大文件可选 via=quark，要求 Linux 端已连接夸克、设为默认并开启 cloud-only。
        receipt_from 指定要等待回执的设备，不改变公屏可见范围。超时不删除待发项。
        """
        args = ["send", "--conversation", "shared", "--via", via]
        if receipt_from:
            args += ["--receipt-from", receipt_from]
        if text:
            args += ["--text", text]
        for path in files:
            args += ["--file", path]
        if wait_for_receipt:
            args += ["--wait", "--timeout", str(timeout_seconds)]
        return await cli.call(*args)

    @mcp.tool(annotations=read)
    async def mpt_receipts(item_ids: list[str], receiver: str = "") -> dict:
        """查询各条消息的真实接收回执。已排队或已上传不等于已送达。"""
        args = ["receipts"]
        for item in item_ids:
            args += ["--item", item]
        if receiver:
            args += ["--from", receiver]
        return await cli.call(*args)

    @mcp.tool(annotations=read)
    async def mpt_wait_receipts(item_ids: list[str], receiver: str = "", timeout_seconds: int = 600) -> dict:
        """等待每条消息的接收回执；指定 receiver 时其他设备的回执不算成功。"""
        args = ["wait", "--timeout", str(timeout_seconds)]
        for item in item_ids:
            args += ["--item", item]
        if receiver:
            args += ["--from", receiver]
        return await cli.call(*args)

    @mcp.tool(annotations={"readOnlyHint": False, "destructiveHint": True})
    async def mpt_cancel(item_id: str) -> dict:
        """取消指定待发消息。不会批量清空队列。"""
        return await cli.call("cancel", "--item", item_id)

    @mcp.tool(annotations=read)
    async def mpt_cloud_accounts() -> dict:
        """查看 Linux 端网盘账号、默认账号和中转策略，不返回账号凭据。"""
        return await cli.call("cloud", "accounts")

    @mcp.tool(annotations=send)
    async def mpt_pair_device(invitation_file: str) -> dict:
        """从主机私密设备连接码文件配对一台设备，建立私聊权限。不会加入公屏。
        不要把连接码内容写进工具参数。
        """
        return await cli.call("pair", "--code-file", invitation_file)

    @mcp.tool(annotations=send)
    async def mpt_join_shared(invitation_file: str) -> dict:
        """从私密文件助手会话邀请文件加入公屏。不同于设备私聊配对。"""
        return await cli.call("join", "--code-file", invitation_file)

    @mcp.tool(annotations=send)
    async def mpt_cloud_select(account_id: str, mode: Literal["auto", "cloud-only"] = "cloud-only") -> dict:
        """明确设置 Linux 文件助手的默认网盘和持久中转策略，影响后续发送。
        cloud-only 不允许附件退回公网上传；auto 允许自动选择后端。
        """
        await cli.call("cloud", "default", "--account", account_id)
        return await cli.call("cloud", "preferences", "--mode", mode)

    @mcp.tool(annotations=send)
    async def mpt_cloud_authorize(provider: Literal["quark", "baidu"]) -> dict:
        """发起网盘授权并返回 operationId；已接入登录入口时才返回提供方网页。当前 Linux 需要已有有效凭据文件。"""
        return await cli.call("cloud", "authorize", "--provider", provider)

    @mcp.tool(annotations=send)
    async def mpt_cloud_complete(operation_id: str, credential_file: str,
                                 kind: Literal["cookie", "refreshToken"]) -> dict:
        """用主机上已有的私密凭据文件完成授权，凭据存入系统密钥环。
        不要把 cookie 或令牌内容写进工具参数；夸克用 cookie，百度用 refreshToken。
        """
        return await cli.call("cloud", "complete", "--operation", operation_id,
                              "--credential-file", credential_file, "--kind", kind)

    return mcp


def main() -> None:
    command = json.loads(os.environ.get("MPT_COMMAND_JSON", '["mpt"]'))
    if not isinstance(command, list) or not command or any(not isinstance(x, str) for x in command):
        raise ValueError("MPT_COMMAND_JSON must be a nonempty JSON array of command arguments")
    cli = TransferCli(command, os.environ.get("MPT_DATA_ROOT", ""), os.environ.get("MPT_ENDPOINT_ADDRESS", ""))
    create_server(cli).run(transport="stdio", show_banner=False)


if __name__ == "__main__":
    main()
