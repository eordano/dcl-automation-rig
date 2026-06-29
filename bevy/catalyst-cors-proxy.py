#!/usr/bin/env python3
# =============================================================================
# catalyst-cors-proxy.py — make a LOCAL catalyst content server usable by a
# cross-origin-isolated (COEP require-corp) wasm bundle.
#
# Why this exists: the bevy-explorer web build is served with
#   Cross-Origin-Embedder-Policy: require-corp
# (mandatory for SharedArrayBuffer = the threaded asset loader). Under that
# policy every *cross-origin* subresource the page fetches must answer with BOTH
#   Access-Control-Allow-Origin: <origin or *>
#   Cross-Origin-Resource-Policy: cross-origin
# or the browser drops the response silently (no console error you'd notice —
# the asset just never arrives, the scene loads half-empty). A plain catalyst
# (your catalyst host, OR a local catalyst content core) sends neither header.
# This thin reverse proxy sits in front of it and adds them, so the COEP-isolated
# wasm bundle can load real content — which makes load-time and first-frame
# numbers deterministic.
#
# PORT of the Unity rig's bevy/catalyst-cors-proxy.py, as-is — stdlib only. The
# upstream is set via DCL_CATALYST_UPSTREAM (no default — set it). This is the
# SAME upstream sites/app uses (sites/app/lib/catalyst/client.ts:20, DEFAULT_BASE).
# Point it at a LOCAL catalyst core for fully-deterministic loads, or a public
# catalyst host.
#
# It also rewrites the host in /about's publicUrls so the client follows links
# back through this proxy (not straight to the un-CORS'd upstream).
#
# NOTE a local content core serves /content + /about only — it has NO /lambdas
# and NO comms. For a *live* scene you still point ?realm= at a live realm
# (lambdas/comms/pointers) and override only content via the client's
# content-server override (the "hybrid realm" — see docs/00 web-cdp-capture).
# This proxy is the content half of that.
#
# Usage:
#   catalyst-cors-proxy.py [listen_port] [upstream_url]
#   DCL_CATALYST_PROXY_PORT=5142 DCL_CATALYST_UPSTREAM=https://your-catalyst-host \
#     ./bevy/catalyst-cors-proxy.py
# =============================================================================
import http.server, socketserver, urllib.request, urllib.error, sys, os

PORT = int(sys.argv[1]) if len(sys.argv) > 1 else int(os.environ.get("DCL_CATALYST_PROXY_PORT", "5142"))
UP = sys.argv[2] if len(sys.argv) > 2 else os.environ.get("DCL_CATALYST_UPSTREAM")
if not UP:
    sys.exit("set DCL_CATALYST_UPSTREAM (or pass upstream_url) — your catalyst host, e.g. a local catalyst core or a public catalyst")
UP = UP.rstrip("/")              # never double the slash when we concat UP + self.path
UP_HOST = UP.split("://", 1)[1]  # e.g. your catalyst host — rewritten out of /about


class H(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"  # keep-alive; needs content-length on every reply

    def _cors(self):
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "*")
        self.send_header("Access-Control-Allow-Methods", "GET,POST,OPTIONS")
        self.send_header("Cross-Origin-Resource-Policy", "cross-origin")

    def do_OPTIONS(self):
        self.send_response(204)
        self._cors()
        self.send_header("content-length", "0")
        self.end_headers()

    def _proxy(self, method):
        body = None
        n = self.headers.get("content-length")
        if n:
            body = self.rfile.read(int(n))
        # Multi-origin escape hatch: /__ext/<host>/<rest> -> https://<host>/<rest>
        # so ANY cross-origin asset the COEP bundle needs (the github System-Scene
        # HUD, worlds-content-server, the AB-CDN) can be routed through here with
        # CORS/CORP added. The default upstream still serves the realm at the root.
        if self.path.startswith("/__ext/"):
            rest = self.path[len("/__ext/"):]
            host, _, tail = rest.partition("/")
            target = "https://" + host + "/" + tail
        else:
            target = UP + self.path
        req = urllib.request.Request(target, data=body, method=method)
        ct_in = self.headers.get("content-type")
        if ct_in:
            req.add_header("content-type", ct_in)
        try:
            r = urllib.request.urlopen(req, timeout=60)
            data, status = r.read(), r.status
            ct = r.headers.get("content-type", "application/octet-stream")
        except urllib.error.HTTPError as e:
            data, status = e.read(), e.code
            ct = e.headers.get("content-type", "application/json")
        except Exception as e:
            self.send_response(502)
            self._cors()
            msg = str(e).encode()
            self.send_header("content-length", str(len(msg)))
            self.end_headers()
            self.wfile.write(msg)
            return
        # /about embeds absolute content/lambdas/bff URLs pointing at the upstream
        # host; rewrite them to this proxy so follow-up fetches stay CORS'd. Rewrite
        # the FULL scheme+host (https://host -> http://127.0.0.1:PORT): the proxy
        # speaks plain HTTP on loopback, so leaving an https:// scheme would make the
        # engine fetch https://127.0.0.1:PORT (TLS) and every content/lambdas fetch
        # would fail. Match both schemes, longest first.
        if self.path.startswith("/about"):
            local = f"http://127.0.0.1:{PORT}".encode()
            for scheme in (b"https://", b"http://"):
                data = data.replace(scheme + UP_HOST.encode(), local)
            # any bare host references left over (no scheme) -> host:port
            data = data.replace(UP_HOST.encode(), f"127.0.0.1:{PORT}".encode())
        self.send_response(status)
        self.send_header("content-type", ct)
        self._cors()
        self.send_header("content-length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self._proxy("GET")

    def do_POST(self):
        self._proxy("POST")

    def log_message(self, *a):
        pass


class S(socketserver.ThreadingMixIn, http.server.HTTPServer):
    daemon_threads = True


print(f"catalyst CORS/CORP proxy :{PORT} -> {UP} (/about rewritten)", flush=True)
S(("127.0.0.1", PORT), H).serve_forever()
