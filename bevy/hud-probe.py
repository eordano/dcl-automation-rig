#!/usr/bin/env python3
# =============================================================================
# hud-probe.py — drive the bevy wasm boot over CDP, report progress honestly,
# hide boot chrome, and grim-capture the GPU compositor (canvas + HUD).
#
# Uses a minimal RFC6455 CDP client (no external deps). Evaluates JS via
# Runtime.evaluate and reads the result by message id. Captures with grim via
# nix-shell against the rig's nested wayland (wayland-1 in RIG_RT).
# =============================================================================
import base64, json, os, socket, subprocess, sys, time, urllib.request

CDP_PORT = int(os.environ["CDP_PORT"])
OUT = os.environ["OUT"]
RIG_RT = os.environ["RIG_RT"]
BOOT_DEADLINE = float(os.environ.get("BOOT_DEADLINE", "220"))


def log(*a):
    print("[probe]", *a, file=sys.stderr, flush=True)


def ws_url():
    raw = urllib.request.urlopen(f"http://127.0.0.1:{CDP_PORT}/json/list", timeout=10).read()
    targets = json.loads(raw)
    pages = [t for t in targets if t.get("type") == "page" and t.get("webSocketDebuggerUrl")]
    # Prefer the bevy page (localhost:8080) over any extension/onboarding page.
    bevy = [t for t in pages if "localhost:8080" in (t.get("url") or "")]
    if bevy:
        pages = bevy
    if not pages:
        pages = [t for t in targets if t.get("webSocketDebuggerUrl")]
    if not pages:
        raise RuntimeError("no CDP page target")
    return pages[0]["webSocketDebuggerUrl"]


class WS:
    def __init__(self, url):
        rest = url.split("://", 1)[1]
        hostport, _, path = rest.partition("/")
        host, _, port = hostport.partition(":")
        self.sock = socket.create_connection((host, int(port or 80)), timeout=15)
        key = base64.b64encode(os.urandom(16)).decode()
        hs = (f"GET /{path} HTTP/1.1\r\nHost: {hostport}\r\n"
              "Upgrade: websocket\r\nConnection: Upgrade\r\n"
              f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n")
        self.sock.sendall(hs.encode())
        buf = b""
        while b"\r\n\r\n" not in buf:
            buf += self.sock.recv(4096)
        self.buf = buf.split(b"\r\n\r\n", 1)[1]
        self._id = 0

    def _frame(self, data):
        h = bytearray([0x81])
        n = len(data)
        if n < 126:
            h.append(0x80 | n)
        elif n < 65536:
            h.append(0x80 | 126); h += n.to_bytes(2, "big")
        else:
            h.append(0x80 | 127); h += n.to_bytes(8, "big")
        m = os.urandom(4); h += m
        return bytes(h) + bytes(b ^ m[i % 4] for i, b in enumerate(data))

    def _send(self, obj):
        try:
            self.sock.sendall(self._frame(json.dumps(obj).encode()))
            return True
        except Exception:
            return False

    def _recv_exact(self, n):
        while len(self.buf) < n:
            c = self.sock.recv(65536)
            if not c:
                raise ConnectionError("ws closed")
            self.buf += c
        out, self.buf = self.buf[:n], self.buf[n:]
        return out

    def _recv(self, timeout):
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

    def eval(self, expr, await_promise=False, timeout=20):
        self._id += 1
        mid = self._id
        if not self._send({"id": mid, "method": "Runtime.evaluate", "params": {
                "expression": expr, "returnByValue": True,
                "awaitPromise": await_promise, "allowUnsafeEvalBlocklistedAPI": True}}):
            return None
        end = time.time() + timeout
        while time.time() < end:
            try:
                raw = self._recv(max(0.2, end - time.time()))
            except socket.timeout:
                continue
            except Exception:
                return None
            try:
                obj = json.loads(raw)
            except Exception:
                continue
            if obj.get("id") == mid:
                r = obj.get("result", {})
                if "exceptionDetails" in r:
                    return {"__exc__": r["exceptionDetails"].get("text", "exception")}
                return r.get("result", {}).get("value")
        return None

    def bring_to_front(self):
        self._id += 1
        self._send({"id": self._id, "method": "Page.bringToFront"})

    def close(self):
        try:
            self.sock.close()
        except Exception:
            pass


def grim(out):
    cmd = (f"XDG_RUNTIME_DIR='{RIG_RT}' WAYLAND_DISPLAY=wayland-1 grim '{out}'")
    r = subprocess.run(["nix-shell", "-p", "grim", "--run", cmd],
                       capture_output=True, text=True)
    if r.returncode != 0:
        log("grim FAILED:", r.stderr.strip()[:300])
    return r.returncode == 0


def canvas_nonblack_ratio(out):
    """Sample the saved PNG: fraction of clearly non-black pixels (crude liveness)."""
    try:
        import struct, zlib
        with open(out, "rb") as f:
            data = f.read()
    except Exception:
        return None
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        return None
    # parse minimal PNG: IHDR + IDAT
    pos = 8
    w = h = bitd = colt = None
    idat = b""
    while pos < len(data):
        ln = int.from_bytes(data[pos:pos+4], "big"); typ = data[pos+4:pos+8]
        chunk = data[pos+8:pos+8+ln]
        if typ == b"IHDR":
            w = int.from_bytes(chunk[0:4], "big"); h = int.from_bytes(chunk[4:8], "big")
            bitd = chunk[8]; colt = chunk[9]
        elif typ == b"IDAT":
            idat += chunk
        elif typ == b"IEND":
            break
        pos += 12 + ln
    if not w or colt not in (2, 6) or bitd != 8:
        return None
    ch = 3 if colt == 2 else 4
    raw = zlib.decompress(idat)
    stride = w * ch
    out_rows = bytearray()
    prev = bytearray(stride)
    p = 0
    def paeth(a, b, c):
        pp = a + b - c
        pa, pb, pc = abs(pp - a), abs(pp - b), abs(pp - c)
        return a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
    for _ in range(h):
        ft = raw[p]; p += 1
        line = bytearray(raw[p:p+stride]); p += stride
        if ft == 1:
            for i in range(ch, stride):
                line[i] = (line[i] + line[i-ch]) & 255
        elif ft == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif ft == 3:
            for i in range(stride):
                a = line[i-ch] if i >= ch else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 255
        elif ft == 4:
            for i in range(stride):
                a = line[i-ch] if i >= ch else 0
                c = prev[i-ch] if i >= ch else 0
                line[i] = (line[i] + paeth(a, prev[i], c)) & 255
        out_rows += line
        prev = line
    # sample on a grid; count pixels whose max channel > 24
    total = nz = 0
    step = max(1, w // 200)
    rstep = max(1, h // 200)
    for y in range(0, h, rstep):
        base = y * stride
        for x in range(0, w, step):
            o = base + x * ch
            r0, g0, b0 = out_rows[o], out_rows[o+1], out_rows[o+2]
            total += 1
            if max(r0, g0, b0) > 24:
                nz += 1
    return (nz / total) if total else None


def main():
    url = ws_url()
    log("attached", url)
    ws = WS(url)
    try:
        deadline = time.time() + BOOT_DEADLINE
        last = ""
        canvas_started_seen = False
        engine_seen = False
        scene_seen = False
        while time.time() < deadline:
            st = ws.eval(r"""
              (function(){
                var c = document.getElementById('mygame-canvas');
                var hud = document.querySelector('.ui3-overlay');
                var s = {
                  canvas: !!c,
                  cw: c?c.width:0, chh: c?c.height:0,
                  started: !!(c && c.started),
                  engine: !!window.engine,
                  coi: !!self.crossOriginIsolated,
                  gpu: !!navigator.gpu,
                  hud: !!hud,
                  hudName: (document.querySelector('.mm__name')||{}).textContent||null
                };
                return JSON.stringify(s);
              })()
            """)
            if isinstance(st, dict) and "__exc__" in st:
                log("eval exc:", st["__exc__"][:160]); time.sleep(3); continue
            try:
                s = json.loads(st) if st else {}
            except Exception:
                s = {}
            line = (f"canvas={s.get('canvas')} {s.get('cw')}x{s.get('chh')} "
                    f"started={s.get('started')} engine={s.get('engine')} "
                    f"coi={s.get('coi')} gpu={s.get('gpu')} hud={s.get('hud')} "
                    f"hudName={s.get('hudName')}")
            if line != last:
                log(line); last = line
            if s.get("started"):
                canvas_started_seen = True
            if s.get("engine"):
                engine_seen = True
            # scene loaded? ask the engine if available
            if s.get("engine") and not scene_seen:
                lv = ws.eval(
                    "(window.engine&&window.engine.liveScenes)?window.engine.liveScenes():''",
                    await_promise=True, timeout=8)
                if lv and isinstance(lv, str) and lv.strip() and "no scene" not in lv.lower():
                    scene_seen = True
                    log("live_scenes:", lv.replace("\n", " | ")[:300])
            time.sleep(3)
            # Once the canvas has STARTED, give the engine a fixed render window to
            # download scene content + compile shaders + draw frames, then capture.
            if canvas_started_seen:
                rw = float(os.environ.get("RENDER_WINDOW", "75"))
                log(f"canvas started -> render window {rw:.0f}s (waiting for scene geometry)")
                rwend = time.time() + rw
                tmp_probe = OUT.replace(".png", ".probe.png")
                while time.time() < rwend:
                    time.sleep(10)
                    lv = ws.eval(
                        "(window.engine&&window.engine.liveScenes)?window.engine.liveScenes():''",
                        await_promise=True, timeout=8)
                    if lv and isinstance(lv, str) and lv.strip() and "no scene" not in lv.lower():
                        if not scene_seen:
                            scene_seen = True
                        sc = lv.replace("\n", " | ")[:200]
                    else:
                        sc = "(none yet)"
                    # quick non-black probe so we can stop early once it's lit
                    grim(tmp_probe)
                    pr = canvas_nonblack_ratio(tmp_probe)
                    log(f"render-wait t-{int(rwend-time.time())}s scene={sc} nonblack={pr}")
                    # Don't bail at the first non-black frame (that's just the base
                    # terrain). Let scene GLBs stream + shaders compile for the full
                    # window so buildings/props are visible; only stop early once the
                    # scene is reported AND the canvas is well-lit AND we've given it
                    # a real chunk of time.
                    elapsed = rw - (rwend - time.time())
                    if scene_seen and pr is not None and pr > 0.35 and elapsed > rw * 0.7:
                        log("scene loaded + canvas well-lit + settled -> capture now")
                        break
                try:
                    os.remove(tmp_probe)
                except Exception:
                    pass
                break

        # Make sure the bevy page is the focused/front window for grim.
        ws.bring_to_front()
        # Hide boot chrome + dev overlay, dark backdrop behind transparent HUD.
        ws.eval("""
          for (const id of ['header','loading-logo','bevy-badge','shader-compiling',
                            'loading-container','dev-debug-overlay']) {
            const el = document.getElementById(id); if (el) el.style.display='none';
          }
        """)
        time.sleep(1.5)

        # Capture a couple frames a few seconds apart; keep the best.
        shots = []
        for i in range(3):
            tmp = OUT if i == 0 else OUT.replace(".png", f".f{i}.png")
            if grim(tmp):
                ratio = canvas_nonblack_ratio(tmp)
                log(f"frame {i}: {tmp} nonblack_ratio={ratio}")
                shots.append((ratio if ratio is not None else -1, tmp))
            time.sleep(4)

        # Pick the frame with the highest non-black ratio as the final OUT.
        if shots:
            shots.sort(reverse=True)
            best_ratio, best = shots[0]
            if best != OUT:
                import shutil
                shutil.copyfile(best, OUT)
            log(f"FINAL {OUT} best_nonblack_ratio={best_ratio}")
            # clean extra frames
            for _, f in shots:
                if f != OUT:
                    try: os.remove(f)
                    except Exception: pass
            print(f"RESULT started={canvas_started_seen} engine={engine_seen} "
                  f"scene={scene_seen} nonblack_ratio={best_ratio}", flush=True)
        else:
            log("NO frames captured (grim failed)")
            print("RESULT no-capture", flush=True)
    finally:
        ws.close()


if __name__ == "__main__":
    main()
