from http.server import ThreadingHTTPServer, SimpleHTTPRequestHandler
import json, os
class Handler(SimpleHTTPRequestHandler):
    def translate_path(self, path):
        raw = super().translate_path(path)
        return os.path.join(os.path.dirname(__file__), 'web', os.path.relpath(raw, os.getcwd()))
    def do_GET(self):
        if self.path == '/api/status':
            body=json.dumps({'state':'ready','summary':'template.web is running'}).encode(); self.send_response(200); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(body))); self.end_headers(); self.wfile.write(body); return
        if self.path == '/api/logs':
            body=json.dumps({'lines':['template.web ready']}).encode(); self.send_response(200); self.send_header('Content-Type','application/json'); self.send_header('Content-Length',str(len(body))); self.end_headers(); self.wfile.write(body); return
        return super().do_GET()
ThreadingHTTPServer(('127.0.0.1',43110),Handler).serve_forever()