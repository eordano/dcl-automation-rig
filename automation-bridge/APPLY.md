# APPLY — patching `chrome-devtool-protocol-unity` durably into a unity-explorer worktree

This is the durable apply procedure for **BUILD STEP 1**: getting the two patched bridge files
(`CDPRequest.cs` + `CDPResponse.cs`, which add `CDPMethod.Custom` and `CDPResult.Json`) into a
unity-explorer build so the in-player automation bridge can use the CDP package as a generic RPC server.

## The problem: PackageCache is NOT durable

`unity-explorer` consumes the package via a **read-only git-URL** entry in
`Explorer/Packages/manifest.json`:

```jsonc
"com.nickkhalow.chrome-devtool-protocol-unity":
  "https://github.com/decentraland/chrome-devtool-protocol-unity.git?path=/Packages",
```

On disk this materializes at:

```
Explorer/Library/PackageCache/com.nickkhalow.chrome-devtool-protocol-unity@<hash>/
```

Unity **refetches and overwrites** that `@<hash>` folder on every package resolve (and the folder
name changes with the locked hash). **Editing files there is non-durable** — they get blown away.

There are two durable options. **Option A (EMBED) is recommended** — it is local, offline-safe,
survives a `Library/` wipe (the patched source lives in version-controlled `Packages/`), and needs no
hosted fork. Option B (FORK) is documented for the CI / shared-team case.

---

## Option A — EMBED the package under `Explorer/Packages/` (RECOMMENDED)

Any folder directly under `Explorer/Packages/` that contains a `package.json` is an **embedded
package**, and an embedded package **wins over the registry/git manifest entry of the same id**.

**Precedent already in this repo:** `com.unity.cloud.ktx` is embedded at
`Explorer/Packages/com.unity.cloud.ktx/` (its `package.json` says `3.4.5`) while `manifest.json`
still lists the registry version `3.6.3`. Unity resolves the **embedded** copy — `packages-lock.json`
records it as `"version": "file:com.unity.cloud.ktx"`, `"source": "embedded"`. We do the same thing
for the CDP package.

### A.1 The exact `manifest.json` edit

Re-point **only** the CDP line. Leave the two REnum lines (`com.decentraland.renum` and
`com.decentraland.renum.sourcegen` — including the `#sourcegen/1.1.4` pin) **untouched**.

```diff
-    "com.nickkhalow.chrome-devtool-protocol-unity": "https://github.com/decentraland/chrome-devtool-protocol-unity.git?path=/Packages",
+    "com.nickkhalow.chrome-devtool-protocol-unity": "file:com.nickkhalow.chrome-devtool-protocol-unity",
```

`file:` references in a manifest are resolved **relative to the `Packages` folder**, so
`file:com.nickkhalow.chrome-devtool-protocol-unity` points at the embedded folder we create next.
(Per the ktx precedent the embedded folder wins even if you leave the git URL, but re-pointing to
`file:` is cleaner and stops Unity from also trying to fetch git.)

The embedded `package.json` still declares its own
`com.decentraland.renum` / `com.decentraland.renum.sourcegen` dependencies, so the REnum pin is
satisfied transitively **and** is still listed explicitly at top-level — nothing about REnum changes.

### A.2 The apply script

Parameterized by the unity-explorer worktree's `Explorer` dir. Idempotent (safe to re-run).

```bash
#!/usr/bin/env bash
set -euo pipefail

# --- params -----------------------------------------------------------------
EXPLORER="${1:?usage: apply.sh /path/to/unity-explorer/Explorer}"     # e.g. ~/unity-explorer/Explorer
PKG_SRC="${PKG_SRC:-$HOME/chrome-devtool-protocol-unity/Packages}"   # checkout of decentraland/chrome-devtool-protocol-unity
PATCH_SRC="${PATCH_SRC:-$HOME/decentraland-automation-rig/automation-bridge/chrome-devtool-protocol-unity}"  # the 2 patched files in this rig
PKG_ID="com.nickkhalow.chrome-devtool-protocol-unity"
# ---------------------------------------------------------------------------

DEST="$EXPLORER/Packages/$PKG_ID"
MANIFEST="$EXPLORER/Packages/manifest.json"

[ -f "$PKG_SRC/package.json" ]       || { echo "no package.json under PKG_SRC=$PKG_SRC"; exit 1; }
[ -f "$PATCH_SRC/CDPRequest.cs" ]    || { echo "missing patch CDPRequest.cs in $PATCH_SRC"; exit 1; }
[ -f "$PATCH_SRC/CDPResponse.cs" ]   || { echo "missing patch CDPResponse.cs in $PATCH_SRC"; exit 1; }
[ -f "$MANIFEST" ]                   || { echo "no manifest at $MANIFEST"; exit 1; }

# 1) copy the WHOLE real package (incl. .meta files -> preserves asmdef + file GUIDs)
rm -rf "$DEST"
mkdir -p "$DEST"
rsync -a --exclude '.DS_Store' "$PKG_SRC"/ "$DEST"/

# 2) overlay the two patched .cs files (CONTENTS only; their .meta stay from step 1 -> GUIDs intact)
cp -f "$PATCH_SRC/CDPRequest.cs"  "$DEST/Bridges/CDPRequest.cs"
cp -f "$PATCH_SRC/CDPResponse.cs" "$DEST/Bridges/CDPResponse.cs"

# 3) re-point ONLY the CDP manifest entry to the embedded folder (REnum lines untouched)
tmp="$(mktemp)"
jq --arg id "$PKG_ID" '.dependencies[$id] = "file:" + $id' "$MANIFEST" > "$tmp"
mv "$tmp" "$MANIFEST"

# 4) drop the stale git PackageCache copy + lock entry so Unity re-resolves cleanly to the embed
rm -rf "$EXPLORER"/Library/PackageCache/"$PKG_ID"@*
# (Unity rewrites packages-lock.json -> source:"embedded", version:"file:..." on next resolve.
#  If you want a deterministic lock immediately, also delete the old lock block for $PKG_ID:)
# jq 'del(.dependencies["'"$PKG_ID"'"])' "$EXPLORER/Packages/packages-lock.json" > "$tmp" && mv "$tmp" "$EXPLORER/Packages/packages-lock.json"

echo "Embedded $PKG_ID at $DEST and re-pointed manifest to file:$PKG_ID"
```

Run it against a worktree (do dev work in a worktree, never in a read-only mirror checkout):

```bash
bash apply.sh ~/unity-explorer/Explorer
```

### A.3 Why each step matters

- **Copy `.meta` files (step 1):** the package's `CDPBridge.asmdef.meta` carries the asmdef GUID that
  unity-explorer assemblies reference; `CDPRequest.cs.meta` / `CDPResponse.cs.meta` carry the file
  GUIDs. Copying them — and overwriting only the `.cs` **contents** in step 2 — keeps every GUID
  stable across the PackageCache→embedded move, so nothing else needs touching.
- **`Libraries/` + `csc.rsp` + `Examples/` come along for free** in the full copy; `csc.rsp` keeps
  `-nullable:enable` (the patched files are nullable-clean).
- **`Bridges/CDPRequest.cs` retains the `Unknown` variant** (the patch keeps `[REnumField(typeof(Unknown))]`).
  This is **required**, not cosmetic: `Explorer/Assets/DCL/WebRequests/ChromeDevtool/ChromeDevToolHandler.cs`
  references `CDPMethod.Kind.Unknown`. Removing the variant would break the existing devtool handler's
  compile. Re-routing `FromRaw`'s default from `Unknown` → `Custom` is behaviorally safe for that
  handler (it still returns `null` for unrecognized methods, just via a different branch).

---

## Option B — FORK the package (CI / shared-team alternative)

1. Fork `decentraland/chrome-devtool-protocol-unity`; on a branch (e.g. `automation-bridge`) commit
   the 2-file patch over `Packages/Bridges/{CDPRequest,CDPResponse}.cs`. Push and note the commit hash.
2. Re-point **only** the CDP line in `manifest.json` to the fork, pinned by commit for reproducibility:

   ```jsonc
   "com.nickkhalow.chrome-devtool-protocol-unity":
     "https://github.com/<you>/chrome-devtool-protocol-unity.git?path=/Packages#<commit-hash>",
   ```
3. Delete `Explorer/Library/PackageCache/com.nickkhalow.chrome-devtool-protocol-unity@*` and let Unity
   re-resolve; it updates the `hash`/`version` in `packages-lock.json`.
4. Leave the REnum lines (`#sourcegen/1.1.4` pin) untouched.

Trade-offs vs EMBED: shareable + CI-friendly, but needs a hosted fork and **network/git access at
resolve time**, and the source still lands in the overwriteable `PackageCache` (durability comes from
the pinned URL, not from on-disk files). EMBED is preferred for local/offline iteration.

---

## What must NOT change (both options)

- The REnum source-gen pin **`com.decentraland.renum.sourcegen` → `...REnum.git#sourcegen/1.1.4`**
  and `com.decentraland.renum` stay exactly as-is. Only the **CDP** package id is re-pointed.
- Only **two** files differ from upstream: `Bridges/CDPRequest.cs`, `Bridges/CDPResponse.cs`. Every
  other package file (asmdef, `csc.rsp`, `Libraries/*.dll`, `Examples/*`, all `.meta`) is verbatim.

## Verify

1. Open/refresh the worktree in Unity (or batch-compile on the Mac host). `packages-lock.json` should now
   show the CDP package as `"source": "embedded"`, `"version": "file:com.nickkhalow.chrome-devtool-protocol-unity"`
   (Option A) or the new git `hash` (Option B).
2. Compiler must accept the generated REnum accessors used by the patch:
   `CDPMethod.FromCustom` / `IsCustom`, `CDPResult.FromJson` / `IsJson`, `IsGetResponseBody` — these
   mirror the existing `FromText`/`IsText`/`FromGetResponseBody` usage in `Bridge.cs`, so they compile
   against the same REnum pin.
3. Existing devtool integration still compiles (`ChromeDevToolHandler.cs` uses `CDPMethod.Kind.Unknown`
   + `IsGetResponseBody`), confirming the patch is backward-compatible.
4. Then proceed to BUILD STEP 2: drop the three `unity-explorer/*.cs` files in and follow `WIRING.md`.

## Revert (Option A)

```bash
EXPLORER=/path/to/unity-explorer/Explorer
rm -rf "$EXPLORER/Packages/com.nickkhalow.chrome-devtool-protocol-unity"
# restore the original git-URL manifest line:
jq '.dependencies["com.nickkhalow.chrome-devtool-protocol-unity"]="https://github.com/decentraland/chrome-devtool-protocol-unity.git?path=/Packages"' \
   "$EXPLORER/Packages/manifest.json" > /tmp/m && mv /tmp/m "$EXPLORER/Packages/manifest.json"
```
