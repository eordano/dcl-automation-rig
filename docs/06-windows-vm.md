# Target 6 — Windows VM + {editor, binary}

> **Status:** Partly verified. VM launch + audio/mic and the `vmssh`/`vmkeys` round-trips are verified; driving the editor/binary inside the VM and `vmclick` (QMP mouse) are NOT verified.

Drive a Windows guest **entirely from the Linux host** — no RDP, no in-guest
agent, no human. This is how you get the Windows editor (target 4) and Windows
binary (target 5) on a Linux machine, fully scripted.

## The control stack

The guest is a QEMU/KVM Windows 11 VM. Everything goes through three channels;
pick the right one for the job:

| Channel | Socket / port | Use it for |
|---------|---------------|------------|
| **SSH** ([`vmssh.sh`](../vm/vmssh.sh)) | host:2222 → guest:22 | files, cache nuke, *registering* Scheduled Tasks. **Lands in session 0 — no graphics.** |
| **QMP** ([`vmclick.sh`](../vm/vmclick.sh)) | `qemu-qmp.sock` | mouse: absolute-tablet clicks into the emulated hardware |
| **HMP** ([`vmkeys.sh`](../vm/vmkeys.sh)) | `qemu-mon.sock` | screenshots (`screendump`) and keystrokes (`sendkey`) |

Point the scripts at your VM with `DCL_VM_DIR` (the dir holding the sockets) and
`DCL_VM_KEY` (the SSH key). Defaults assume a `dcl` user.

## The cardinal rule: session 0 vs session 1

An SSH command runs in **session 0**, which has **no graphics device**. Unity
Play mode, the in-editor harness, and the player all need a real desktop
session. So:

- Use SSH to *prepare* (copy files, nuke caches, register a Scheduled Task).
- Use a **Scheduled Task with `LogonType=Interactive`** to *launch* anything
  that renders — it runs in session 1. This is exactly what
  `windows/reset-and-launch-editor.ps1` and `windows/launch-binary.ps1` do.

## Pushing PowerShell into the guest

Pipe a script over stdin to the guest's PowerShell:

```bash
vm/vmssh.sh 'powershell -NoProfile -Command -' < windows/reset-and-launch-editor.ps1
# read a result back:
vm/vmssh.sh 'powershell -NoProfile -Command "Get-Content C:\Users\dcl\harness-report.json -Raw"'
```

Type *complex* commands from files, not as shell args — quoting through SSH +
PowerShell is a minefield.

## GUI driving (when there's no Scheduled-Task path)

```bash
DCL_VM_DIR=~/vms/win11 vm/vmkeys.sh shot before     # /tmp/before.png
DCL_VM_DIR=~/vms/win11 vm/vmclick.sh 675 61         # click the editor Play button
DCL_VM_DIR=~/vms/win11 vm/vmkeys.sh shot after
```

- **Coords are framebuffer pixels** (default 1280×800), mapped to QMP's
  0..32767 grid — not physical screen pixels. If the guest resolution changes,
  set `DCL_VM_FB_W`/`DCL_VM_FB_H` or your clicks land in the wrong place.
- **Confirm a click landed** by pixel-diffing the before/after screenshots
  (`magick compare -metric AE`); a real UI change shows a large diff. The source
  rigs auto-recalibrate the click mapping against a known target before clicking
  to stay drift-proof.
- **Single vs double click matters** for Windows UI — `vmclick.sh` is single by
  default, `-d` for double.

## End-to-end: a self-healing editor session

```bash
DCL_VM_DIR=~/vms/win11 vm/run-playtest.sh 3
```

That reset+launches the harness, polls the run log every 30 s, kills+retries on
the two known pre-play stall signatures (licensing freeze, compile freeze), and
re-runs if a session finishes without ever reaching the world. A valid report
lands in `vm/reports/<UTC>.json`.

## Provisioning notes

The VM itself is a stock QEMU/KVM Windows 11 install with: OpenSSH server
(key auth, `dcl` user), the Unity editor + Unity Hub installed, and the
sockets (`qemu-qmp.sock`, `qemu-mon.sock`) exposed by the QEMU launch line
(`-qmp unix:…,server,nowait -monitor unix:…,server,nowait`) plus a forwarded SSH
port (`-netdev user,hostfwd=tcp::2222-:22`). Run the host-side socket scripts as
the user that owns those sockets (often root).
