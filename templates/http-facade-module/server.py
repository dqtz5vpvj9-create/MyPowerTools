from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path == "/api/status":
            self.write_json({"state": "running", "service": "sample-http-facade"})
            return

        if self.path == "/api/ping":
            self.write_json({"ok": True, "message": "pong from HTTP facade"})
            return

        self.send_response(404)
        self.end_headers()

    def write_json(self, payload):
        body = json.dumps(payload).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


if __name__ == "__main__":
    ThreadingHTTPServer(("127.0.0.1", 42080), Handler).serve_forever()
