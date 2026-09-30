#!/usr/bin/env python3
"""A minimal OpenAI-compatible chat-completions endpoint for testing ApiKey mode without any real model.

It is a test double, not a model: every request gets a fixed reply. It exists so that the ApiKey-mode wiring
(provider base URL, API key header, model name, streaming) can be verified on a machine with no network at all,
for example under `unshare -n` on Linux. Nothing here is used by the host.

Usage:
  python tools/airgap/mock_openai_server.py --port 8089 --api-key test-key [--reply OK] [--log requests.jsonl]

Endpoints: GET /v1/models, POST /v1/chat/completions (streaming and non-streaming), POST /v1/responses (minimal).
Every request is appended to the log as one JSON line: method, path, authorization header (redacted to a
match/mismatch flag), the model name and whether streaming was requested.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    server_version = "mock-openai/1"
    api_key = "test-key"
    reply = "OK"
    log_path: str | None = None

    def log_message(self, fmt, *args):  # quiet by default; the JSON log is the record
        pass

    def _record(self, body: dict | None, stream: bool) -> None:
        auth = self.headers.get("Authorization", "")
        entry = {
            "ts": time.time(),
            "method": self.command,
            "path": self.path,
            "auth": "bearer-match" if auth == f"Bearer {self.api_key}" else ("missing" if not auth else "mismatch"),
            "model": (body or {}).get("model"),
            "stream": stream,
            "tools": len((body or {}).get("tools") or []),
        }
        line = json.dumps(entry)
        print(line, flush=True)
        if self.log_path:
            with open(self.log_path, "a", encoding="utf-8") as f:
                f.write(line + "\n")

    def _json(self, status: int, payload: dict) -> None:
        data = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def _unauthorized(self) -> None:
        self._json(401, {"error": {"message": "invalid api key", "type": "invalid_request_error"}})

    def do_GET(self):
        self._record(None, False)
        if self.headers.get("Authorization", "") != f"Bearer {self.api_key}":
            return self._unauthorized()
        if self.path.rstrip("/").endswith("/models"):
            return self._json(200, {"object": "list", "data": [{"id": "mock-model", "object": "model", "owned_by": "mock"}]})
        self._json(404, {"error": {"message": "not found"}})

    def do_POST(self):
        length = int(self.headers.get("Content-Length") or 0)
        raw = self.rfile.read(length) if length else b""
        try:
            body = json.loads(raw or b"{}")
        except json.JSONDecodeError:
            body = {}
        stream = bool(body.get("stream"))
        self._record(body, stream)
        if self.headers.get("Authorization", "") != f"Bearer {self.api_key}":
            return self._unauthorized()
        model = body.get("model") or "mock-model"
        if self.path.rstrip("/").endswith("/chat/completions"):
            return self._chat(model, stream)
        if self.path.rstrip("/").endswith("/responses"):
            return self._responses(model, stream)
        self._json(404, {"error": {"message": "not found"}})

    def _chat(self, model: str, stream: bool) -> None:
        created = int(time.time())
        if not stream:
            return self._json(200, {
                "id": "chatcmpl-mock", "object": "chat.completion", "created": created, "model": model,
                "choices": [{"index": 0, "message": {"role": "assistant", "content": self.reply}, "finish_reason": "stop"}],
                "usage": {"prompt_tokens": 10, "completion_tokens": 1, "total_tokens": 11},
            })
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.end_headers()
        chunks = [
            {"id": "chatcmpl-mock", "object": "chat.completion.chunk", "created": created, "model": model,
             "choices": [{"index": 0, "delta": {"role": "assistant", "content": self.reply}, "finish_reason": None}]},
            {"id": "chatcmpl-mock", "object": "chat.completion.chunk", "created": created, "model": model,
             "choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}],
             "usage": {"prompt_tokens": 10, "completion_tokens": 1, "total_tokens": 11}},
        ]
        for chunk in chunks:
            self.wfile.write(f"data: {json.dumps(chunk)}\n\n".encode())
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()

    def _responses(self, model: str, stream: bool) -> None:
        response = {
            "id": "resp-mock", "object": "response", "created_at": int(time.time()), "model": model, "status": "completed",
            "output": [{"type": "message", "id": "msg-mock", "status": "completed", "role": "assistant",
                        "content": [{"type": "output_text", "text": self.reply, "annotations": []}]}],
            "usage": {"input_tokens": 10, "output_tokens": 1, "total_tokens": 11},
        }
        if not stream:
            return self._json(200, response)
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        events = [
            ("response.created", {"type": "response.created", "response": dict(response, status="in_progress", output=[])}),
            ("response.output_item.added", {"type": "response.output_item.added", "output_index": 0,
                                            "item": {"type": "message", "id": "msg-mock", "status": "in_progress", "role": "assistant", "content": []}}),
            ("response.output_text.delta", {"type": "response.output_text.delta", "item_id": "msg-mock", "output_index": 0,
                                            "content_index": 0, "delta": self.reply}),
            ("response.output_text.done", {"type": "response.output_text.done", "item_id": "msg-mock", "output_index": 0,
                                           "content_index": 0, "text": self.reply}),
            ("response.output_item.done", {"type": "response.output_item.done", "output_index": 0, "item": response["output"][0]}),
            ("response.completed", {"type": "response.completed", "response": response}),
        ]
        for name, data in events:
            self.wfile.write(f"event: {name}\ndata: {json.dumps(data)}\n\n".encode())
        self.wfile.flush()


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8089)
    parser.add_argument("--api-key", default="test-key")
    parser.add_argument("--reply", default="OK")
    parser.add_argument("--log")
    args = parser.parse_args()
    Handler.api_key, Handler.reply, Handler.log_path = args.api_key, args.reply, args.log
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"mock OpenAI-compatible server on http://{args.host}:{args.port}/v1 (reply={args.reply!r})", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()
        sys.exit(0)


if __name__ == "__main__":
    main()
