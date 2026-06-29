# Cross-cutting — AltTester (drive & inspect the running player)

> **Status:** Not verified. Adapted from the project docs; not exercised here.

The editor has ClaudeIPC; the *built player* has **AltTester** — Decentraland's
in-app UI-automation instrumentation. It's the cross-platform way to query the
live object hierarchy and click real UI on Windows/Linux/Mac binaries.

## How it works

- A **non-release build** ships the AltTester prefab; pass `--alttester` at
  launch to activate it. (Release builds strip it — `CloudBuild.PreExport`
  removes the scripting define when `IS_RELEASE_BUILD=true`.)
- The instrumented build connects to **AltTester Desktop** over WebSocket
  (default `localhost:13000`).
- Tests live in a separate repo (`decentraland/explorer-automation`, AltTester
  SDK 2.3.0) and can target a local or remote machine.

```bash
./Decentraland --alttester           # any platform's player binary
```

## Live inspection from Claude (MCP)

AltTester Desktop ships an MCP server that gives Claude Code real-time access to
the running game's hierarchy. Add it to `.claude/settings.json`:

```json
{ "mcpServers": { "alttester": { "command": "/path/to/AltTesterMcp" } } }
```

Binary path by platform (from the AltTester Data Path dir):
- **Linux:** `~/.local/share/AltTesterDesktop/AltTesterMcp`
- **macOS:** `~/Library/Application Support/AltTesterDesktop/AltTesterMcp`
- **Windows:** `%LOCALAPPDATA%\AltTesterDesktop\AltTesterMcp.exe`

Tools exposed include `get_game_state` (scene + on/off-screen object tree),
`find_objects`, `component_property`, `click`, `scroll`, `wait_for_object` — so
you can inspect UI state and drive the player without screenshots/OCR.

## When to use which driver

| Want to… | Use |
|----------|-----|
| Call an editor method / compile / read scene roots | ClaudeIPC ([01](01-linux-editor.md)) — editor only |
| Click real UI in a **built player**, query object tree | AltTester (this doc) |
| Self-driving perf run that quits | autopilot (`--autopilot`, [05](05-windows-binary.md)) |
| Pixel-level UI click on a VM with no agent | QMP/HMP ([06](06-windows-vm.md)) |
