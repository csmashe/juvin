#!/usr/bin/env python3
"""A stand-in OpenAI-compatible model server for the session kit's CI run.

Answers what OpenAiCompatibleProvider asks: /api/version with 404 (so the provider takes the
OpenAI route, not Ollama's), /v1/models with one model, and /v1/chat/completions with a fixed
synthetic reply streamed as server-sent events. It prints the message count of a request, never its content.

Usage: openai-stub.py [--port 11434] [--model juvin-stub]
"""

import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

REPLY = [
    "Hi Robin,\n\n",
    "April works for me, apart from the first week, when I have the audit. ",
    "Could we pick somewhere closer this year? Last year was a long drive.\n\n",
    "Thanks",
]


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    model = "juvin-stub"

    def log_message(self, fmt, *args):
        print("stub: %s %s" % (self.command, self.path), flush=True)

    def send_body(self, status, content_type, body):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path.rstrip("/") == "/v1/models":
            body = json.dumps({"object": "list", "data": [{"id": self.model, "object": "model", "owned_by": "stub"}]})
            self.send_body(200, "application/json", body.encode())
        else:
            self.send_body(404, "application/json", b'{"error":"not found"}')

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        request = json.loads(self.rfile.read(length) or b"{}")
        if self.path.rstrip("/") != "/v1/chat/completions":
            self.send_body(404, "application/json", b'{"error":"not found"}')
            return
        print("stub: chat request with %d messages" % len(request.get("messages", [])), flush=True)
        events = []
        for index, piece in enumerate(REPLY):
            chunk = {
                "id": "stub-1",
                "object": "chat.completion.chunk",
                "model": self.model,
                "choices": [{"index": 0, "delta": {"content": piece}, "finish_reason": None}],
            }
            if index == 0:
                chunk["choices"][0]["delta"]["role"] = "assistant"
            events.append(chunk)
        events.append(
            {
                "id": "stub-1",
                "object": "chat.completion.chunk",
                "model": self.model,
                "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}],
            }
        )
        events.append({"id": "stub-1", "object": "chat.completion.chunk", "model": self.model, "choices": [],
                       "usage": {"prompt_tokens": 100, "completion_tokens": 40, "total_tokens": 140}})
        body = "".join("data: %s\n\n" % json.dumps(event) for event in events) + "data: [DONE]\n\n"
        self.send_body(200, "text/event-stream", body.encode())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=11434)
    parser.add_argument("--model", default="juvin-stub")
    args = parser.parse_args()
    Handler.model = args.model
    server = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print("stub: listening on 127.0.0.1:%d" % args.port, flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
