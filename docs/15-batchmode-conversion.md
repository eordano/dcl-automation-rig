# Cross-cutting — headless batchmode conversion loops & the hang watchdog

> **Status:** Mixed. The technique and gotchas below were observed working — the
> abgen asset-bundle reference corpus was generated this way on this host (Unity
> 6000.2.6f2, Win64 + OSX, thousands of bundles). The `convert/` scripts here are
> a generalized, env-overridable distillation of that rig and have NOT been
> re-verified on a clean host.

Most of this rig drives the **Explorer**. This doc covers a different headless
Unity job: running **`decentraland/asset-bundle-converter`** in batchmode to
turn deployed entities (scenes, wearables, emotes) into asset bundles — used to
generate a *reference corpus* (`abgen-rs` parity-tests its pure-Rust bundles
against it). It's a separate Unity project on a separate pinned editor, but the
reliability machinery — the **hang watchdog**, run **serialization**, lockfile
hygiene, resume — applies to *any* long batchmode loop, including repeated
`BuildScript` builds or EditMode test sweeps.

Scripts: [`convert/convert-loop.sh`](../convert/convert-loop.sh) (the loop),
[`convert/unity-batch-watchdog.sh`](../convert/unity-batch-watchdog.sh) (the
backstop). Config lives in `config.sh` under `DCL_ABCONV_*`.

## The one gotcha that will cost you a day: the editor version

The converter pins **Unity 6000.2.6f2** — *not* the Explorer's 6000.4.0f1.

**Never open the converter project with a 6000.4 editor.** It silently
auto-upgrades the working tree (`ProjectVersion.txt`, `manifest.json` gains
packages, the RP/Graphics assets migrate). On 6000.4 the converter's nested
`AssetDatabase.ImportAsset` (`CustomGltfImporter.FixTextureReferences` →
`SaveAndReimport` *during* a glb import) becomes a **fatal** "Calls to
ImportAsset are restricted during asset importing" → `Failed to Import GLTF` →
the editor exits, on *every textured glb*. On 6000.2.6f2 the same call is a
harmless warning. This is not an upstream bug; it's the version bump.

If a project got upgraded by accident: `git checkout --` the migrated files
(`ProjectVersion.txt`, `GraphicsSettings.asset`, `manifest.json`,
`packages-lock.json`, the URP asset + global settings), delete any untracked
`Assets/Editor` IPC symlink, then `rm -rf Library Temp Assets/_Downloaded*` to
force a clean 6000.2 reimport. `config.sh` keeps the converter's editor in its
own `DCL_ABCONV_UNITY_VERSION` var precisely so the Explorer pin can't leak in.

## The batchmode invocation (how it differs from a build)

```
<fhs> <Unity-6000.2.6f2> -batchmode -nographics \
      -projectPath <converter-project> \
      -executeMethod <DCL.ABConverter.SceneClient.…> \
      -baseUrl http://127.0.0.1:5141/contents/ \
      -output <out>/<cid> \
      -buildTarget StandaloneWindows64|StandaloneOSX \
      -logFile <log>
```

Two differences from the Explorer build invocation in
[`00-shared.md`](00-shared.md):

- **No `-quit`.** The converter pumps its own `EditorApplication.update` and
  self-exits when conversion finishes; `-quit` races that pump and truncates
  output. (A normal `BuildScript` build *does* use `-quit`.)
- **The entry point isn't `BuildScript`.** Two methods on `SceneClient`:
  - `ExportSceneToAssetBundles` + `-sceneCid <cid>` — single entity, scenes only.
    This is what `convert-loop.sh` calls per entity.
  - `ExportSceneBatchToAssetBundles` + `-batchQueueFile <one-cid-per-line>` — the
    **current, faster** path: one editor reimport amortized across the whole
    queue. Crucially it is **entity-type-agnostic** — it auto-detects
    wearable/emote/scene from the content-server DTO, so feeding *wearable/emote
    CIDs* through the scene batch produces bundles byte-identical to the proper
    collection-URN path, and only needs the local content server (the
    collection-URN path — `ExportWearablesCollectionToAssetBundles` — needs
    lambdas, i.e. internet). Prefer this for anything beyond a handful of scenes;
    the per-entity loop is the simpler worked example. (Confirmed in converter
    source: `BatchLoopAsync` → `ConvertEntityById` fetches each entity's DTO and
    branches on `entityDTO.type` — there is no scene-only filter on the batch
    path.)

`-buildTarget` is **Unity's own built-in switch** (consumed before any
`-executeMethod` runs to set the active build target), not a converter-parsed
arg — the converter reads `EditorUserBuildSettings.activeBuildTarget`. It accepts
`StandaloneWindows64`/`StandaloneOSX` and the `Win64`/`OSXUniversal` aliases
interchangeably.

## Reliability machinery (the reusable part)

A multi-hour, thousands-of-entities loop will hit license stalls, import
deadlocks, and the occasional crash. Four mechanisms keep it moving unattended:

1. **Resume by output check.** An entity is "done" once it has a
   `<hash>_<platform>` bundle; the loop skips those, so a kill/crash/reboot just
   re-runs the unfinished tail. Make the loop idempotent before you make it fast.

2. **Per-entity `timeout`.** Each conversion is wrapped in `timeout 7200`
   (`ENTITY_TIMEOUT`) so one wedged entity can't hang the whole run forever. This
   is the *last* resort, not the first — see the watchdog.

3. **The hang watchdog** ([`unity-batch-watchdog.sh`](../convert/unity-batch-watchdog.sh)).
   A wedged editor sits at ~0% CPU; waiting out a 2h `timeout` on a run that died
   in minute 3 burns the budget. The watchdog samples Unity's CPU and, after
   `STUCK_SECS` (default 300) below the floor, kills the **whole tree**: the
   `timeout`, the `bwrap` sandbox, the editor, the `Unity.Licensing.Client`, and
   `UnityPackageManager`. Then the loop's resume logic picks up the next entity.
   - **Match the right process.** Find Unity by `Editor/Unity .*-batchmode`, not
     the bare version path — the latter also matches the persistent licensing
     daemon (always ~0% CPU), which you'd kill every tick.
   - **The launched pid isn't the editor.** The FHS wrapper forks the real
     editor; track progress via the log, find the pid by command line.

4. **Lockfile / cache hygiene between runs.** Before each entity, remove
   `Temp/UnityLockfile` (a crashed prior run leaves it held) and
   `Assets/_Downloaded*` together with its `.meta` (a half-written download cache
   triggers reimport churn / meta-mismatch).

## Serializing runs (only one editor at a time)

Two batchmode editors on the same project fight over the project lockfile and a
single local license seat — you can run at most one. To chain a *test* corpus
then a *validation* corpus, wait for the first loop's `DONE` sentinel rather than
launching both:

```bash
# Wait for the test loop, then launch the val loop on the same paths.
until grep -q 'convert-loop: DONE' "$TEST_LOG" 2>/dev/null; do sleep 30; done
sleep 5   # let the license + lockfile clear
MANIFEST=…/validation_entities.json OUTPUT_DIR=…/corpus-val \
    PLATFORM=windows ./convert/convert-loop.sh > "$VAL_LOG" 2>&1
```

(`lib/common.sh`'s `dcl_wait_for` does the same poll if you'd rather not hand-roll
the loop.) The 5s pause matters — relaunching before the previous editor has
released the lockfile fails to acquire it.

## Tolerated failures

A nonzero exit **with bundles written** is normal: the converter returns
`CONVERSION_ERRORS_TOLERATED` (e.g. `rc=12`) for draco-compressed or
missing-dependency glbs but still emits a usable bundle. The loop treats
"rc≠0 **and** zero bundles" as the only real failure; everything else is logged
and the run continues.

## Verifying the generated corpus (downstream, in abgen-rs)

This loop only *produces* the reference bundles. The clean-room Rust converter
(`abgen-rs`) gates against them with its own loops — out of scope for this rig,
but worth knowing where the corpus goes:

- **Byte parity** — `abgen-corpus --from-reference <ref>` then `abgen-verify`
  emits per-bundle `bits_diff` / `ours_bytes` / `ref_bytes`; the metric is the
  byte-identical set, held zero-regression.
- **Render equivalence** — `examples/render_assess <ours> <ref>` grades each pair
  into the G1–G8 taxonomy ([08](08-testing.md)); G1–G3 are render-equivalent.

**Parity-flag trap (cost a wasted round):** when gating against a *fork*-generated
reference like this corpus, do **not** set `--v38-compat` / `--real-textures`
(`ABGEN_V38_COMPAT` / `ABGEN_REAL_TEXTURES`) — their own help text says they
"diverge from fork byte-parity" (they emit production-style bundles), and against
a fork reference they zero out byte-parity on *every* bundle. Those flags belong
to the production AB-CDN comparison, not the fork-reference gate.

## Quick start

```bash
# Point the rig at your converter checkout + content server (or set in config.sh):
export DCL_ABCONV_REPO=~/asset-bundle-converter
export DCL_ABCONV_CONTENT_URL=http://127.0.0.1:5141/contents/

# Babysit + run:
WATCH_WHILE=convert-loop.sh ./convert/unity-batch-watchdog.sh &
MANIFEST=~/corpora/test_entities.json \
OUTPUT_DIR=~/corpus-test PLATFORM=windows \
    ./convert/convert-loop.sh | tee ~/corpus-test/loop.log
```
