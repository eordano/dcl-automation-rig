# Cross-cutting — UI capture atlas (driver recipes for every screen)

> **Status:** Not verified here. The atlas itself is a real, current work product
> in the VM harness; the rig only *references* it. The recipes drive an in-world
> editor/player, which can't present on this host (no GPU — see [03](03-linux-alternatives.md)),
> so none were executed here. Treat the recipes as design intent until run.

The [fidelity tour](11-fidelity-tour.md) captures **world scenes**; this captures
**UI / auth / loading screens** — the states you can't reach just by teleporting
(OTP entry, Web3 verify/confirm popups, the loading-tip carousel, scene-loading
screen, new-account onboarding lobby, …). The hard part is *getting the client
into each state on demand*; the atlas solves that with one reflection recipe per
screen.

## Where the atlas code lives (reference, don't copy)

The atlas is maintained in the VM harness, not vendored here — it carries
absolute source paths and changes as the UI evolves. Locations below are
relative; `<vm-harness>` is the VM-control harness dir on your host (the sibling
`harness/` of the VM target dir), and `<explorer>` is your unity-explorer
checkout. Regenerate/extend it there; this rig only consumes it.

**The engine (Unity C#)** — runs in-editor on the VM. Host master at
`<explorer>/Explorer/Assets/DCL/Harness/Editor/DclPlaytestHarness.cs`, scp'd to
the same `Explorer\Assets\DCL\Harness\Editor\` path under the guest's
unity-explorer checkout.

- `RunAtlasHeadless()` (line ~233) → arms `mode="atlas"` → `RunAtlasCoroutine()`
  (line ~535): reaches interactive, teleports to the capture parcel, then
  iterates the dispatch list calling each `AtlasCapture_<route>` coroutine.
- `// ATLAS_METHODS_BEGIN` (line ~2504) — the spliced `AtlasCapture_<route>`
  driver methods.
- `// ATLAS_DRIVERS_BEGIN` (line ~637) — the dispatch list (which drivers run,
  in what order; ordering + mutating guards live here).

**The driver sources** (one `.cs` per screen) — edited, then merged into the
harness: `<vm-harness>/atlas-drivers-{clean,batch2,batch3,batch4}/*.cs`. A Python
splice (run inline) concatenates these into the two marked regions above, with
later batches overriding earlier on name collision.

**The runner + support scripts** (`<vm-harness>/`):

| Script | Role |
|---|---|
| `run-atlas.sh` | full capture (reset → launch → poll → pull → consolidate; self-healing babysitter) |
| `run-atlas-subset.sh "routes" [X,Y]` | capture only listed drivers (+optional parcel, e.g. `0,0` for Genesis Plaza) |
| `reset-and-launch-atlas.ps1` | kills/clears caches, launches Unity via scheduled task with `-executeMethod …RunAtlasHeadless` |
| `deploy-harness.sh` | scp the harness to the guest — **vendored** as [`vm/deploy-harness.sh`](../vm/deploy-harness.sh) |
| `poll-atlas.ps1` | guest status |
| `run-auth.sh` | the pre-world auth-screen pass — see [auth-capture pass](#the-auth-capture-pass) |
| `consolidate-atlas.py` + `atlas-codes.json` | map raw `NNN_<label>.png` → `<CODE>-<route>.png` using the dcl-react-ui registry |

**The naming registry + recipes — vendored here.** The route→code map, its
tooling, and the path-free *design knowledge* (the recipes) live in this repo
under [`atlas/`](../atlas/). Only the engine, the driver `.cs` sources, and the
PNG output stay in the VM harness:

| In-repo | Role |
|---|---|
| [`atlas/atlas-codes.json`](../atlas/atlas-codes.json) | canonical route→`<CODE>` registry (the names) |
| [`atlas/INDEX.md`](../atlas/INDEX.md) | generated human index of every named surface |
| [`atlas/gen-index.py`](../atlas/gen-index.py) | regenerate `INDEX.md` from the registry (`--check` gates staleness) |
| [`atlas/consolidate-atlas.py`](../atlas/consolidate-atlas.py) | rename raw `NNN_<route>.png` → `<CODE>-<route>.png` using the registry |
| [`atlas/recipes.json`](../atlas/recipes.json) | machine-readable recipe per screen (state to reach, data needs, feasibility) — the design knowledge each driver was built from |
| [`atlas/DIGEST.md`](../atlas/DIGEST.md) | human-readable digest of the recipes |

Keep this registry + recipes as the source of truth for names and design intent;
the VM harness copy should track them. (Recipes are vendored because they're
path-free and are the one artifact you'd actually rebuild the atlas *from* on
another host — see the [recipe schema](#recipe-schema-recipesjson-one-object-per-screen)
below.)

**Output:** the code-named PNGs land in `<out>/` (default `atlas/shots/atlas/`,
override `DCL_ATLAS_OUT`); the capture run on the VM produces them under the
harness's own `shots/atlas/`.

**Flow:** edit driver `.cs` → splice into `DclPlaytestHarness.cs` →
`deploy-harness.sh` → `run-atlas.sh` / `run-atlas-subset.sh` (drives Unity on the
VM) → `consolidate-atlas.py` → `shots/atlas/`.

## The auth-capture pass

A handful of surfaces are **pre-world** — the login selection, OTP entry, the
Web3 verify/confirm popups, the profile-fetching and new/existing-account lobby
screens. They live in the authentication FSM *before* a world ever loads, so the
in-world atlas mode can't reach them. They have their own harness entry point:

- **`RunAuthCaptureHeadless`** (mode `"auth"`) — arms the auth screen, then
  force-shows each pre-world view directly. The FSM keeps trying to re-assert the
  lobby during settle, so each driver hides its sibling sub-views immediately
  before the shot (`HideAuthSubViews` / `ActivateAuthSubView`) and hides the debug
  panel. With a cached identity the FSM auto-skips login, which is exactly why
  these must be force-shown rather than observed.

It needs no new launcher — it's the parameterized editor launch with a different
method:

```bash
# guest side: same Scheduled-Task trick, different -executeMethod
windows/reset-and-launch-editor.ps1 -Method DCL.Harness.DclPlaytestHarness.RunAuthCaptureHeadless
```

The VM harness wraps that in `run-auth.sh` (reset → launch → poll → pull report
+ shots → `consolidate-atlas.py`), the same babysitter shape as `run-atlas.sh`.
The method itself is vendored (it's in [`../unity/DclPlaytestHarness.cs`](../unity/DclPlaytestHarness.cs));
the runner stays in the VM harness with the rest of the atlas pipeline.

## Capture reality — coverage and the hardness tiers

The registry *names* every surface, but they are not equally reachable. In
practice each falls into one of these tiers — know which before you spend time on
one:

1. **Force-showable (most).** A reflection driver pushes the controller/view into
   the target state on the quiet parcel, no live data needed — reliable every
   run. The modals, panels, auth/loading screens, force-shown popups.
2. **Content-gated.** The surface only renders against live realm data (real
   peers, an active broadcast, or an external web-view backing the screen), so
   capture has to happen where that data exists — e.g. at a crowded public parcel
   rather than the quiet one, with extra settle for activity to arrive.
3. **Precondition-blocked.** The action needs an on-chain asset/permission the
   test account lacks; the backend rejects it otherwise. The triggering *form*
   still captures, but completing the action needs the account provisioned first.
4. **Structurally hard.** The trigger can't be reached by the usual MVC
   reflection — e.g. a generic, by-ref ECS call whose signature resists
   reflection against the precompiled assembly.

Keep this per-surface status **out of the registry** — it drifts as the client
and realm change (a "needs live data" screen captures fine once you capture it
where that data exists). The registry holds only the stable name→code mapping;
status lives here in prose.

## Operational discipline (iterate without regressing the set)

The set is fragile: some screens only come out clean
under specific conditions, and a blind full run can overwrite a good shot with a
worse one. The rules that keep it at its best:

- **Subset-only iteration.** Use `run-atlas-subset.sh "routes" [X,Y]` to recapture
  just the screens you're working on. A full `run-atlas.sh` is for a cold rebuild,
  not touch-ups — it can regress already-clean screens.
- **Back up, then keep-best.** Snapshot the current best set (e.g. an
  `atlas-best-backup/`) before a run; afterwards keep a new shot **only when you've
  verified it's actually better**. A failed real action can leave an error toast on
  an otherwise-clean form — when that happens, restore the backup.
- **Mutating drivers are opt-in.** Drivers that change real state run **only when
  named explicitly in a subset filter, never in a full run**, so repeated captures
  never re-trigger those actions. This is the `mutatingDrivers` guard in the
  dispatch loop (`// ATLAS_DRIVERS_BEGIN`).
- **No-noise by default; crowded parcel on demand.** Capture defaults to a quiet,
  unpopulated parcel so screens are stable and uncluttered. Only the content-gated
  shots spawn at a crowded public parcel — via a **one-shot parcel override** (a
  small `X,Y` file the harness reads once then deletes) plus extra settle for live
  activity to arrive.

## Recipe schema (`recipes.json`, one object per screen)

| field | meaning |
|---|---|
| `label` | the screen (e.g. "OTP Code Entry Screen", "web3confirm", "sceneloading") |
| `feasible` | `yes-automated` (most) or `needs-data` (needs a live backend / test account — see the hardness tiers above) |
| `captureContext` | what state you must already be in: `auth-cached` or `in-world-session` |
| `confidence` | how sure the recipe is (high / medium) |
| `triggerSummary` | how the screen appears at runtime (the FSM/event path) |
| `driverSteps` | the concrete reflection recipe to *force* it (see below) |
| `dataNeeds` | preconditions (e.g. "fresh wallet with no profile", "mock OTP server") |
| `keyRefs` | source file:line anchors the recipe depends on |

## The technique (what every `driverSteps` does)

Reach a UI state by reflecting through the live MVC graph — the same reflection
discipline as `DclPlaytestHarness` ([04](04-windows-editor.md)), so it has no
hard dependency on game assemblies:

1. From the running session, get `dynamicWorldContainer` → `MvcManager`.
2. `FindControllerByTypeName(mvc, "AuthenticationScreenController")` (etc.).
3. Read/drive its FSM (`GetPrivateField(authCtl, "fsm")`) or its
   `CurrentState` `ReactiveProperty<AuthStatus>` to push it into the target state.
4. Instantiate/Show the view directly when there's no controller (e.g.
   `Web3ConfirmationPopupView.ShowAsync(...)`), or invoke the private trigger
   (`Login(LoginMethod.METAMASK)`).
5. Let it settle, then screenshot (`dcl_headless_shot`, or the VM screendump).

## Using it

1. Read `DIGEST.md`; pick a screen and check `feasible`/`dataNeeds`/`captureContext`.
2. Get the client into the required `captureContext` first (cached identity for
   `auth-cached`; a booted world for `in-world-session`).
3. Implement the recipe's `driverSteps` as a harness method (Windows:
   `DclPlaytestHarness`; Linux: a static method invoked over `ClaudeIPC`),
   keyed off the `keyRefs` anchors.
4. Capture, then advance — same settle discipline as the fidelity tour (a floor
   after the transition, a real readiness check, a per-screen timeout).

## Which capture tool for what

| Want to capture… | Use |
|---|---|
| A world scene/game (teleport target) | [fidelity tour](11-fidelity-tour.md) |
| A UI / auth / loading screen (no teleport reaches it) | this atlas |
| An arbitrary editor method / state | [ClaudeIPC](01-linux-editor.md) directly |
