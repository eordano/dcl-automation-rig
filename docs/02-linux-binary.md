# Target 2 — Linux binary (built player)

> **Status:** Partly verified. The Proton launcher is verified only up to wine init (`fsync up and running`); the native build and full 3D render are NOT verified (no GPU available for presentation on this host).

Two ways to run the player on Linux, both onto the same headless display as the
editor target:

- **Native** ([`linux/binary-native.sh`](../linux/binary-native.sh)) — a real
  `StandaloneLinux64` build. Fastest, truest Linux signal.
- **Proton** ([`linux/binary-proton.sh`](../linux/binary-proton.sh)) — the
  Windows `Decentraland.exe` under GE-Proton. Use it when there's no native
  Linux build (e.g. the upstream refclient) or to compare against the shipped
  Windows binary.

## Native

**Prerequisite:** a native Linux player build needs the Linux IL2CPP packages in
`Explorer/Packages/manifest.json` — not every checkout has them:

```json
"com.unity.sdk.linux-x86_64": "1.1.0",
"com.unity.toolchain.linux-x86_64-linux": "1.1.0"
```

If they're absent, `BuildScript.BuildLinux64` can't produce a Linux player
(add them, or build via the Proton path below). Quick check:
`grep linux Explorer/Packages/manifest.json`.

```bash
# build it from the editor first (BuildScript.BuildLinux64[Dev]) — see 00-shared.md
./linux/binary-native.sh -- --realm http://localhost:8000 --position 0,0
```

Expects `build/Linux/decentraland-explorer.x86_64` (override `DCL_LINUX_BIN`).

**The two NixOS ABI shims** (no-ops on a normal distro, required on NixOS):

- `LD_PRELOAD=v8-deepbind.so` (set `DCL_V8_SHIM`) — forces `RTLD_DEEPBIND` so
  ClearScript V8's bundled libstdc++ isn't interposed. Without it you get
  `free(): invalid size` deep in `std::locale`.
- `GLIBC_TUNABLES=glibc.cpu.hwcaps=-AVX512,-AVX512VL` — the Boehm GC over-reads
  past buffers when glibc uses the EVEX `memcpy` variants. You must list
  `AVX512VL` too or glibc still picks an over-reading path.

Graphics API via `DCL_GFX_FLAG` (`-force-vulkan` default, `-force-glcore`
fallback). See [03](03-linux-alternatives.md).

## Proton

```bash
export DCL_WIN_EXE=~/unity-explorer-win/extracted/Decentraland.exe
export DCL_PROTON_PATH=/path/to/GE-Proton    # dir containing ./proton
./linux/binary-proton.sh -- --realm http://localhost:8000
```

Why the specific environment:

- **`UMU_ID=0`** → Proton uses `umu.exe` instead of `steam.exe`, dodging
  lsteamclient's `!status` assertion that fires when Steam isn't running.
- **`WINEDLLOVERRIDES="nvapi,nvapi64=,lsteamclient="`** → disables nvapi (unused,
  can hang) and lsteamclient. Disabling lsteamclient is *also* what makes
  `Application.OpenURL()` route through `winebrowser → xdg-open` (our auth shim)
  instead of returning `EAGAIN` — i.e. it's what makes headless login work.
- **WineBrowser registry key** → Wine looks up the browser here *before* PATH,
  so the launcher injects the absolute path to `auth/xdg-open` once.
- **Runs on the host, not in bwrap** → steam-run needs a real `/sys`. It still
  renders into the rig's nested Xwayland.
- **`DCL_WALK_FORCE_X11=1`** → hide `WAYLAND_DISPLAY` so Wine uses `x11.drv`,
  required if you inject input with xdotool/XTEST (the wayland driver ignores it).

### Known Proton limitations (bucket them as platform, not regressions)

- **WebSocket comms don't hold under Wine** — `ManagedWebSocket` drops `wss://`,
  so the refclient can log in but currently **can't enter a world** under Proton.
  Good for login/version/onboarding parity only.
- **No video decode** — `AVProVideoWinRT` DLL is missing; video content is blank.

## Auth for either path

```bash
. config.sh; . lib/auth.sh
dcl_auth_sign &            # tails the URL log, signs the challenge when it appears
./linux/binary-native.sh   # (or binary-proton.sh)
```
