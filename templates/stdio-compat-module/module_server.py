#!/usr/bin/env python3
import json
import sys
import datetime

MODULE_ID = "sample.stdio-compat-module"


def now():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def respond(request_id, result=None, error=None):
    payload = {"jsonrpc": "2.0", "id": request_id}
    if error is not None:
        payload["error"] = error
    else:
        payload["result"] = result
    print(json.dumps(payload, ensure_ascii=False), flush=True)


def handle(req):
    method = req.get("method")
    if method == "module.getStatus":
        return {"state": "running", "summary": "stdio compatibility module", "updatedAt": now()}
    if method == "module.listCommands":
        return {"commands": [{"id": f"{MODULE_ID}.hello", "title": "Hello", "kind": "action"}]}
    if method == "command.execute":
        return {"success": True, "output": "Hello from stdio fallback"}
    return None


def main():
    for line in sys.stdin:
        try:
            req = json.loads(line)
            result = handle(req)
            if result is None:
                respond(req.get("id"), error={"code": "MPT_UNKNOWN_METHOD", "message": req.get("method")})
            else:
                respond(req.get("id"), result=result)
        except Exception as exc:
            print(f"stdio compat module error: {exc}", file=sys.stderr)


if __name__ == "__main__":
    main()
