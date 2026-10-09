"""Collects result POSTs from the spike extension into /tmp/juvin-nm-spike/reports.jsonl."""
import json
from http.server import BaseHTTPRequestHandler, HTTPServer

class H(BaseHTTPRequestHandler):
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        with open("/tmp/juvin-nm-spike/reports.jsonl", "ab") as f:
            f.write(body + b"\n")
        self.send_response(204); self.end_headers()
    def log_message(self, *a):
        pass

HTTPServer(("127.0.0.1", 18765), H).serve_forever()
