# Wiring the automation bridge into unity-explorer

Three tiny insertions, mirroring how the existing CDP network monitor is wired.

## 0. Build define — `DCL_AUTOMATION_BRIDGE` (SECURITY, required)
`AutomationBridgeHandler` is gated `#if !UNITY_WEBGL && DCL_AUTOMATION_BRIDGE` so the WebSocket
command channel is **stripped from release players** entirely (same posture as AltTester's
`#if ALTTESTER`), not merely dormant behind the launch flag. Add `DCL_AUTOMATION_BRIDGE` to the
**Editor + dev/automation** scripting define symbols (Project Settings ▸ Player ▸ Scripting Define
Symbols, or via CI for the automation build target). **Release/store CI must NOT define it.** The
boot block in §2 and the test/selftest assemblies are guarded by the same define, so without it the
handler simply doesn't exist in the binary. (`exec` is additionally opt-in at runtime via
`DCL_AUTOMATION_ALLOW_EXEC=1`, so even a dev build isn't a default-on RCE surface.)

## 1. App-arg flag — `AppArgsFlags`
Add alongside `LAUNCH_CDP_MONITOR_ON_START`:

```csharp
public const string LAUNCH_AUTOMATION_BRIDGE = "launch-automation-bridge";
```

## 2. Boot — `BootstrapContainer.cs` (next to the existing `cdpClient` creation, ~line 126)

```csharp
// existing:
var cdpClient = ChromeDevToolHandler.New(
    applicationParametersParser.HasFlag(AppArgsFlags.LAUNCH_CDP_MONITOR_ON_START), applicationParametersParser);

// ADDED — license-free automation channel (built player), compiled in only when DCL_AUTOMATION_BRIDGE
// is defined (editor/dev builds), and then only started when the launch flag is passed:
#if !UNITY_WEBGL && DCL_AUTOMATION_BRIDGE
DCL.Automation.AutomationBridgeHandler? automationBridge = null;
if (applicationParametersParser.HasFlag(AppArgsFlags.LAUNCH_AUTOMATION_BRIDGE))
{
    automationBridge = new DCL.Automation.AutomationBridgeHandler(
        logger: new DCLLogger(ReportCategory.CHROME_DEVTOOL_PROTOCOL));
    var r = automationBridge.Start();
    if (r.IsBridgeStartError(out var err))
        ReportHub.LogError(ReportCategory.CHROME_DEVTOOL_PROTOCOL, $"automation bridge start failed: {err}");
}
// keep `automationBridge` on the container so Dispose() tears the socket down on shutdown.
#endif
```

## 3. (optional) Editor toggle — `DebugSettingsDrawer.cs`
Mirror the existing "Enable DevTools on Start" checkbox with an "Enable Automation Bridge" one that
toggles `LAUNCH_AUTOMATION_BRIDGE`, so devs can flip it from the inspector for editor Play runs too.

## Assembly placement
All of `AutomationCore.cs`, `AutomationCore.*`/`AutomationInput.cs`, `AutomationScreenshot.cs`,
`AutomationBridgeHandler.cs`, `NoopBrowser.cs`, `MainThreadPump.cs` go in the same assembly that
already references `CDPBridges` (the one holding `ChromeDevToolHandler` — the `DCL.WebRequests`
folder is a `DCL.WebRequests.asmref` → the **`DCL.Network`** asmdef). `CDPBridges`, `UnityEngine`,
`UnityEngine.UIElements`, and `Newtonsoft.Json` are already referenced there.

**REQUIRED asmdef edit (input injection):** `AutomationInput.cs` uses the new Input System, which
`DCL.Network.asmdef` does **not** currently reference. Add the Input System assembly to its
`references` — GUID `"GUID:75469ad4d38634e559750d17036d5f7c"` (`Unity.InputSystem`, the same one
`DCL.Input` and ~12 other DCL asmdefs already use). Without this, `AutomationInput.cs` won't compile.

> `AutomationCore` has **no UnityEditor dependency**, so the same file can also be referenced by the
> editor-only `ClaudeIPC` (have `ClaudeIPC.Dispatch` delegate its shared ops to `AutomationCore`,
> keeping only editor-only ops — `compile`, `asmdefs`, `game-rect` — behind `#if UNITY_EDITOR`).
