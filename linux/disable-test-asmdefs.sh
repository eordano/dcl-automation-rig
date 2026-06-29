#!/usr/bin/env bash
# =============================================================================
# disable-test-asmdefs.sh — work around a Unity 6.0.x headless boot crash.
#
# Unity 6000.4's LifecycleController hits a non-deterministic NRE (~50% of cold
# boots) while topologically sorting assemblies, triggered by test asmdefs gated
# on UNITY_INCLUDE_TESTS in Library/PackageCache. Renaming those package test
# dirs to a trailing-"~" (Unity ignores "~" dirs) makes headless boots reliable.
# Idempotent; restores any in-flight .disabled renames first. Run before launch.
# =============================================================================
set -u

PROJECT="${1:-${DCL_PROJECT_DIR:?set DCL_PROJECT_DIR or pass the Explorer/ path}}"
PKGCACHE="$PROJECT/Library/PackageCache"

SCAN_ROOTS=("$PKGCACHE")

if [ ! -d "$PKGCACHE" ]; then
  echo "[disable-test-asmdefs] no PackageCache yet at $PKGCACHE — skipping"
  exit 0
fi

already_excluded() {
  local p="$1"
  case "$p" in
    *~*/*|*~*) return 0 ;;
  esac
  local IFS=/
  for part in $p; do
    case "$part" in *~) return 0 ;; esac
  done
  return 1
}

declare -A target_dirs
while IFS= read -r asmdef; do
  if grep -q 'UNITY_INCLUDE_TESTS' "$asmdef" 2>/dev/null; then
    target_dirs["$(dirname "$asmdef")"]=1
  fi
done < <(find "${SCAN_ROOTS[@]}" -type f \( -name '*.asmdef' -o -name '*.asmdef.disabled' \) 2>/dev/null)

renamed_dirs=0
restored_asmdefs=0
already_renamed=0
for dir in "${!target_dirs[@]}"; do
  rel="${dir#$PKGCACHE/}"
  if already_excluded "$rel"; then
    already_renamed=$((already_renamed + 1))
    continue
  fi
  parent="$(dirname "$dir")"
  base="$(basename "$dir")"
  new="$parent/${base}~"
  if [ -e "$new" ]; then
    already_renamed=$((already_renamed + 1))
    continue
  fi
  while IFS= read -r d; do
    base_d="${d%.disabled}"
    if [ ! -e "$base_d" ]; then
      mv "$d" "$base_d"
      restored_asmdefs=$((restored_asmdefs + 1))
    fi
  done < <(find "$dir" -type f -name '*.disabled' 2>/dev/null)

  mv "$dir" "$new"
  renamed_dirs=$((renamed_dirs + 1))
done

stray_restored=0
while IFS= read -r d; do
  base_d="${d%.disabled}"
  if [ ! -e "$base_d" ]; then
    mv "$d" "$base_d"
    stray_restored=$((stray_restored + 1))
  fi
done < <(find "$PKGCACHE" -type f -name '*.asmdef.disabled' -o -name '*.asmdef.meta.disabled' 2>/dev/null)

echo "[disable-test-asmdefs] excluded $renamed_dirs test dir(s) this run; restored $restored_asmdefs in-flight asmdef rename(s); $already_renamed already-excluded; $stray_restored stray .disabled cleaned up"
