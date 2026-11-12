"""Tiny fake of the Ollama, Gemini and Azure OpenAI REST APIs (wire formats as documented) used to verify
the HTTP provider clients without internet, API keys or a GPU.  Run:  python tools/fake_llm_server.py 8099"""
import json, sys
from http.server import BaseHTTPRequestHandler, HTTPServer

def vec(): return [0.1, 0.2, 0.3, 0.4]

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def _json(self, obj, code=200):
        b = json.dumps(obj).encode(); self.send_response(code); self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(b))); self.end_headers(); self.wfile.write(b)
    def _stream(self, lines, ctype):
        self.send_response(200); self.send_header("content-type", ctype); self.end_headers()
        for l in lines: self.wfile.write((l + "\n").encode()); self.wfile.flush()
    def do_POST(self):
        if self.headers.get("transfer-encoding", "").lower() == "chunked":
            raw = b""
            while True:
                size = int(self.rfile.readline().strip() or b"0", 16)
                if size == 0: self.rfile.readline(); break
                raw += self.rfile.read(size); self.rfile.readline()
        else:
            raw = self.rfile.read(int(self.headers.get("content-length", 0)))
        body = json.loads(raw or b"{}")
        p = self.path
        if p.startswith("/api/chat"):                                   # Ollama
            if body.get("stream"):
                return self._stream([json.dumps({"message": {"content": w}, "done": False}) for w in ["Hello ", "from ", "ollama"]] + [json.dumps({"done": True})], "application/x-ndjson")
            return self._json({"message": {"content": "Hello from ollama"}, "prompt_eval_count": 11, "eval_count": 4, "done": True})
        if p.startswith("/api/embeddings"): return self._json({"embedding": vec()})
        if ":streamGenerateContent" in p:                               # Gemini
            if self.headers.get("x-goog-api-key") != "k": return self._json({"error": "bad key"}, 403)
            return self._stream(["data: " + json.dumps({"candidates": [{"content": {"parts": [{"text": w}]}}]}) for w in ["Hello ", "from ", "gemini"]], "text/event-stream")
        if ":generateContent" in p:
            if self.headers.get("x-goog-api-key") != "k": return self._json({"error": "bad key"}, 403)
            assert "contents" in body, body
            return self._json({"candidates": [{"content": {"parts": [{"text": "Hello from gemini"}]}}], "usageMetadata": {"promptTokenCount": 12, "candidatesTokenCount": 5}})
        if ":embedContent" in p: return self._json({"embedding": {"values": vec()}})
        if "/chat/completions" in p:                                    # Azure OpenAI
            if self.headers.get("api-key") != "k": return self._json({"error": "bad key"}, 401)
            if body.get("stream"):
                return self._stream(["data: " + json.dumps({"choices": [{"delta": {"content": w}}]}) for w in ["Hello ", "from ", "azure"]] + ["data: [DONE]"], "text/event-stream")
            return self._json({"choices": [{"message": {"content": "Hello from azure"}}], "usage": {"prompt_tokens": 13, "completion_tokens": 6}})
        if "/embeddings" in p: return self._json({"data": [{"embedding": vec()}]})
        self._json({"error": "not found " + p}, 404)

HTTPServer(("127.0.0.1", int(sys.argv[1]) if len(sys.argv) > 1 else 8099), H).serve_forever()
