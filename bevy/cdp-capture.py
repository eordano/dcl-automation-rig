#!/usr/bin/env python3
# =============================================================================
# cdp-capture.py — a tiny vendored Chrome DevTools Protocol client.
#
# This is the CORE browser-driving primitive of the web rig: it drives
# BOTH the wasm engine console (product-tour-web, web-bench-ab) AND the React-HUD
# route-walk (atlas/url-walk.sh). The Unity rig drove a Unity client; there is no
# Unity client here, so this CDP client replaces it.
#
# ADAPTED, not ported: the Unity rig's web-bench-ab.sh called bevy's OWN
# benchmark/wasm/cdp_capture.py, which is ABSENT from this checkout. Rather than
# depend on a file that does not exist here, the rig vendors its own small client.
# It does exactly what we need and no more:
#   - connect to chromium's CDP over the websocket at /json/list (the page target),
#   - enable Runtime + Log + Console domains so every console.* + Log entry streams,
#   - OPTIONALLY inject measurement JS at attach time (DCL_CDP_INJECT, a file or
#     literal) — e.g. the submitFps GPUQueue.submit wrapper, or a HUD readiness
#     probe,
#   - OPTIONALLY evaluate one expression (DCL_CDP_EVAL) — e.g. a window.engine.*
#     console command (the main-thread-reachable action path),
#   - stream console/log text to stdout and EXIT on a sentinel substring (or a
#     deadline). The sentinel is how a capture self-terminates: WASMBENCH_RESULT
#     for the bench build, a scene-load marker for the tour, an auth URL for auth.
#
# >>> HONEST GATING (read this) <<<
# WIRED-NOW: console/log capture works on ANY WebGPU bundle today — that is the
#   functional tour + the submitFps fallback + the HUD route-walk readiness gate.
# GATED: the WASMBENCH_RESULT sentinel only ever appears if the bundle was built
#   with DCL_WASM_BENCHMARK (option_env!-gated in web.rs). A stock bundle emits no
#   such line and the capture just hits its deadline. See BUILD-WASM-BENCHMARK.md.
#
# Pure stdlib EXCEPT the websocket transport: prefer the `websockets` package if
# present (nix-shell -p python3Packages.websockets), else fall back to a minimal
# built-in RFC6455 client over a raw socket so the rig has no hard dependency.
#
# Usage:
#   CDP_PORT=9344 cdp-capture.py <sentinel-substring> [deadline_s]
# Env:
#   CDP_PORT          chromium --remote-debugging-port (default $DCL_WEB_CDP_PORT/9344)
#   DCL_CDP_INJECT    path to a .js file OR literal JS, evaluated once at attach
#   DCL_CDP_EVAL      a single JS expression evaluated after inject (e.g. a console cmd)
#   DCL_CDP_QUIET     "1" = only print sentinel-matching lines (else stream all)
# Exit: 0 if the sentinel was seen, 1 on deadline/no-match, 2 on connect failure.
# =============================================================================
import json
import os
import socket
import sys
import time
import urllib.request

CDP_PORT = int(os.environ.get("CDP_PORT", os.environ.get("DCL_WEB_CDP_PORT", "9344")))
QUIET = os.environ.get("DCL_CDP_QUIET", "0") == "1"


def _inject_source():
    """Resolve DCL_CDP_INJECT to JS source: a readable file path, or a literal."""
    v = os.environ.get("DCL_CDP_INJECT", "")
    if not v:
        return ""
    if os.path.exists(v):
        with open(v, encoding="utf-8", errors="replace") as f:
            return f.read()
    return v


def ws_url(port):
    """Find the page target's webSocketDebuggerUrl via the HTTP /json/list."""
    raw = urllib.request.urlopen(f"http://127.0.0.1:{port}/json/list", timeout=10).read()
    targets = json.loads(raw)
    pages = [t for t in targets if t.get("type") == "page" and t.get("webSocketDebuggerUrl")]
    if not pages:
        # Some chromium builds list the bundle page as type "other" early on.
        pages = [t for t in targets if t.get("webSocketDebuggerUrl")]
    if not pages:
        raise RuntimeError("no CDP page target with a webSocketDebuggerUrl yet")
    return pages[0]["webSocketDebuggerUrl"]


# --- websocket transport -----------------------------------------------------
# Prefer the `websockets` library; fall back to a minimal client so the rig runs
# even without it (stdlib has no websocket client, only the server side via http).
try:
    import websockets  # noqa: F401  (presence check)
    _HAVE_WS_LIB = True
except Exception:
    _HAVE_WS_LIB = False


class MiniWS:
    """A bare-minimum RFC6455 text client — enough to talk CDP. No TLS (CDP is
    always ws:// on loopback), no fragmentation, no extensions. Used only when
    the `websockets` package is unavailable."""

    def __init__(self, url):
        # ws://host:port/devtools/page/<id>
        rest = url.split("://", 1)[1]
        hostport, _, path = rest.partition("/")
        host, _, port = hostport.partition(":")
        self.sock = socket.create_connection((host, int(port or 80)), timeout=15)
        import base64
        import os as _os
        key = base64.b64encode(_os.urandom(16)).decode()
        handshake = (
            f"GET /{path} HTTP/1.1\r\n"
            f"Host: {hostport}\r\n"
            "Upgrade: websocket\r\nConnection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n"
        )
        self.sock.sendall(handshake.encode())
        # Read past the 101 handshake response headers.
        buf = b""
        while b"\r\n\r\n" not in buf:
            buf += self.sock.recv(4096)
        self.buf = buf.split(b"\r\n\r\n", 1)[1]

    def send(self, text):
        data = text.encode()
        # FIN+text opcode; client frames MUST be masked.
        import os as _os
        header = bytearray([0x81])
        n = len(data)
        if n < 126:
            header.append(0x80 | n)
        elif n < 65536:
            header.append(0x80 | 126)
            header += n.to_bytes(2, "big")
        else:
            header.append(0x80 | 127)
            header += n.to_bytes(8, "big")
        mask = _os.urandom(4)
        header += mask
        masked = bytes(b ^ mask[i % 4] for i, b in enumerate(data))
        self.sock.sendall(bytes(header) + masked)

    def _recv_exact(self, n):
        while len(self.buf) < n:
            chunk = self.sock.recv(65536)
            if not chunk:
                raise ConnectionError("ws closed")
            self.buf += chunk
        out, self.buf = self.buf[:n], self.buf[n:]
        return out

    def recv(self, timeout):
        self.sock.settimeout(timeout)
        b0 = self._recv_exact(1)[0]
        b1 = self._recv_exact(1)[0]
        length = b1 & 0x7F
        if length == 126:
            length = int.from_bytes(self._recv_exact(2), "big")
        elif length == 127:
            length = int.from_bytes(self._recv_exact(8), "big")
        payload = self._recv_exact(length) if length else b""
        return payload.decode("utf-8", "replace")

    def close(self):
        try:
            self.sock.close()
        except Exception:
            pass


def run(sentinel, deadline_s):
    # Wait for the CDP endpoint + a page target (chromium may still be starting).
    url = None
    end_connect = time.time() + 40
    while time.time() < end_connect:
        try:
            url = ws_url(CDP_PORT)
            break
        except Exception:
            time.sleep(1)
    if not url:
        print("cdp-capture: no CDP page target (is chromium up with --remote-debugging-port?)",
              file=sys.stderr)
        return 2

    if _HAVE_WS_LIB:
        return _run_wslib(url, sentinel, deadline_s)
    return _run_miniws(url, sentinel, deadline_s)


def _enable_and_inject_msgs():
    """The fixed opening sequence of CDP messages (same for both transports)."""
    msgs = [
        {"id": 1, "method": "Runtime.enable"},
        {"id": 2, "method": "Log.enable"},
        {"id": 3, "method": "Console.enable"},
        {"id": 4, "method": "Page.enable"},
    ]
    inject = _inject_source()
    if inject:
        # addScriptToEvaluateOnNewDocument so it survives the bundle's own reloads.
        msgs.append({"id": 5, "method": "Page.addScriptToEvaluateOnNewDocument",
                     "params": {"source": inject}})
        msgs.append({"id": 6, "method": "Runtime.evaluate", "params": {"expression": inject}})
    ev = os.environ.get("DCL_CDP_EVAL", "")
    if ev:
        msgs.append({"id": 7, "method": "Runtime.evaluate",
                     "params": {"expression": ev, "awaitPromise": True, "returnByValue": True}})
    return msgs


def _texts_from(obj):
    """Pull human-readable text out of a CDP console/log event."""
    out = []
    m = obj.get("method", "")
    p = obj.get("params", {})
    if m == "Runtime.consoleAPICalled":
        parts = []
        for a in p.get("args", []):
            parts.append(str(a.get("value", a.get("description", ""))))
        out.append("[console] " + " ".join(parts))
    elif m == "Log.entryAdded":
        e = p.get("entry", {})
        out.append(f"[log/{e.get('level','')}] {e.get('text','')}")
    elif m == "Runtime.exceptionThrown":
        d = p.get("exceptionDetails", {})
        out.append("[exception] " + (d.get("text", "") + " " +
                   str(d.get("exception", {}).get("description", ""))))
    return out


def _handle_stream(send, recv):
    """Shared loop: send the opening sequence via `send`, then pump `recv`."""
    for msg in _enable_and_inject_msgs():
        send(json.dumps(msg))
    sentinel = _handle_stream.sentinel
    deadline = _handle_stream.deadline
    while time.time() < deadline:
        try:
            raw = recv(max(0.5, deadline - time.time()))
        except socket.timeout:
            continue
        except Exception:
            break
        if not raw:
            continue
        try:
            obj = json.loads(raw)
        except Exception:
            continue
        for line in _texts_from(obj):
            hit = sentinel and sentinel in line
            if hit or not QUIET:
                print(line, flush=True)
            if hit:
                return 0
    return 1


def _run_wslib(url, sentinel, deadline_s):
    import asyncio
    import websockets

    async def go():
        async with websockets.connect(url, max_size=None, open_timeout=15) as ws:
            for msg in _enable_and_inject_msgs():
                await ws.send(json.dumps(msg))
            deadline = time.time() + deadline_s
            while time.time() < deadline:
                try:
                    raw = await asyncio.wait_for(ws.recv(), timeout=max(0.5, deadline - time.time()))
                except asyncio.TimeoutError:
                    continue
                except Exception:
                    break
                try:
                    obj = json.loads(raw)
                except Exception:
                    continue
                for line in _texts_from(obj):
                    hit = sentinel and sentinel in line
                    if hit or not QUIET:
                        print(line, flush=True)
                    if hit:
                        return 0
            return 1

    return asyncio.get_event_loop().run_until_complete(go())


def _run_miniws(url, sentinel, deadline_s):
    ws = MiniWS(url)
    _handle_stream.sentinel = sentinel
    _handle_stream.deadline = time.time() + deadline_s
    try:
        return _handle_stream(ws.send, ws.recv)
    finally:
        ws.close()


def main():
    if len(sys.argv) < 2:
        sys.exit("usage: CDP_PORT=<port> cdp-capture.py <sentinel-substring> [deadline_s]")
    sentinel = sys.argv[1]
    deadline_s = float(sys.argv[2]) if len(sys.argv) > 2 else 240.0
    rc = run(sentinel, deadline_s)
    if rc == 1:
        print(f"cdp-capture: sentinel {sentinel!r} not seen within {deadline_s:.0f}s "
              f"(a stock bundle never emits WASMBENCH_RESULT — see BUILD-WASM-BENCHMARK.md)",
              file=sys.stderr)
    sys.exit(rc)


if __name__ == "__main__":
    main()
