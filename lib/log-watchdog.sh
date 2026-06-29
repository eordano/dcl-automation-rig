#!/usr/bin/env bash
# =============================================================================
# lib/log-watchdog.sh — self-healing Unity editor-log watchdog.
# From the Monitor R&D (2026-06-23): tail -F + grep -E --line-buffered over a
# shared pattern set, reacting to matches (relaunch on license-modal/crash,
# signal "ready" on Completed). The SAME recipe the Claude Code Monitor uses, but
# host-side so it self-heals without an agent in the loop.
#
# WHY tail -F (capital): Unity overwrites/truncates its -logFile on EVERY launch
# (new inode). `tail -f` (descriptor) goes silent after a relaunch; `tail -F`
# (by-name + retry) re-attaches — VERIFIED to fire on post-recreation lines. So a
# crash/relaunch loop keeps being watched. Always use -F here.
#
# SILENCE != SUCCESS: the pattern must cover failure signatures (crash, license,
# stall), not just the happy "Completed" — a crashloop must produce events, not
# look identical to "still loading". A pure silent HANG (no log line) is NOT
# caught by log matching — pair this with a liveness/TTL check if you need that.
#
# Cross-platform: the patterns + reactions are identical everywhere; only the
# tailer differs. Linux/Mac: this script (tail -F; or inotifywait -m / fswatch
# for lower overhead). Windows: PowerShell `Get-Content -Wait -Tail 0` (survives
# recreation) or a .NET FileSystemWatcher, with the same case/patterns.
#
#   bash lib/log-watchdog.sh [logfile]        # run in foreground
#   bash lib/log-watchdog.sh &                # background (editor-up can launch it)
# Customize without editing: export before sourcing/running —
#   DCL_WATCHDOG_RELAUNCH="bash mac/editor-up.sh"   # command to run on license-modal/crash
#   DCL_WATCHDOG_READY=~/.dcl-rig/editor-ready       # touched when in-world (Completed)
#   DCL_WATCHDOG_PATTERNS="<grep -E alternation>"    # override the watch set
# =============================================================================
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]:-$0}")" && pwd)"
# best-effort shared logging; standalone fallback if config/common absent
[ -f "$HERE/../config.sh" ] && . "$HERE/../config.sh" 2>/dev/null || true
[ -f "$HERE/common.sh" ]    && . "$HERE/common.sh"    2>/dev/null || true
command -v dcl_log >/dev/null 2>&1 || dcl_log() { printf '[%s] %s\n' "$(date -Iseconds)" "$*" >&2; }

LOG="${1:-${DCL_MAC_LOG:-$HOME/.dcl-rig/mac-editor.log}}"
READY="${DCL_WATCHDOG_READY:-$HOME/.dcl-rig/editor-ready}"
RELAUNCH="${DCL_WATCHDOG_RELAUNCH:-}"   # empty = report only, no auto-action
PAT="${DCL_WATCHDOG_PATTERNS:-error CS|Shader error|shader.*not (supported|found)|Licensing.*(Connection Lost|unavailable)|Access token is unavailable|Current loading stage:|Fatal error|Aborting|Crash!|SIGSEGV|signal SIG|Native stacktrace|failed to reconnect}"

react() {
  local line="$1"
  case "$line" in
    *"Connection Lost"*|*"Access token is unavailable"*)
      dcl_log "WATCHDOG: license modal — ${RELAUNCH:+relaunching}${RELAUNCH:-report-only}"
      [ -n "$RELAUNCH" ] && eval "$RELAUNCH" || true ;;
    *"Fatal error"*|*SIGSEGV*|*"Native stacktrace"*|*"Crash!"*)
      dcl_log "WATCHDOG: crash — ${RELAUNCH:+relaunching}${RELAUNCH:-report-only}"
      [ -n "$RELAUNCH" ] && eval "$RELAUNCH" || true ;;
    *"Current loading stage: Completed"*)
      dcl_log "WATCHDOG: in-world (Completed)"; mkdir -p "$(dirname "$READY")"; : > "$READY" ;;
    *) dcl_log "WATCHDOG: $line" ;;
  esac
}

dcl_log "watchdog armed on $LOG (tail -F; relaunch=${RELAUNCH:-none})"
# -n0: only NEW lines. line-buffered grep so matches surface immediately (no block buffering).
tail -F -n0 "$LOG" 2>/dev/null | grep -E --line-buffered "$PAT" | while IFS= read -r line; do
  react "$line"
done
