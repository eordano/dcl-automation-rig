# Client patches — fold into canonical unity-explorer

Platform-agnostic changes to the driven `unity-explorer` checkout, extracted so they
travel with the rig and can be applied onto the canonical unity-explorer commit
(`d68c73062`). All repo-relative (apply from the Explorer repo root).
Env-gated / additive — unset env ⇒ identical to upstream.

| Patch | File | Effect |
|---|---|---|
| `dcl-client-shared-gateway-catalyst.patch` | `Assets/DCL/NetworkDefinitions/Browser/GatewayUrlsSource.cs` | `CATALYST_BASE`/`CATALYST_EXCLUDE` gateway retarget + idempotency guard |
| `dcl-client-shared-realm.patch` | `Assets/DCL/Infrastructure/Global/Dynamic/WorldManifestProvider.cs` | `MAIN_REALM_NAMES += "myrealm"` |

## Apply (from the Explorer repo root, on d68c73062)
    git apply --check patches/dcl-client-shared-realm.patch   # verify
    git apply         patches/dcl-client-shared-realm.patch
    git apply --check patches/dcl-client-shared-gateway-catalyst.patch
    git apply         patches/dcl-client-shared-gateway-catalyst.patch
# If context drifted: `git apply -C1 <patch>` or `patch -p1 -F3 < <patch>`.
# Base blobs: gateway efb88ae6, realm 7c38589d (both last touched at 15009411).

## Harness comms-hold (separate — rig-side, not a client patch)
The rig-canonical harness change (`unity/DclPlaytestHarness.cs`, +129 comms-hold mode)
ships as a `git am`-able patch:
- `patches/harness-comms-hold-c651d08.patch`  — apply with `git am < <file>` (keeps msg+author)

It modifies `unity/DclPlaytestHarness.cs` (the canonical harness source). After folding,
the canonical `unity/*.cs` is what each platform drops into `Explorer/Assets/Editor/DclHarness/`.
