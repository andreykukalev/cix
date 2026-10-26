#!/usr/bin/env python3
"""Tests for the agents' Azure job mode (agent-job.sh, ADR-0013), without Azure or Claude.

One local HTTP server stands in for the managed identity endpoint, the Storage queues and the engine; a fake `claude` on
PATH records how it was started. Each scenario runs run-trader.sh or run-analyst.sh once with CIX_JOB_MODE=1 and checks
the calls it made. Run from the repository root: python3 tools/trader/test-agent-job.py
"""

import base64
import json
import os
import subprocess
import sys
import tempfile
import threading
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
IDENTITY_HEADER = "identity-" + os.urandom(4).hex()
ENGINE_TOKEN = "eyJ" + ".".join(os.urandom(8).hex() for _ in range(3))   # JWT-shaped, built at run time: not a secret
STORAGE_TOKEN = "storage-token-" + os.urandom(8).hex()
AUDIENCE = "api://cix-engine-test"


class World:
    """What the fake services hold and what they were asked."""

    def __init__(self):
        self.messages = {}       # queue name -> list of (id, dequeue_count, text)
        self.requests = []       # (method, path, body)
        self.claim_answers = []  # (status, body) per claim call, the last one repeated
        self.outcome_status = 204
        self.lock = threading.Lock()


WORLD = World()


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def _send(self, status, body=b"", content_type="application/json"):
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def _bearer(self):
        return self.headers.get("Authorization", "").removeprefix("Bearer ")

    def _record(self):
        length = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(length).decode() if length else ""
        with WORLD.lock:
            WORLD.requests.append((self.command, self.path, body))
        return body

    def do_GET(self):
        self._record()
        url = urllib.parse.urlparse(self.path)
        query = urllib.parse.parse_qs(url.query)
        if url.path == "/msi/token":
            if self.headers.get("X-IDENTITY-HEADER") != IDENTITY_HEADER:
                return self._send(401)
            token = STORAGE_TOKEN if query["resource"][0] == "https://storage.azure.com/" else ENGINE_TOKEN
            return self._send(200, json.dumps({"access_token": token}).encode())
        if url.path == "/healthz":
            return self._send(200, b'{"status":"ready"}')
        if url.path.startswith("/queue/") and url.path.endswith("/messages"):
            if self._bearer() != STORAGE_TOKEN or not self.headers.get("x-ms-version"):
                return self._send(403)
            queue = url.path.split("/")[2]
            with WORLD.lock:
                waiting = WORLD.messages.get(queue, [])
                if not waiting:
                    return self._send(200, b"<?xml version=\"1.0\" encoding=\"utf-8\"?><QueueMessagesList />", "application/xml")
                msg_id, count, text = waiting[0]
                waiting[0] = (msg_id, count + 1, text)
            xml = (
                '<?xml version="1.0" encoding="utf-8"?><QueueMessagesList><QueueMessage>'
                f"<MessageId>{msg_id}</MessageId><InsertionTime>Mon, 26 Oct 2026 16:00:00 GMT</InsertionTime>"
                f"<PopReceipt>AgAAAAMAAAAAAAAA+/rc==</PopReceipt><DequeueCount>{count + 1}</DequeueCount>"
                f"<MessageText>{text}</MessageText></QueueMessage></QueueMessagesList>"
            )
            return self._send(200, xml.encode(), "application/xml")
        self._send(404)

    def do_DELETE(self):
        self._record()
        url = urllib.parse.urlparse(self.path)
        parts = url.path.split("/")
        if self._bearer() != STORAGE_TOKEN or urllib.parse.parse_qs(url.query).get("popreceipt") != ["AgAAAAMAAAAAAAAA+/rc=="]:
            return self._send(403)
        with WORLD.lock:
            WORLD.messages[parts[2]] = [m for m in WORLD.messages.get(parts[2], []) if m[0] != parts[4]]
        self._send(204)

    def do_POST(self):
        body = self._record()
        url = urllib.parse.urlparse(self.path)
        if url.path.startswith("/queue/"):
            if self._bearer() != STORAGE_TOKEN:
                return self._send(403)
            return self._send(201, b"", "application/xml")
        if self._bearer() != ENGINE_TOKEN or self.headers.get("Content-Type") != "application/json":
            return self._send(401)
        json.loads(body)  # the engine takes JSON only
        if url.path.endswith("/claim"):
            with WORLD.lock:
                status, answer = WORLD.claim_answers.pop(0) if len(WORLD.claim_answers) > 1 else WORLD.claim_answers[0]
            return self._send(status, json.dumps(answer).encode())
        if url.path.endswith("/outcome"):
            return self._send(WORLD.outcome_status)
        self._send(404)


FAKE_CLAUDE = """#!/usr/bin/env bash
{
  printf 'run=%s\\n' "$CIX_RUN_ID"
  [[ "$CIX_ENGINE_TOKEN" == "$EXPECTED_ENGINE_TOKEN" ]] && echo token=ok
  printf 'arg=%s\\n' "$@"
} > "$FAKE_CLAUDE_LOG"
case "${FAKE_CLAUDE_MODE:-done}" in
  done) echo '{"is_error":false,"result":"cycle finished","total_cost_usd":0.02,"num_turns":4,"modelUsage":{"claude-sonnet":{}}}' ;;
  limit) echo '{"is_error":true,"result":"Claude AI usage limit reached","total_cost_usd":0,"num_turns":0}' ;;
  error) echo 'not json'; echo "boom: bearer $CIX_ENGINE_TOKEN, key $(cat "$CLAUDE_TOKEN_FILE")" >&2; exit 2 ;;
esac
"""


def message(run_id, job, purpose="heartbeat"):
    return base64.b64encode(json.dumps({"runId": run_id, "job": job, "purpose": purpose}).encode()).decode()


def run(script, job, port, tmp, claude_mode="done"):
    log = os.path.join(tmp, "claude.log")
    if os.path.exists(log):
        os.remove(log)
    token_file = os.path.join(tmp, "claude_token")
    with open(token_file, "w") as f:
        f.write("sk-" + "ant-" + "oat-" + "x" * 12)
    env = {
        "PATH": tmp + os.pathsep + os.environ["PATH"],
        "HOME": tmp,
        "CLAUDE_PROJECT_DIR": ROOT,
        "CLAUDE_TOKEN_FILE": token_file,
        "CLAUDE_TOKEN_EXPIRES": "2099-01-01",
        "CIX_JOB_MODE": "1",
        "CIX_JOB": job,
        "CIX_ENGINE_URL": f"http://127.0.0.1:{port}",
        "CIX_ENGINE_AUDIENCE": AUDIENCE,
        "CIX_QUEUE_URL": f"http://127.0.0.1:{port}/queue/runs-{'requests' if job == 'trader-requests' else job}",
        "CIX_POISON_QUEUE_URL": f"http://127.0.0.1:{port}/queue/runs-poison",
        "CIX_JOB_RETRY_SECONDS": "0",
        "IDENTITY_ENDPOINT": f"http://127.0.0.1:{port}/msi/token",
        "IDENTITY_HEADER": IDENTITY_HEADER,
        "CONTAINER_APP_JOB_EXECUTION_NAME": "cix-trader-x7k2p",
        "EXPECTED_ENGINE_TOKEN": ENGINE_TOKEN,
        "FAKE_CLAUDE_LOG": log,
        "FAKE_CLAUDE_MODE": claude_mode,
    }
    result = subprocess.run(["bash", os.path.join(ROOT, "tools", "trader", script)], env=env, capture_output=True, text=True, timeout=120)
    output = result.stdout + result.stderr
    assert ENGINE_TOKEN not in output and STORAGE_TOKEN not in output and IDENTITY_HEADER not in output, "a token was printed"
    claude = open(log).read() if os.path.exists(log) else None
    return result.returncode, output, claude


def calls(method, fragment):
    return [r for r in WORLD.requests if r[0] == method and fragment in r[1]]


def reset(queue=None, messages=(), claim=((200, {"wanted": True, "purpose": "heartbeat"}),)):
    WORLD.requests.clear()
    WORLD.messages = {queue: list(messages)} if queue else {}
    WORLD.claim_answers = list(claim)
    WORLD.outcome_status = 204


def main():
    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    port = server.server_address[1]
    threading.Thread(target=server.serve_forever, daemon=True).start()
    failures = []

    def scenario(name, check):
        try:
            check()
            print(f"ok   {name}")
        except AssertionError as e:
            failures.append(name)
            print(f"FAIL {name}: {e}")
            if os.environ.get("GITHUB_ACTIONS") == "true":   # an annotation: CI's logs can't be read from sessions
                detail = str(e).replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")[:2000]
                print(f"::error file=tools/trader/test-agent-job.py::{name}: {detail}")

    with tempfile.TemporaryDirectory() as tmp:
        fake = os.path.join(tmp, "claude")
        with open(fake, "w") as f:
            f.write(FAKE_CLAUDE)
        os.chmod(fake, 0o755)

        def trader_run_done():
            reset("runs-trader", [("m1", 0, message("run-20261026-160000-abc123", "trader"))])
            code, out, claude = run("run-trader.sh", "trader", port, tmp)
            assert code == 0, out
            assert calls("DELETE", "/queue/runs-trader/messages/m1"), "message not deleted"
            claim = calls("POST", "/runs/run-20261026-160000-abc123/claim")
            assert claim and json.loads(claim[0][2]) == {"claimant": "cix-trader-x7k2p"}, claim
            assert claude and "run=run-20261026-160000-abc123" in claude and "token=ok" in claude, claude
            assert "arg=Run one trading cycle of the active strategy now." in claude, claude
            assert any(line.startswith("arg=") and line.endswith("tools/trader/mcp-azure.json") for line in claude.splitlines()), claude
            outcome = calls("POST", "/runs/run-20261026-160000-abc123/outcome")
            assert [json.loads(o[2]) for o in outcome] == [{"outcome": "done"}], outcome
            # The claim comes after the delete, the run after the claim.
            order = [r[0] + " " + r[1].split("?")[0] for r in WORLD.requests if "/msi/" not in r[1] and "/healthz" not in r[1]]
            assert order.index("DELETE /queue/runs-trader/messages/m1") < order.index("POST /runs/run-20261026-160000-abc123/claim"), order

        def requests_run_cannot_trade():
            reset("runs-requests", [("m2", 0, message("run-20261026-160100-def456", "trader-requests", "requests"))])
            code, out, claude = run("run-trader.sh", "trader-requests", port, tmp)
            assert code == 0, out
            tools = next(line for line in claude.splitlines() if "mcp__cix__get_manual_requests" in line and "answer_manual_request" in line)
            assert "place_order" not in tools, tools
            assert "Do not run the regular trading cycle" in claude, claude

        def analyst_run():
            reset("runs-analyst", [("m3", 0, message("analyst-20261026-160200-0a1b2c", "analyst", "analyst"))])
            code, out, claude = run("run-analyst.sh", "analyst", port, tmp)
            assert code == 0, out
            assert "run=analyst-20261026-160200-0a1b2c" in claude and "arg=Assess the market now and record your view." in claude, claude
            assert [json.loads(o[2]) for o in calls("POST", "/outcome")] == [{"outcome": "done"}]

        def not_wanted():
            reset("runs-trader", [("m4", 0, message("run-20261026-160300-aaaaaa", "trader"))], claim=((200, {"wanted": False, "purpose": None}),))
            code, out, claude = run("run-trader.sh", "trader", port, tmp)
            assert code == 0 and claude is None and not calls("POST", "/outcome"), out

        def busy_engine_is_retried():
            reset("runs-trader", [("m5", 0, message("run-20261026-160400-bbbbbb", "trader"))],
                  claim=((503, {}), (503, {}), (200, {"wanted": True, "purpose": "heartbeat"})))
            code, out, claude = run("run-trader.sh", "trader", port, tmp)
            assert code == 0 and claude is not None, out
            claims = calls("POST", "/claim")
            assert len(claims) == 3 and len({c[2] for c in claims}) == 1, claims  # the same claimant each time

        def engine_never_answers():
            reset("runs-trader", [("m6", 0, message("run-20261026-160500-cccccc", "trader"))], claim=((503, {}),))
            code, out, claude = run("run-trader.sh", "trader", port, tmp)
            assert code != 0 and claude is None and len(calls("POST", "/claim")) == 6, out

        def usage_limit_is_not_run():
            reset("runs-trader", [("m7", 0, message("run-20261026-160600-dddddd", "trader"))])
            code, out, _ = run("run-trader.sh", "trader", port, tmp, claude_mode="limit")
            assert code == 0, out
            assert [json.loads(o[2]) for o in calls("POST", "/outcome")] == [{"outcome": "not-run"}]

        def failed_run_is_reported():
            reset("runs-trader", [("m8", 0, message("run-20261026-160700-eeeeee", "trader"))])
            code, out, _ = run("run-trader.sh", "trader", port, tmp, claude_mode="error")
            assert code != 0, out
            assert [json.loads(o[2]) for o in calls("POST", "/outcome")] == [{"outcome": "failed"}]
            # The job's files go with its container: the end of stderr reaches its log, with tokens and keys masked.
            assert "stderr (last 30 lines)" in out and "| boom: bearer <token>, key sk-ant-***" in out, out
            assert "x" * 12 not in out, out

        def poison_message():
            reset("runs-trader", [("m9", 2, message("run-20261026-160800-ffffff", "trader"))])
            code, out, claude = run("run-trader.sh", "trader", port, tmp)
            assert code == 0 and claude is None, out
            assert calls("POST", "/queue/runs-poison/messages") and calls("DELETE", "/queue/runs-trader/messages/m9")
            assert not calls("POST", "/claim")

        def empty_queue():
            reset("runs-trader")
            code, out, claude = run("run-trader.sh", "trader", port, tmp)
            assert code == 0 and claude is None and not calls("POST", "/claim"), out

        def other_jobs_message_is_dropped():
            reset("runs-trader", [("m10", 0, message("run-20261026-160900-123456", "analyst"))])
            code, out, claude = run("run-trader.sh", "trader", port, tmp)
            assert code == 0 and claude is None and not calls("POST", "/claim"), out
            assert calls("DELETE", "/queue/runs-trader/messages/m10")

        def bad_run_id_is_dropped():
            reset("runs-trader", [("m11", 0, message("../../etc/passwd", "trader"))])
            code, out, claude = run("run-trader.sh", "trader", port, tmp)
            assert code == 0 and claude is None and not calls("POST", "/claim"), out

        def wrong_job_setting():
            reset("runs-trader")
            code, out, _ = run("run-analyst.sh", "trader", port, tmp)
            assert code != 0 and "CIX_JOB must be" in out and not WORLD.requests, out

        for name, check in list(locals().items()):
            if callable(check) and name not in ("scenario", "check"):
                scenario(name, check)

    server.shutdown()
    if failures:
        print(f"{len(failures)} failed")
        sys.exit(1)
    print("all passed")


if __name__ == "__main__":
    main()
