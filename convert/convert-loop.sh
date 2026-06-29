#!/usr/bin/env bash
# =============================================================================
# convert/convert-loop.sh — drive decentraland/asset-bundle-converter in
# headless batchmode, one entity per Unity invocation, to generate an
# asset-bundle reference corpus. See docs/15-batchmode-conversion.md.
#
# This is NOT the Explorer and NOT unity/BuildScript.cs — it's a second Unity
# project on its OWN pinned editor (6000.2.6f2; see config.sh / docs/15).
#
# Required env:
#   MANIFEST     JSON {"entities":[{"entity_id","entity_type","file_count"},...]}
#   OUTPUT_DIR   where per-entity bundle dirs are written ($OUTPUT_DIR/<cid>/)
# Optional env:
#   PLATFORM     windows | mac           (default: windows)
#   LOG_DIR      per-entity Unity logs    (default: $OUTPUT_DIR/.logs)
#   ENTITY_TIMEOUT  per-entity hard cap, seconds (default: 7200)
#   DCL_ABCONV_*  project/editor/content-url — resolved from config.sh
# =============================================================================
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
. "$HERE/../config.sh"
. "$HERE/../lib/common.sh"

MANIFEST="${MANIFEST:?MANIFEST required}"
OUTPUT_DIR="${OUTPUT_DIR:?OUTPUT_DIR required}"
PLATFORM="${PLATFORM:-windows}"
LOG_DIR="${LOG_DIR:-$OUTPUT_DIR/.logs}"
ENTITY_TIMEOUT="${ENTITY_TIMEOUT:-7200}"

# -buildTarget: Unity accepts the Standalone* names and the Win64/OSXUniversal
# aliases interchangeably; we use the canonical Standalone* forms.
case "$PLATFORM" in
    windows) BUILD_TARGET=StandaloneWindows64 ;;
    mac)     BUILD_TARGET=StandaloneOSX ;;
    *) dcl_die "unknown PLATFORM=$PLATFORM (want: windows|mac)" ;;
esac

PROJECT="$DCL_ABCONV_PROJECT_DIR"
[ -d "$PROJECT" ] || dcl_die "converter project not found: $PROJECT (set DCL_ABCONV_REPO)"
# The converter pins 6000.2.6f2 — pass the version so dcl_unity_bin resolves THAT
# editor, not the Explorer's 6000.4 default.
UNITY="${DCL_ABCONV_UNITY_BIN:-$(dcl_unity_bin "$DCL_ABCONV_UNITY_VERSION")}"
[ -x "$UNITY" ] || dcl_die "converter editor not executable: $UNITY (want $DCL_ABCONV_UNITY_VERSION — NOT 6000.4)"

mkdir -p "$OUTPUT_DIR" "$LOG_DIR"

N=$(python3 -c "import json,sys;print(len(json.load(open('$MANIFEST'))['entities']))")
dcl_log "convert-loop: N=$N platform=$PLATFORM target=$BUILD_TARGET out=$OUTPUT_DIR"
dcl_log "convert-loop: editor=$UNITY  fhs=${DCL_FHS_WRAP:-<none>}"
idx=0
TOTAL_START=$(date +%s)

python3 -c "
import json
for e in json.load(open('$MANIFEST'))['entities']:
    print(f\"{e['entity_id']}\t{e['entity_type']}\t{e.get('file_count','?')}\")
" | while IFS=$'\t' read -r entity_id entity_type fc; do
    idx=$((idx + 1))
    ent_out="$OUTPUT_DIR/$entity_id"
    log="$LOG_DIR/${entity_id}.convert.log"
    stdout_log="$LOG_DIR/${entity_id}.stdout.log"

    # Resume: a finished entity has at least one <hash>_<platform> bundle. Skip it
    # so the loop is restartable after a watchdog kill / crash / reboot.
    if ls "$ent_out"/*_"$PLATFORM" >/dev/null 2>&1; then
        dcl_log "[$idx/$N] skip $entity_type/$entity_id (already built)"
        continue
    fi

    # Per-entity method dispatch. This per-entity path only does scenes (the CLI
    # entry point takes a single -sceneCid). For wearables/emotes — and for batch
    # throughput — use the batch-queue method instead (see docs/15); fed entity
    # CIDs, it is entity-type-agnostic and byte-matches the collection path.
    case "$entity_type" in
        scene)
            METHOD=DCL.ABConverter.SceneClient.ExportSceneToAssetBundles
            ARG="-sceneCid $entity_id"
            ;;
        wearable|emote|outfits|profile)
            dcl_log "[$idx/$N] skip $entity_type/$entity_id (per-entity scene CLI can't take it — use the batch-queue method, docs/15)"
            continue
            ;;
        *)
            dcl_log "[$idx/$N] ? unknown entity_type=$entity_type, skipping"
            continue
            ;;
    esac

    mkdir -p "$ent_out"
    # Clear stale per-project state that wedges a fresh run: the editor lockfile
    # (a prior crashed/killed run leaves it held) and the converter's download
    # cache + its .meta (a half-written cache triggers reimport churn / meta
    # mismatch). Remove the cache and its sibling .meta together.
    rm -f "$PROJECT/Temp/UnityLockfile" 2>/dev/null
    rm -rf "$PROJECT"/Assets/_Downloaded* 2>/dev/null

    T0=$(date +%s)
    # NOTE: no -quit. The converter self-exits via its own EditorApplication.update
    # pump when the conversion completes; -quit would race it and truncate output.
    # The per-entity `timeout` is the backstop for a hang the converter never ends;
    # convert/unity-batch-watchdog.sh is the faster backstop for a *stalled* run.
    timeout "$ENTITY_TIMEOUT" ${DCL_FHS_WRAP:+"$DCL_FHS_WRAP"} "$UNITY" \
        -batchmode -nographics \
        -projectPath "$PROJECT" \
        -executeMethod "$METHOD" \
        $ARG \
        -baseUrl "$DCL_ABCONV_CONTENT_URL" \
        -output "$ent_out" \
        -buildTarget "$BUILD_TARGET" \
        -logFile "$log" \
        > "$stdout_log" 2>&1
    RC=$?
    T=$(($(date +%s) - T0))
    nbnd=$(ls "$ent_out"/*_"$PLATFORM" 2>/dev/null | wc -l)

    # A nonzero rc with bundles emitted is TOLERATED: the converter returns
    # CONVERSION_ERRORS_TOLERATED (e.g. rc=12) for draco / missing-dep glbs but
    # still writes a usable bundle. Only "rc!=0 AND zero bundles" is a real fail.
    if [ "$RC" = 0 ] && [ "$nbnd" -gt 0 ]; then
        dcl_log "[$idx/$N] ok  $entity_type/$entity_id  ${T}s  bundles=$nbnd"
    elif [ "$nbnd" -gt 0 ]; then
        dcl_log "[$idx/$N] ERR $entity_type/$entity_id  ${T}s  rc=$RC  bundles=$nbnd  (tolerated)"
    else
        dcl_log "[$idx/$N] ERR $entity_type/$entity_id  ${T}s  rc=$RC  bundles=$nbnd"
    fi
done
TOTAL_T=$(($(date +%s) - TOTAL_START))
dcl_log "convert-loop: DONE in ${TOTAL_T}s"
