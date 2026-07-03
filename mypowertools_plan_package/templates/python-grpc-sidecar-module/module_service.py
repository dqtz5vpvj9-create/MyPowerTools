#!/usr/bin/env python3
"""
Python gRPC sidecar skeleton.

生成代码建议：
  python -m grpc_tools.protoc \
    -I ../../proto \
    --python_out=. \
    --grpc_python_out=. \
    ../../proto/mpt_module_v1.proto

Windows 生产实现应接入 Named Pipe transport。
macOS/Linux 生产实现应接入 Unix Domain Socket transport。
该模板展示服务语义，具体 transport bootstrap 由 Python SDK 封装。
"""

import datetime

MODULE_ID = "sample.python-grpc-module"


def utc_now() -> str:
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


class SampleModuleService:
    """Pseudo implementation matching ModuleControl semantics."""

    def Initialize(self, request, context):
        return {
            "ok": True,
            "protocol_version": "1.0",
            "capabilities": ["status", "commands", "settings"],
        }

    def GetStatus(self, request, context):
        return {
            "module_id": MODULE_ID,
            "state": "MODULE_STATE_RUNNING",
            "summary": "Python gRPC sidecar is running",
            "updated_at": utc_now(),
            "event_seq": 1,
        }

    def ListCommands(self, request, context):
        return {
            "commands": [
                {
                    "id": f"{MODULE_ID}.hello",
                    "title": "Hello from Python gRPC",
                    "kind": "action",
                    "requires_elevation": False,
                }
            ]
        }

    def ExecuteCommand(self, request, context):
        return {
            "invocation_id": request.invocation_id,
            "command_id": request.command_id,
            "state": "COMMAND_STATE_SUCCEEDED",
            "success": True,
            "output": "Hello from Python gRPC sidecar",
        }


if __name__ == "__main__":
    raise SystemExit(
        "Use the MyPowerTools Python SDK transport bootstrap to host this service over Named Pipe or Unix Domain Socket."
    )
