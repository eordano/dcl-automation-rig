// ============================================================================
//  DclPlaytestHarness.cs   —  compiles clean vs Unity 6000.4.0f1 (DCL.Harness.Editor asmdef)
// ----------------------------------------------------------------------------
//  In-editor automation + profiling hook for the Decentraland unity-explorer
//  client ("AVENUE 3" of the DCL Unity auto-fix loop).
//
//  Purpose:
//    Programmatically enter Play mode, drive a deterministic Genesis Plaza
//    session (load -> measure time-to-interactive -> teleport -> chat ->
//    sample perf for N seconds), capture warnings/errors, and dump a single
//    structured JSON report to C:\Users\dcl\harness-report.json that the
//    loop agent can read over SSH. Replaces fragile blind QMP GUI driving.
//
//  Two entry points:
//    * static RunHeadless()  -> for `-executeMethod DCL.Harness.DclPlaytestHarness.RunHeadless`
//    * [MenuItem]            -> "DCL/Harness/Run Genesis Plaza Playtest"
//
//  DESIGN PRINCIPLE — reflection-only against the running client:
//    The runtime services we need (Profiler, LoadingStatus, RealmNavigator,
//    ChatMessagesBus) are created by the DI bootstrap and held in PRIVATE
//    fields of the MainSceneLoader MonoBehaviour. There is NO static service
//    locator. Rather than hard-reference ~305 game assemblies from an Editor
//    asmdef (brittle, slow, and a compile dependency nightmare), this harness
//    reaches them via reflection. That keeps the asmdef tiny (Editor + a few
//    Unity packages) and decoupled. The reflected member names below are all
//    verified against source (file:line in DESIGN.md); items still needing
//    LIVE confirmation are marked  // TODO(LIVE).
//
//  WHY THIS WORKS WITHOUT TOUCHING THE SCENE:
//    Main.unity is build scene index 0 (ProjectSettings/EditorBuildSettings).
//    EnterPlaymode loads it; MainSceneLoader.Awake() runs the normal bootstrap
//    (auto-login as the saved "Evaristo" identity in editor) and loads the
//    starting realm = Genesis Plaza. We only OBSERVE + nudge via public/known
//    APIs; we never simulate keyboard/mouse input.
// ============================================================================

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using Unity.Profiling;

namespace DCL.Harness
{
    public static class DclPlaytestHarness
    {
        // ----- Tunables -------------------------------------------------------
        // Output paths default to the Windows VM-harness locations but are ENV-OVERRIDABLE
        // (DCL_HARNESS_REPORT / DCL_HARNESS_SHOTS) so the SAME harness runs on Mac/Linux,
        // where C:\ doesn't exist. The launcher exports these before starting the editor;
        // static readonly reads them once at class load (re-read on each domain reload).
        // [Mac-parity addition — keep in sync with the VM harness master.]
        // MUTABLE (not readonly) so they can ALSO be set at runtime via SetCapturePaths over ClaudeIPC —
        // see why below. Default from env at class load; re-read on each domain reload.
        private static string REPORT_PATH = EnvOr("DCL_HARNESS_REPORT", @"C:\Users\dcl\harness-report.json");
        private static string SHOTS_DIR   = EnvOr("DCL_HARNESS_SHOTS",  @"C:\Users\dcl\harness-shots");
        // Platform-aware base dir for the perf-CSV + atlas-subset/parcel sidecar files (portability fix):
        // Linux/Mac default under /tmp; Windows keeps the VM-harness default. Override with DCL_ATLAS_DIR.
        private static readonly string ATLAS_DIR = EnvOr("DCL_ATLAS_DIR", Path.DirectorySeparatorChar == '/' ? "/tmp/dcl-atlas" : @"C:\Users\dcl");
        private static string EnvOr(string envVar, string fallback)
        {
            try { var v = Environment.GetEnvironmentVariable(envVar); return string.IsNullOrEmpty(v) ? fallback : v; }
            catch { return fallback; }
        }
        // Set the capture output paths at RUNTIME via ClaudeIPC, so the launcher does NOT have to relaunch the
        // editor just to inject DCL_HARNESS_SHOTS/REPORT env. The relaunch path was fragile on Mac: quitting a
        // running editor made Unity HUB re-exec a fresh editor WITHOUT our env (so shots/report went to the
        // Windows fallback path and nothing landed). Now mac/atlas-capture.sh execs this on the LIVE editor
        // before RunAtlasFromMenu — no kill, no Hub re-exec. Empty/blank arg = keep the current value.
        // [Mac-parity addition — keep in sync with the VM harness master.]
        public static void SetCapturePaths(string shots, string report)
        {
            if (!string.IsNullOrEmpty(shots))  SHOTS_DIR   = shots;
            if (!string.IsNullOrEmpty(report)) REPORT_PATH = report;
            Debug.Log($"[Harness] capture paths set: SHOTS_DIR={SHOTS_DIR} REPORT_PATH={REPORT_PATH}");
        }

        // Runtime subset override (merged into the atlas driver filter). Same motivation as SetCapturePaths:
        // DCL_ATLAS_ONLY is read from the process env at run start, but a REUSED editor has fixed launch-time
        // env — so set the subset over IPC instead. Comma/space-separated route keys; empty clears. [Mac-parity.]
        private static readonly System.Collections.Generic.HashSet<string> atlasOnlyOverride = new System.Collections.Generic.HashSet<string>();
        private static bool atlasOnlyOverrideSet;   // true once SetAtlasOnly was called -> it becomes AUTHORITATIVE
        public static void SetAtlasOnly(string csv)
        {
            // AUTHORITATIVE: once called, the override fully REPLACES the env/file subset (even when cleared to
            // empty = full run). Otherwise a reused editor's stale launch-time DCL_ATLAS_ONLY env would keep
            // re-applying the old subset and a requested full run would silently stay a subset (observed).
            atlasOnlyOverrideSet = true;
            atlasOnlyOverride.Clear();
            if (!string.IsNullOrEmpty(csv))
                foreach (var tok in csv.Split(new[] { ',', ' ', '\n', '\r', '\t', '﻿' }, StringSplitOptions.RemoveEmptyEntries))
                    atlasOnlyOverride.Add(tok.Trim().Trim('﻿'));
            Debug.Log("[Harness] atlas-only override set: " + (atlasOnlyOverride.Count == 0 ? "(cleared -> FULL)" : string.Join(",", atlasOnlyOverride)));
        }
        // Per-photo settle window in SECONDS (wall-clock, NOT frames — a frame count
        // elapsed in under a second at the editor's high Play FPS). CaptureShot takes
        // one screenshot per second across this window and keeps the LAST as the
        // settled frame. Env-overridable (DCL_ATLAS_SETTLE_SECONDS). [Mac-parity.]
        private static int atlasSettleSeconds = (int)EnvFloatOr("DCL_ATLAS_SETTLE_SECONDS", 8f);
        private static float EnvFloatOr(string envVar, float fallback)
        {
            try
            {
                return float.TryParse(Environment.GetEnvironmentVariable(envVar),
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
                    out var v) && v >= 0 ? v : fallback;
            }
            catch { return fallback; }
        }
        private const float  LOAD_TIMEOUT_S   = 180f;  // time-to-interactive budget
        private const float  SAMPLE_SECONDS   = 20f;   // perf sampling window per phase
        private const float  SETTLE_AFTER_TP  = 8f;    // let a scene stream in after teleport

        // ----- Screenshot resolution (configurable via the DCL/Harness/Capture Resolution menu,
        //       or the --harness-res=WxH and --harness-super=N command-line args) ---------------
        //       Final PNG resolution = captureW*captureSuper x captureH*captureSuper.
        //       Use NATIVE GameView resolution (superSize=1): screen-space-overlay UI composites
        //       correctly only in the native capture path. superSize>1 re-renders cameras WITHOUT the
        //       UI overlay -> blank/black UI (do not use it for UI screenshots).
        //       Default: native 3840x2160 (4K), superSize 1.
        private static int captureW     = 3840;
        private static int captureH     = 2160;
        private static int captureSuper = 1;
        // NO-NOISE (2026-06-19, user steer): atlas captures run against the LIVE prod realm but must NOT
        // be visible to other users. Teleport to a remote, unpopulated parcel (far from the crowded Genesis
        // Plaza at 0,0) before capturing the UI surface. Override with --harness-atlas-parcel=X,Y.
        private static Vector2Int atlasParcel = new Vector2Int(140, 140);
        public static void SetCaptureResolution(int width, int height, int superSize)
        {
            captureW = Mathf.Max(16, width);
            captureH = Mathf.Max(16, height);
            captureSuper = Mathf.Clamp(superSize, 1, 8);
            Debug.Log($"[Harness] capture resolution set: {captureW}x{captureH} x{captureSuper} = {captureW * captureSuper}x{captureH * captureSuper}");
        }
        // Parse --harness-res=1920x1080 and --harness-super=2 from the command line (launch-script override).
        private static void ApplyCaptureArgsFromCommandLine()
        {
            try
            {
                foreach (string a in Environment.GetCommandLineArgs())
                {
                    if (a.StartsWith("--harness-res=", StringComparison.Ordinal))
                    {
                        string[] wh = a.Substring("--harness-res=".Length).Split('x', 'X');
                        if (wh.Length == 2 && int.TryParse(wh[0], out int w) && int.TryParse(wh[1], out int h))
                        { captureW = Mathf.Max(16, w); captureH = Mathf.Max(16, h); }
                    }
                    else if (a.StartsWith("--harness-super=", StringComparison.Ordinal)
                             && int.TryParse(a.Substring("--harness-super=".Length), out int s))
                        captureSuper = Mathf.Clamp(s, 1, 8);
                    else if (a.StartsWith("--harness-atlas-parcel=", StringComparison.Ordinal))
                    {
                        string[] xy = a.Substring("--harness-atlas-parcel=".Length).Split(',');
                        if (xy.Length == 2 && int.TryParse(xy[0], out int px) && int.TryParse(xy[1], out int py))
                            atlasParcel = new Vector2Int(px, py);
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[Harness] capture-arg parse failed: " + e.Message); }
        }

        // Genesis Plaza spawn is ~ (0,0); pick two nearby parcels that are part
        // of Genesis Plaza so we exercise scene streaming without leaving GP.
        private static readonly Vector2Int[] TELEPORT_TARGETS =
        {
            new Vector2Int(0,  0),
            new Vector2Int(-9, -9),  // GP plaza edge
            new Vector2Int(74, -9),  // a populated GP parcel
            // NOTE (2026-06-17): far genesis-city teleports (-50,0)/(50,50) were tried for wider
            // scene coverage but their load/timing churn destabilized downstream panel-render
            // assertions (18/23 vs 22/23). Reverted; wider content will be pursued via realm/World
            // switch instead, which is a cleaner boundary than arbitrary far teleports.
        };

        // ----- Log capture ----------------------------------------------------
        private static readonly List<LogEntry> warnings = new();
        private static readonly List<LogEntry> errors   = new();
        private static int totalLogCount;
        private static int shotIndex;

        private struct LogEntry { public string message; public string stack; public string type; public double t; }

        // =====================================================================
        //  ENTRY POINTS  (domain-reload-safe via SessionState + playModeStateChanged)
        // =====================================================================
        //  Entering Play mode triggers a domain reload that wipes ALL static state
        //  and the EditorApplication.update pump, so we cannot drive the session
        //  straight through EnterPlaymode(). Instead the entry points record the
        //  request in SessionState (which survives domain reloads within an editor
        //  session) and call EnterPlaymode(); after the reload, OnPlayModeStateChanged
        //  (re-registered on every domain load via [InitializeOnLoadMethod]) sees the
        //  flag and starts the driving coroutine — now safely inside Play mode (no
        //  further reload until we exit). On exit, the headless path quits the editor.

        private const string KEY_RUN  = "DclHarness.Run";
        private const string KEY_EXIT = "DclHarness.ExitOnFinish";
        private const string KEY_QUIT = "DclHarness.QuitWhenEditMode";
        private const string KEY_MODE = "DclHarness.Mode";   // "session" | "perf" | "cpu"

        // --- CPU-breakdown mode: enumerate ALL time-unit profiler markers at runtime
        // and attribute per-frame ms at steady idle, so we see what eats the frame
        // (every ECS system emits a "{SystemName}.Update" CustomSampler; see BaseUnityLoopSystem).
        private static readonly string CPU_CSV_PATH    = Environment.GetEnvironmentVariable("DCL_CPU_CSV")    ?? Path.Combine(ATLAS_DIR, "harness-cpu.csv");
        private const float  CPU_SETTLE_S    = 35f;  // Genesis Plaza keeps streaming assets well past "Completed"
        private const float  CPU_WARMUP_S    = 3f;
        private const float  CPU_SAMPLE_S    = 15f;
        private const int    CPU_MAX_MARKERS = 600;
        private const int    CPU_TOP_N       = 70;

        // --- shadow A/B mode: toggle all shadow-casting lights ON/OFF in paired windows
        // at the rendered spawn scene -> isolates the true CPU+GPU cost of shadows.
        private static readonly string SHADOW_CSV_PATH = Environment.GetEnvironmentVariable("DCL_SHADOW_CSV") ?? Path.Combine(ATLAS_DIR, "harness-shadow.csv");
        private const float  SHADOW_SETTLE_S = 30f;  // let Genesis Plaza stream shadow casters

        // --- render decomposition: A/B each rendering knob on/off in its own block ---
        private static readonly string RENDER_CSV_PATH = Environment.GetEnvironmentVariable("DCL_RENDER_CSV") ?? Path.Combine(ATLAS_DIR, "harness-render.csv");
        private const int    RENDER_WINDOWS_PER_KNOB = 24;

        // --- perf-benchmark mode (statistically-rigorous micro-measurement) -------
        // Within ONE session, alternate a target subsystem ON/OFF in counterbalanced
        // (ABBA) ~1s windows and record per-frame CPU/GPU. Because both conditions
        // share the identical scene/stream/thermal state, the paired difference cancels
        // the huge between-session variance, so sub-ms effects become measurable.
        // First instrumented target: the Backpack character-preview render pipeline
        // (toggle = preview camera.enabled). The A-B frame-cost delta = its true cost.
        private static readonly string PERF_CSV_PATH   = Environment.GetEnvironmentVariable("DCL_PERF_CSV")   ?? Path.Combine(ATLAS_DIR, "harness-perf.csv");
        private const float  PERF_WARMUP_S   = 4f;   // discarded after Backpack settle (JIT/asset warmup)
        private const float  PERF_WINDOW_S   = 1f;   // one A or B window
        private const int    PERF_WINDOWS    = 80;   // total windows (~80s of sampling)
        private const int    PERF_DROP_FRAMES = 4;   // transition frames dropped at each window start
        private static bool _exitOnFinish;
        private static bool _runActive;   // guards against a second concurrent trigger (double-subscribe / shared-state race)

        // Invoked by:  Unity.exe -projectPath <proj> -executeMethod DCL.Harness.DclPlaytestHarness.RunHeadless ...
        // NOTE: Do NOT pass -batchmode (Play mode + ProfilerRecorder + rendering are
        //       unreliable headless). Run windowed via the session-1 Scheduled Task.
        public static void RunHeadless()
        {
            Debug.Log("[Harness] RunHeadless invoked; arming and entering Play mode.");
            Arm(exitOnFinish: true, mode: "session");
        }

        // Invoked by:  -executeMethod DCL.Harness.DclPlaytestHarness.RunPerfHeadless
        public static void RunPerfHeadless()
        {
            Debug.Log("[Harness] RunPerfHeadless invoked; arming perf-benchmark mode.");
            Arm(exitOnFinish: true, mode: "perf");
        }

        // Invoked by:  -executeMethod DCL.Harness.DclPlaytestHarness.RunCpuBreakdownHeadless
        public static void RunCpuBreakdownHeadless()
        {
            Debug.Log("[Harness] RunCpuBreakdownHeadless invoked; arming CPU-breakdown mode.");
            Arm(exitOnFinish: true, mode: "cpu");
        }

        // Invoked by:  -executeMethod DCL.Harness.DclPlaytestHarness.RunShadowPerfHeadless
        public static void RunShadowPerfHeadless()
        {
            Debug.Log("[Harness] RunShadowPerfHeadless invoked; arming shadow A/B mode.");
            Arm(exitOnFinish: true, mode: "shadow");
        }

        // Invoked by:  -executeMethod DCL.Harness.DclPlaytestHarness.RunRenderDecompHeadless
        public static void RunRenderDecompHeadless()
        {
            Debug.Log("[Harness] RunRenderDecompHeadless invoked; arming render-decomposition mode.");
            Arm(exitOnFinish: true, mode: "render");
        }

        // Invoked by:  -executeMethod DCL.Harness.DclPlaytestHarness.RunAuthCaptureHeadless
        // AUTH-SCREEN CAPTURE mode (2026-06-19, user request): the ONLY mode that does NOT skip auth —
        // it sets debugSettings.showAuthentication=TRUE so the login/auth flow renders, then screenshots
        // each AuthStatus state the flow transitions through (uses the EXISTING cached identity; no wallet
        // generation, no browser). Does NOT need to reach in-world.
        public static void RunAuthCaptureHeadless()
        {
            Debug.Log("[Harness] RunAuthCaptureHeadless invoked; arming auth-screen-capture mode.");
            Arm(exitOnFinish: true, mode: "auth");
        }

        [MenuItem("DCL/Harness/Capture Auth Screens")]
        public static void RunAuthCaptureFromMenu() => Arm(exitOnFinish: false, mode: "auth");

        // ATLAS: capture the full UI surface (panels/overlays/detail states) in-world. Reaches interactive,
        // teleports to a remote unpopulated parcel (NO-NOISE), then drives each AtlasCapture_<label> driver.
        public static void RunAtlasHeadless()
        {
            Debug.Log("[Harness] RunAtlasHeadless invoked; arming atlas UI-surface capture mode.");
            Arm(exitOnFinish: true, mode: "atlas");
        }

        [MenuItem("DCL/Harness/Capture Atlas (UI surface)")]
        public static void RunAtlasFromMenu() => Arm(exitOnFinish: false, mode: "atlas");

        // COMMS HOLD: live multi-client test (2026-06-22). Join the launch realm (--realm
        // https://<your-catalyst-host>), reach interactive, teleport to Genesis Plaza (0,0), then HOLD
        // in-world indefinitely so peer clients (Linux/Windows) see this avatar and we see theirs.
        // UNLIKE atlas: no teleport-away, no realm switch, no Play-exit. Logs a remote-avatar census
        // + refreshes a world shot every CENSUS_S so the rig reads the count from the log. exec via
        // ClaudeIPC: exec method=DCL.Harness.DclPlaytestHarness.RunCommsHoldFromMenu.
        [MenuItem("DCL/Harness/Comms Hold at Genesis Plaza (live multi-client)")]
        public static void RunCommsHoldFromMenu() => Arm(exitOnFinish: false, mode: "comms");

        // --- Screenshot resolution presets (final PNG = GameView size x superSize) ---
        // NATIVE-resolution presets (superSize 1 so UI composites correctly).
        [MenuItem("DCL/Harness/Capture Resolution/720p native (1280x720)")]   public static void Res720()  => SetCaptureResolution(1280, 720, 1);
        [MenuItem("DCL/Harness/Capture Resolution/1080p native (1920x1080)")] public static void Res1080() => SetCaptureResolution(1920, 1080, 1);
        [MenuItem("DCL/Harness/Capture Resolution/1440p native (2560x1440)")] public static void Res1440() => SetCaptureResolution(2560, 1440, 1);
        [MenuItem("DCL/Harness/Capture Resolution/4K native (3840x2160)")]    public static void Res4K()   => SetCaptureResolution(3840, 2160, 1);
        [MenuItem("DCL/Harness/Capture Resolution/5K native (5120x2880)")]    public static void Res5K()   => SetCaptureResolution(5120, 2880, 1);

        [MenuItem("DCL/Harness/Run Genesis Plaza Playtest")]
        public static void RunFromMenu() => Arm(exitOnFinish: false, mode: "session");

        [MenuItem("DCL/Harness/Run Perf Benchmark (Backpack preview)")]
        public static void RunPerfFromMenu() => Arm(exitOnFinish: false, mode: "perf");

        [MenuItem("DCL/Harness/Run CPU Breakdown (steady-state markers)")]
        public static void RunCpuFromMenu() => Arm(exitOnFinish: false, mode: "cpu");

        [MenuItem("DCL/Harness/Run Shadow A/B (cost of shadows)")]
        public static void RunShadowFromMenu() => Arm(exitOnFinish: false, mode: "shadow");

        [MenuItem("DCL/Harness/Run Render Decomposition (per-knob cost)")]
        public static void RunRenderFromMenu() => Arm(exitOnFinish: false, mode: "render");

        private static void Arm(bool exitOnFinish, string mode)
        {
            ApplyCaptureArgsFromCommandLine();   // honor --harness-res=WxH / --harness-super=N overrides

            // Open Main.unity (build index 0) so EnterPlaymode runs the real bootstrap.
            try
            {
                const string mainScenePath = "Assets/Scenes/Main.unity";
                if (UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path != mainScenePath)
                    UnityEditor.SceneManagement.EditorSceneManager.OpenScene(mainScenePath);
            }
            catch (Exception e) { Debug.LogWarning("[Harness] Could not open Main.unity: " + e.Message); }

            // Skip the auth/welcome screen so the editor run loads Genesis Plaza
            // unattended (otherwise it stalls at LoadingStage.AuthenticationScreenShowing).
            // We flip the serialized debugSettings.showAuthentication on the in-memory
            // MainSceneLoader component (NOT saved to disk -> no scene/git change).
            try
            {
                var msl = FindMainSceneLoader();   // edit-mode instance in the open scene
                if (msl is UnityEngine.Object uo)
                {
                    var so = new SerializedObject(uo);
                    var prop = so.FindProperty("debugSettings.showAuthentication");
                    if (prop != null)
                    {
                        bool showAuth = (mode == "auth");   // ONLY auth-capture mode shows the login flow
                        prop.boolValue = showAuth;
                        so.ApplyModifiedPropertiesWithoutUndo();   // in-memory only
                        Debug.Log($"[Harness] debugSettings.showAuthentication={showAuth} (mode={mode}).");
                    }
                    else Debug.LogWarning("[Harness] could not find debugSettings.showAuthentication property.");
                }
                else Debug.LogWarning("[Harness] MainSceneLoader not found in edit scene to set auth-skip.");
            }
            catch (Exception e) { Debug.LogWarning("[Harness] auth-skip set failed: " + e.Message); }

            SessionState.SetBool(KEY_RUN, true);
            SessionState.SetBool(KEY_EXIT, exitOnFinish);
            SessionState.SetBool(KEY_QUIT, false);
            SessionState.SetString(KEY_MODE, mode);

            if (EditorApplication.isPlaying) OnPlayModeStateChanged(PlayModeStateChange.EnteredPlayMode);
            else EditorApplication.EnterPlaymode();
        }

        // Re-registered after EVERY domain reload (incl. edit->play and play->edit).
        [InitializeOnLoadMethod]
        private static void Bootstrap()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange s)
        {
            if (s == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(KEY_RUN, false))
            {
                if (_runActive) { Debug.LogWarning("[Harness] a run is already active — ignoring concurrent trigger (would double-count logs / race shared state)."); return; }
                _runActive = true;
                SessionState.SetBool(KEY_RUN, false);   // consume: run once
                _exitOnFinish = SessionState.GetBool(KEY_EXIT, false);
                Application.logMessageReceivedThreaded -= OnLog;        // idempotent: never double-subscribe (double-counts every log)
                warnings.Clear(); errors.Clear(); totalLogCount = 0;    // safe: OnLog not subscribed during the clear (no threaded race)
                Application.logMessageReceivedThreaded += OnLog;
                string mode = SessionState.GetString(KEY_MODE, "session");
                SessionState.SetString(KEY_MODE, "session");
                Debug.Log($"[Harness] Play mode entered; mode={mode}.");
                IEnumerator routine = mode == "perf"   ? RunPerfCoroutine()
                                    : mode == "cpu"    ? RunCpuBreakdownCoroutine()
                                    : mode == "shadow" ? RunShadowPerfCoroutine()
                                    : mode == "render" ? RunRenderDecompCoroutine()
                                    : mode == "auth"   ? RunAuthCaptureCoroutine()
                                    : mode == "atlas"  ? RunAtlasCoroutine()
                                    : mode == "comms"  ? RunCommsHoldCoroutine()
                                    : RunSessionCoroutine();
                HarnessRunner.Start(routine);
            }
            else if (s == PlayModeStateChange.EnteredEditMode)
            {
                _runActive = false;   // the run is over once we're back in edit mode
                if (!SessionState.GetBool(KEY_QUIT, false)) return;
                SessionState.SetBool(KEY_QUIT, false);
                bool exit = SessionState.GetBool(KEY_EXIT, false);
                SessionState.SetBool(KEY_EXIT, false);
                Debug.Log("[Harness] Back in edit mode after run; exitEditor=" + exit);
                if (exit) EditorApplication.Exit(0);
            }
        }

        // =====================================================================
        //  MAIN SESSION COROUTINE
        // =====================================================================
        // AUTH-SCREEN CAPTURE (mode "auth"): showAuthentication was set TRUE in Arm, so the login/auth flow
        // renders. Observe AuthenticationScreenController.CurrentState (AuthStatus) and screenshot each
        // distinct state, plus timed fallback shots so we capture screens even if the controller isn't
        // reachable via the MVCManager during the pre-in-world auth phase. Uses the existing cached identity.
        // Auth sub-views are siblings on AuthenticationScreenView; force-showing one does NOT hide the others
        // (and the existing-account lobby auto-shows), so they stack. Hide all of them (instant: Hide() if
        // present, plus gameObject.SetActive(false)) before each force-show driver so each captures cleanly.
        // CharacterPreviewView (the avatar) is intentionally left visible.
        private static readonly string[] AUTH_SUBVIEWS =
        {
            "LoginSelectionAuthView", "VerificationDappAuthView", "VerificationOTPAuthView",
            "ProfileFetchingAuthView", "LobbyForExistingAccountAuthView", "LobbyForNewAccountAuthView",
        };
        private static void HideAuthSubViews(object authView)
        {
            if (authView == null) return;
            foreach (string name in AUTH_SUBVIEWS)
            {
                try
                {
                    object sub = GetMember(authView, name);
                    if (sub == null) continue;
                    // Hide() + SetActive(false) for hard isolation (Hide() alone leaves siblings stacked).
                    // The target view is re-activated by ActivateAuthSubView before its driver runs, because
                    // some views' Show() (e.g. VerificationDappAuthView) does not SetActive(true) themselves.
                    var hide = sub.GetType().GetMethod("Hide", BindingFlags.Public | BindingFlags.Instance, null, System.Type.EmptyTypes, null);
                    if (hide != null) hide.Invoke(sub, null);
                    object go = GetMember(sub, "gameObject");
                    var setActive = go?.GetType().GetMethod("SetActive", new[] { typeof(bool) });
                    setActive?.Invoke(go, new object[] { false });
                }
                catch { }
            }
        }

        // Re-activate one auth sub-view's gameObject after HideAuthSubViews, so a driver whose Show() does not
        // SetActive(true) (e.g. VerificationDappAuthView) still renders. Call between HideAuthSubViews and the driver.
        private static void ActivateAuthSubView(object authView, string name)
        {
            try
            {
                object sub = GetMember(authView, name);
                object go = sub != null ? GetMember(sub, "gameObject") : null;
                var setActive = go?.GetType().GetMethod("SetActive", new[] { typeof(bool) });
                setActive?.Invoke(go, new object[] { true });
            }
            catch { }
        }

        private static IEnumerator RunAuthCaptureCoroutine()
        {
            SetGameViewSize16x9(captureW, captureH);
            var report = new Report { startedUtc = DateTime.UtcNow.ToString("o") };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var marker = new PhaseMarker { label = "auth_capture", ok = true };
            report.actions.Add(marker);

            object authCtl = null, curStateProp = null, mvcManager = null;
            float findUntil = UnityEngine.Time.realtimeSinceStartup + 60f;
            while (authCtl == null && UnityEngine.Time.realtimeSinceStartup < findUntil)
            {
                try
                {
                    object loader = FindMainSceneLoader();
                    object dyn = loader != null ? GetPrivateField(loader, "dynamicWorldContainer") : null;
                    object mvc = dyn != null ? GetPublicProperty(dyn, "MvcManager") : null;
                    if (mvc != null) { mvcManager = mvc; authCtl = FindControllerByTypeName(mvc, "AuthenticationScreenController"); }
                    if (authCtl != null) curStateProp = GetMember(authCtl, "CurrentState");
                }
                catch { }
                if (authCtl != null) break;
                yield return null;
            }

            // Force-show the pre-world auth sub-screens (registry P06 verify / P07 web3confirm / P05 otp /
            // P04 lobbynew). A cached identity auto-continues to the world quickly, so do this AS SOON AS the
            // auth view is instantiated. These drivers render each sub-view LOCALLY — no real login, OTP send,
            // wallet signature, or profile publish. The natural login/lobby/profilefetching states are
            // captured by the observation loop below + prior runs.
            if (authCtl != null && mvcManager != null)
            {
                HideDebugPanel(GetPrivateField(FindMainSceneLoader(), "staticContainer"));   // hide dev DEBUG PANEL overlay
                object authView = null;
                float vUntil = UnityEngine.Time.realtimeSinceStartup + 25f;
                while (authView == null && UnityEngine.Time.realtimeSinceStartup < vUntil)
                {
                    authView = GetMember(authCtl, "viewInstance");
                    if (authView == null) yield return null;
                }
                if (authView != null)
                {
                    // staticContainer is loaded by now (bootstrap finished while we waited for the auth view), so
                    // HideDebugPanel sticks; call it before EACH capture (the dev panel re-asserts during bootstrap,
                    // which is why a single early call did not hold for the auth screens).
                    object sc = GetPrivateField(FindMainSceneLoader(), "staticContainer");
                    // Each capture: hide debug panel -> HideAuthSubViews (hard-isolate siblings) -> ActivateAuthSubView
                    // (re-activate ONLY the target view, since some Show()s don't) -> driver shows + captures.
                    HideDebugPanel(sc); HideAuthSubViews(authView); ActivateAuthSubView(authView, "LoginSelectionAuthView");
                    for (int k = 0; k < 6; k++) yield return null;
                    yield return AtlasCapture_login(mvcManager, null, null, null, report);

                    HideDebugPanel(sc); HideAuthSubViews(authView); ActivateAuthSubView(authView, "VerificationDappAuthView");
                    for (int k = 0; k < 6; k++) yield return null;
                    yield return AtlasCapture_verify(mvcManager, null, null, null, report);

                    HideDebugPanel(sc); HideAuthSubViews(authView); ActivateAuthSubView(authView, "VerificationDappAuthView");
                    for (int k = 0; k < 6; k++) yield return null;
                    yield return AtlasCapture_web3confirm(mvcManager, null, null, null, report);

                    HideDebugPanel(sc); HideAuthSubViews(authView); ActivateAuthSubView(authView, "VerificationOTPAuthView");
                    for (int k = 0; k < 6; k++) yield return null;
                    yield return AtlasCapture_otp(mvcManager, null, null, null, report);

                    HideDebugPanel(sc); HideAuthSubViews(authView); ActivateAuthSubView(authView, "LobbyForNewAccountAuthView");
                    for (int k = 0; k < 6; k++) yield return null;
                    yield return AtlasCapture_lobbynew(mvcManager, null, null, null, report);
                }
            }

            var seen = new System.Collections.Generic.HashSet<string>();
            string last = null;
            float endAt = UnityEngine.Time.realtimeSinceStartup + 100f;       // observe the flow ~100s
            float nextTimed = UnityEngine.Time.realtimeSinceStartup + 8f;
            int timedIdx = 0;
            object authSc = GetPrivateField(FindMainSceneLoader(), "staticContainer");
            while (UnityEngine.Time.realtimeSinceStartup < endAt)
            {
                HideDebugPanel(authSc);   // keep the dev DEBUG PANEL hidden for the natural-state captures too
                string st = null;
                try { object v = curStateProp != null ? GetMember(curStateProp, "Value") : null; st = v?.ToString(); } catch { }

                if (st != null && st != last)
                {
                    last = st;
                    if (seen.Add(st))
                    {
                        for (int i = 0; i < 18; i++) yield return null;
                        yield return CaptureShot("auth_" + st);
                        // Also save the key pre-world states under their registry route names (now debug-free)
                        // so P01-login / P03-lobby / P08-profilefetching refresh from the clean pipeline.
                        string mapped = st == "LoginSelectionScreen" ? "login"
                                      : st.IndexOf("ProfileFetching", StringComparison.OrdinalIgnoreCase) >= 0 ? "profilefetching"
                                      : st == "LoggedInCached" ? "lobby" : null;
                        if (mapped != null) yield return CaptureShot(mapped);
                    }
                }
                if (UnityEngine.Time.realtimeSinceStartup >= nextTimed && timedIdx < 8)
                {
                    nextTimed = UnityEngine.Time.realtimeSinceStartup + 12f;
                    yield return CaptureShot($"auth_t{(++timedIdx) * 12}s");
                }
                yield return null;
            }

            marker.error = authCtl != null
                ? "auth states seen: " + (seen.Count > 0 ? string.Join(",", seen) : "<none read>")
                : "AuthenticationScreenController not found via MVCManager (pre-in-world); timed shots only";
            report.reachedInteractive = false;
            Finish(report, sw);
        }

        // Hide the in-client DEBUG PANEL (the dev overlay docked on the right edge that occludes the right
        // ~20% of in-world shots). staticContainer.DebugContainerBuilder.IsVisible = false — the same toggle
        // the /debug chat command uses. Called once per capture session so all shots are clean.
        private static void HideDebugPanel(object staticContainer)
        {
            try
            {
                object dcb = staticContainer != null ? GetPublicProperty(staticContainer, "DebugContainerBuilder") : null;
                if (dcb == null) return;
                var prop = dcb.GetType().GetProperty("IsVisible", BindingFlags.Public | BindingFlags.Instance);
                if (prop != null && prop.CanWrite) prop.SetValue(dcb, false);
            }
            catch { }
        }

        // Public no-arg entry to hide the dev DEBUG PANEL overlay (the right-edge
        // panel) from a running session — exec-able via ClaudeIPC so the rig's
        // captures are clean BY DEFAULT (get-in-world / fidelity-tour call this
        // once the world is up). Must be in Play with the world booted.
        // [Mac-parity addition — keep in sync with the VM harness master.]
        public static void HideDebugPanel()
        {
            object sc = GetPrivateField(FindMainSceneLoader(), "staticContainer");
            if (sc == null) { Debug.LogWarning("[Harness] HideDebugPanel: staticContainer not found (in Play?)"); return; }
            HideDebugPanel(sc);
            Debug.Log("[Harness] debug panel hidden");
        }

        // Hide the transient rewards / notification popups — the toast panel that
        // shows "Weekly Goal Completed!" (MarketplaceCredits rewards), badges,
        // gifts, friend requests — so captures aren't cluttered by a popup that
        // happened to fire. Deactivates the NewNotificationPanel container (and any
        // RewardsHUD root) by name; the sidebar buttons are untouched. exec-able
        // via ClaudeIPC; logs how many roots it hid so the effect is transparent.
        // [Mac-parity addition — keep in sync with the VM harness master.]
        public static void HideRewardsPopup()
        {
            string[] needles = { "NewNotificationPanel", "RewardsHUD", "RewardsPopup", "MarketplaceCreditsMenu", "CreditsUnlocked" };
            int hidden = 0;
            foreach (var tr in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
            {
                if (tr == null) continue;
                var go = tr.gameObject;
                if (!go.activeSelf) continue;
                foreach (var needle in needles)
                {
                    if (go.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    go.SetActive(false); hidden++;
                    Debug.Log("[Harness] HideRewardsPopup: hid " + go.name);
                    break;
                }
            }
            Debug.Log($"[Harness] HideRewardsPopup: hid {hidden} popup root(s)");
        }

        // Hide the PERSISTENT in-world chat panel for isolated/clean-HUD captures.
        // The chat HUD (bottom-left "Type /help..." feed + "Press Enter to chat"
        // input bar) is a PERSISTENT MVC view, so IMVCManager.CloseAllNonPersistentViews
        // (used by CloseOpenPanels / the clean-HUD path) NEVER closes it (see the note in
        // AtlasCapture_chat). HideDebugPanel/HideRewardsPopup have analogous hide helpers;
        // this is the chat-panel equivalent. We do NOT close the MVC view (that would raise
        // focus/close-intent events) — we only DEACTIVATE the view's GameObject, a purely
        // visual, local, noise-free hide that leaves the controller registered.
        // Primary path: ChatMainSharedAreaController.viewInstance (the same handle the
        // inputsuggestions driver walks at lines ~4266-4272). Fallback: name scan, mirroring
        // HideRewardsPopup. Matches the reflection style of HideDebugPanel/HideRewardsPopup.
        // [Mac-parity addition — keep in sync with the VM harness master.]
        private static void HideChat(object mvcManager)
        {
            int hidden = 0;
            // Primary: reach the chat MVC view through its controller and deactivate it.
            try
            {
                object chatCtl = mvcManager != null ? FindControllerByTypeName(mvcManager, "ChatMainSharedAreaController") : null;
                object view = chatCtl != null ? GetMember(chatCtl, "viewInstance") : null;
                if (view is UnityEngine.MonoBehaviour mb && mb != null)
                {
                    var go = mb.gameObject;
                    if (go != null && go.activeSelf) { go.SetActive(false); hidden++; }
                }
            }
            catch { }

            // Fallback: name scan over active roots (mirrors HideRewardsPopup) in case the
            // controller isn't registered or the view handle wasn't reachable this frame.
            if (hidden == 0)
            {
                string[] needles = { "ChatMainSharedAreaView", "ChatPanelView" };
                foreach (var tr in UnityEngine.Object.FindObjectsByType<Transform>(FindObjectsInactive.Exclude))
                {
                    if (tr == null) continue;
                    var go = tr.gameObject;
                    if (!go.activeSelf) continue;
                    foreach (var needle in needles)
                    {
                        if (go.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        go.SetActive(false); hidden++;
                        break;
                    }
                }
            }
            Debug.Log($"[Harness] HideChat: hid {hidden} chat root(s)");
        }

        // =====================================================================
        //  ATLAS UI-SURFACE CAPTURE (mode "atlas")
        // =====================================================================
        // Reach interactive (Genesis realm, login skipped via cached identity), teleport to a remote
        // UNPOPULATED parcel so we stay in prod but make NO noise to other users, then drive each
        // AtlasCapture_<label> coroutine in sequence. Drivers are NON-GATING (they record into
        // report.actions and never abort the run) and capture-only (no chat/social/economic actions).
        private static IEnumerator RunAtlasCoroutine()
        {
            var report = new Report { startedUtc = DateTime.UtcNow.ToString("o") };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            shotIndex = 0;
            try
            {
                if (Directory.Exists(SHOTS_DIR)) Directory.Delete(SHOTS_DIR, true);
                Directory.CreateDirectory(SHOTS_DIR);
            }
            catch (Exception e) { Debug.LogWarning("[Harness] atlas: could not reset shots dir: " + e.Message); }

            for (int i = 0; i < 3 && EditorApplication.isPlaying; i++) yield return null;

            // Find MainSceneLoader.
            object mainSceneLoader = null;
            float findDeadline = UnityEngine.Time.realtimeSinceStartup + 30f;
            while (mainSceneLoader == null && UnityEngine.Time.realtimeSinceStartup < findDeadline)
            {
                mainSceneLoader = FindMainSceneLoader();
                if (mainSceneLoader == null) yield return null;
            }
            if (mainSceneLoader == null)
            {
                report.fatal = "atlas: Could not find MainSceneLoader.";
                Finish(report, sw); yield break;
            }

            // Wait for time-to-interactive (LoadingStage.Completed).
            object loadingStatus = null;
            float ttiStart = UnityEngine.Time.realtimeSinceStartup;
            float ttiDeadline = ttiStart + LOAD_TIMEOUT_S;
            bool reachedInteractive = false;
            while (UnityEngine.Time.realtimeSinceStartup < ttiDeadline)
            {
                if (loadingStatus == null)
                {
                    var sc = GetPrivateField(mainSceneLoader, "staticContainer");
                    if (sc != null) loadingStatus = GetPublicProperty(sc, "LoadingStatus");
                }
                if (loadingStatus != null)
                {
                    string stage = ReadLoadingStage(loadingStatus);
                    report.lastLoadingStage = stage;
                    if (stage == "Completed") { reachedInteractive = true; break; }
                }
                yield return null;
            }
            report.reachedInteractive = reachedInteractive;
            report.timeToInteractiveSeconds = reachedInteractive ? UnityEngine.Time.realtimeSinceStartup - ttiStart : -1f;
            Debug.Log($"[Harness] atlas TTI={report.timeToInteractiveSeconds:F2}s reached={reachedInteractive}");

            object dynamicContainer = GetPrivateField(mainSceneLoader, "dynamicWorldContainer");
            object staticContainer2 = GetPrivateField(mainSceneLoader, "staticContainer");
            object realmNavigator   = dynamicContainer != null ? GetPublicProperty(dynamicContainer, "RealmNavigator") : null;
            object mvcManager       = dynamicContainer != null ? GetPublicProperty(dynamicContainer, "MvcManager") : null;
            report.foundRealmNavigator = realmNavigator != null;

            SetGameViewSize16x9(captureW, captureH);
            HideDebugPanel(staticContainer2);   // hide the dev DEBUG PANEL overlay (right-edge) for all shots

            if (!reachedInteractive || mvcManager == null)
            {
                report.fatal = "atlas: not interactive or no MvcManager; aborting (reached=" + reachedInteractive + ", mvc=" + (mvcManager != null) + ")";
                Finish(report, sw); yield break;
            }

            // One-shot parcel override: C:\Users\dcl\atlas-parcel.txt = "X,Y" teleports there instead of the
            // quiet parcel (used to spawn at Genesis Plaza 0,0 for live-chat capture). Deleted after reading.
            int extraSettle = 0;
            try
            {
                const string parcelFile = @"C:\Users\dcl\atlas-parcel.txt";
                if (File.Exists(parcelFile))
                {
                    string[] xy = File.ReadAllText(parcelFile).Trim().Trim('﻿').Split(',');
                    if (xy.Length == 2 && int.TryParse(xy[0], out int px) && int.TryParse(xy[1], out int py))
                    { atlasParcel = new Vector2Int(px, py); extraSettle = 600; }   // +10s to let live chat activity arrive
                    File.Delete(parcelFile);
                }
            }
            catch (Exception e) { Debug.LogWarning("[Harness] atlas-parcel read failed: " + e.Message); }

            // Teleport to the capture parcel (quiet by default; overridable to Genesis Plaza for live-chat shots).
            if (realmNavigator != null)
            {
                var noiseMark = new PhaseMarker { label = "atlas_teleport_quiet", ok = true };
                report.actions.Add(noiseMark);
                bool tok = TryTeleport(realmNavigator, atlasParcel, out string terr);
                noiseMark.error = tok ? ("to " + atlasParcel.x + "," + atlasParcel.y) : ("teleport failed: " + terr);
                // Let the destination scene stream in so the world behind overlays isn't a black void.
                // extraSettle adds time at Genesis Plaza so live chat activity from other users arrives.
                for (int i = 0; i < 240 + extraSettle && tok; i++) yield return null;
            }

            // A world-facing shot at the quiet parcel (context / sanity that we're in-world).
            if (mvcManager != null) yield return CloseOpenPanels(mvcManager);  // close auto-popups (Weekly Rewards welcome) before the base world shot
            yield return CaptureShot("atlas_world_quiet");

            // ----- Driver dispatch list ---------------------------------------
            // Each entry is (label, coroutine). Coroutines are lazy (allocated here, executed when yielded).
            var atlasDrivers = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, IEnumerator>>();
            // ATLAS_DRIVERS_BEGIN
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("explore", AtlasCapture_explore(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // E01
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("settings", RunRoute(Folded("settings"), mvcManager, report)));  // E02
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("places", AtlasCapture_places(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // E03
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("events", AtlasCapture_events(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // E04
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("map", RunRoute(Folded("map"), mvcManager, report)));  // E05
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("backpack", RunRoute(Folded("backpack"), mvcManager, report)));  // E06
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("backpackemotes", AtlasCapture_backpackemotes(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // E07
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("backpackoutfits", AtlasCapture_backpackoutfits(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // E08
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("placedetail", AtlasCapture_placedetail(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // E09
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("eventdetail", AtlasCapture_eventdetail(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // E10
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("navigation", AtlasCapture_navigation(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // E11
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("iteminfo", AtlasCapture_iteminfo(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // E12
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("mapfilters", AtlasCapture_mapfilters(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // E13
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("sidebar", AtlasCapture_sidebar(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // H01
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("minimap", AtlasCapture_minimap(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // H02
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("chat", AtlasCapture_chat(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // H03
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("notifications", AtlasCapture_notifications(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // H04
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("voice", RunRoute(Folded("voice"), mvcManager, report)));  // H05
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("connectionstatus", AtlasCapture_connectionstatus(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // H06
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("profilewidget", RunRoute(Folded("profilewidget"), mvcManager, report)));  // H07
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("chatprofile", AtlasCapture_chatprofile(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // H11
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("marketplace", RunRoute(Folded("marketplace"), mvcManager, report)));  // M01
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("donations", AtlasCapture_donations(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // M02
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("camera", AtlasCapture_camera(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // M03
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("reel", AtlasCapture_reel(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // M04
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("photo", AtlasCapture_photo(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // M05
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("gifting", AtlasCapture_gifting(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // M06
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("creditsunlocked", AtlasCapture_creditsunlocked(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // M07
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("creditsstates", AtlasCapture_creditsstates(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // M08
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("emotewheel", RunRoute(Folded("emotewheel"), mvcManager, report)));  // O01
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("skybox", RunRoute(Folded("skybox"), mvcManager, report)));  // O03
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("teleportprompt", AtlasCapture_teleportprompt(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // O04
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("nftprompt", AtlasCapture_nftprompt(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // O05
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("reward", AtlasCapture_reward(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // O06
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("privateworlds", AtlasCapture_privateworlds(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // O07
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("smartwearables", AtlasCapture_smartwearables(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // O08
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("errorpopup", AtlasCapture_errorpopup(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // O09
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("controls", RunRoute(Folded("controls"), mvcManager, report)));  // O10
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("loading", AtlasCapture_loading(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // P02
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("sceneloading", AtlasCapture_sceneloading(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // P09
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("minspecs", AtlasCapture_minspecs(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // P10
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("updaterequired", AtlasCapture_updaterequired(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // P11
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("connectionerror", AtlasCapture_connectionerror(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // P12
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("friends", RunRoute(Folded("friends"), mvcManager, report)));  // S01
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("communities", AtlasCapture_communities(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // S02
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("passport", AtlasCapture_passport(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // S03
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("communitycreate", AtlasCapture_communitycreate(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // S04
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("createcommunity", AtlasCapture_createcommunity(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // S04b
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("badgesdetail", AtlasCapture_badgesdetail(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // S05
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("communitycard", AtlasCapture_communitycard(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // S06
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("friendactions", AtlasCapture_friendactions(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // S07
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("communitymembers", AtlasCapture_communitymembers(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // S08
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("communitycontent", AtlasCapture_communitycontent(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // S09
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("passportphotos", AtlasCapture_passportphotos(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // S10
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("addlink", AtlasCapture_addlink(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // S11
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("broadcast", AtlasCapture_broadcast(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // S12b
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("contextmenu", AtlasCapture_contextmenu(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // X01
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("confirm", AtlasCapture_confirm(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // X02
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("gallery", AtlasCapture_gallery(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // X03
            // chat-family
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("inputsuggestions", AtlasCapture_inputsuggestions(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // H08
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("chatwindow", AtlasCapture_chatwindow(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // H09
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("reactions", AtlasCapture_reactions(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // H10
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("emoji", AtlasCapture_emoji(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // O02
            // tail LAST
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("communitystream", AtlasCapture_communitystream(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // S12
            atlasDrivers.Add(new System.Collections.Generic.KeyValuePair<string, IEnumerator>("duplicateidentity", AtlasCapture_duplicateidentity(mvcManager, staticContainer2, dynamicContainer, realmNavigator, report)));  // P13
            // ATLAS_DRIVERS_END

            // One-shot SUBSET filter for fast per-driver iteration: if C:\Users\dcl\atlas-only.txt exists,
            // only the listed routes (comma/space/newline-separated) are captured. The file is DELETED after
            // reading so it never affects a subsequent full run.
            var atlasOnly = new System.Collections.Generic.HashSet<string>();
            try
            {
                string onlyFile = Environment.GetEnvironmentVariable("DCL_ATLAS_ONLY") ?? Path.Combine(ATLAS_DIR, "atlas-only.txt");
                if (File.Exists(onlyFile))
                {
                    foreach (var tok in File.ReadAllText(onlyFile).Split(new[] { ',', ' ', '\n', '\r', '\t', '﻿' }, StringSplitOptions.RemoveEmptyEntries))
                        atlasOnly.Add(tok.Trim().Trim('﻿'));
                    File.Delete(onlyFile);
                }
            }
            catch (Exception e) { Debug.LogWarning("[Harness] atlas-only read failed: " + e.Message); }
            // [Mac-parity] also accept a direct env list (DCL_ATLAS_ONLY="places map ...") — the Windows
            // atlas-only.txt path above doesn't exist on Mac/Linux, so the env is the portable way to run a
            // subset (fast per-route re-verification). Merges with anything the file provided.
            try
            {
                var envOnly = Environment.GetEnvironmentVariable("DCL_ATLAS_ONLY");
                if (!string.IsNullOrEmpty(envOnly))
                    foreach (var tok in envOnly.Split(new[] { ',', ' ', '\n', '\r', '\t', '﻿' }, StringSplitOptions.RemoveEmptyEntries))
                        atlasOnly.Add(tok.Trim().Trim('﻿'));
            }
            catch (Exception e) { Debug.LogWarning("[Harness] DCL_ATLAS_ONLY read failed: " + e.Message); }
            // The IPC runtime override (SetAtlasOnly) is AUTHORITATIVE when it was called: it REPLACES the
            // env/file subset entirely (empty override -> full run). This lets the launcher force a true full run
            // on a reused editor whose stale launch-time DCL_ATLAS_ONLY env would otherwise pin the old subset.
            if (atlasOnlyOverrideSet) { atlasOnly.Clear(); foreach (var k in atlasOnlyOverride) atlasOnly.Add(k); }

            Debug.Log($"[Harness] atlas: {atlasDrivers.Count} drivers queued"
                      + (atlasOnly.Count > 0 ? "; SUBSET -> " + string.Join(",", atlasOnly) : " (full)"));
            // MUTATING drivers (real create / go-live) run ONLY when explicitly named in the subset filter —
            // never in a full run — so repeated captures don't re-create communities or re-broadcast.
            var mutatingDrivers = new System.Collections.Generic.HashSet<string> { "createcommunity", "broadcast" };
            foreach (var kv in atlasDrivers)
            {
                if (atlasOnly.Count > 0 && !atlasOnly.Contains(kv.Key)) continue;
                if (mutatingDrivers.Contains(kv.Key) && !atlasOnly.Contains(kv.Key)) continue;   // explicit opt-in only
                Debug.Log("[Harness] atlas driver -> " + kv.Key);
                yield return kv.Value;
                yield return CloseOpenPanels(mvcManager);
                for (int i = 0; i < 8; i++) yield return null;   // settle between panels
            }

            int shown = 0, other = 0;
            foreach (var a in report.actions)
            {
                if (!a.label.StartsWith("atlas_")) continue;
                // folded routes set a real ok (VerifyShown); still-custom routes use the "shown" sentinel
                bool good = a.ok || a.error == "shown" || (a.error != null && a.error.StartsWith("skipped:"));
                if (good) shown++; else other++;
            }
            Debug.Log($"[Harness] atlas done: shown/ok={shown} other={other}");
            Finish(report, sw);
        }

        private static IEnumerator RunSessionCoroutine()
        {
            var report = new Report { startedUtc = DateTime.UtcNow.ToString("o") };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            // Fresh screenshot folder for this session (steer 2026-06-13: capture
            // a before/after screenshot around each action so the run is visually
            // reviewable). Each CaptureShot writes NNN_<label>.png in capture order.
            shotIndex = 0;
            try
            {
                if (Directory.Exists(SHOTS_DIR)) Directory.Delete(SHOTS_DIR, true);
                Directory.CreateDirectory(SHOTS_DIR);
            }
            catch (Exception e) { Debug.LogWarning("[Harness] could not reset shots dir: " + e.Message); }

            // --- 1. Already in Play mode (started from OnPlayModeStateChanged after
            //        the edit->play domain reload). Give Awake/bootstrap a few frames.
            for (int i = 0; i < 3 && EditorApplication.isPlaying; i++) yield return null;

            // --- 2. Locate the running MainSceneLoader & its containers ------
            object mainSceneLoader = null;
            float findDeadline = UnityEngine.Time.realtimeSinceStartup + 30f;
            while (mainSceneLoader == null && UnityEngine.Time.realtimeSinceStartup < findDeadline)
            {
                mainSceneLoader = FindMainSceneLoader();
                if (mainSceneLoader == null) yield return null;
            }
            if (mainSceneLoader == null)
            {
                report.fatal = "Could not find MainSceneLoader MonoBehaviour in the scene.";
                Finish(report, sw); yield break;
            }

            // --- 3. Wait for time-to-interactive (LoadingStage.Completed) -----
            // ILoadingStatus.CurrentStage is a ReactiveProperty<LoadingStage>;
            // Completed == 7. We poll .Value via reflection.
            object loadingStatus = null;
            float ttiStart = UnityEngine.Time.realtimeSinceStartup;
            float ttiDeadline = ttiStart + LOAD_TIMEOUT_S;
            bool reachedInteractive = false;

            // staticContainer may be null for the first ~seconds of bootstrap.
            while (UnityEngine.Time.realtimeSinceStartup < ttiDeadline)
            {
                if (loadingStatus == null)
                {
                    var staticContainer = GetPrivateField(mainSceneLoader, "staticContainer");
                    if (staticContainer != null)
                        loadingStatus = GetPublicProperty(staticContainer, "LoadingStatus"); // StaticContainer.LoadingStatus
                }

                if (loadingStatus != null)
                {
                    string stage = ReadLoadingStage(loadingStatus);   // e.g. "Completed"
                    report.lastLoadingStage = stage;
                    if (stage == "Completed") { reachedInteractive = true; break; }
                }
                yield return null;
            }
            report.timeToInteractiveSeconds = reachedInteractive
                ? UnityEngine.Time.realtimeSinceStartup - ttiStart
                : -1f; // timed out
            report.reachedInteractive = reachedInteractive;
            Debug.Log($"[Harness] TTI = {report.timeToInteractiveSeconds:F2}s reached={reachedInteractive}");

            // Grab the dynamic container services now that we're loaded.
            object dynamicContainer = GetPrivateField(mainSceneLoader, "dynamicWorldContainer");
            object staticContainer2 = GetPrivateField(mainSceneLoader, "staticContainer");
            object realmNavigator   = dynamicContainer != null ? GetPublicProperty(dynamicContainer, "RealmNavigator") : null;
            object chatBus          = ReachChatBus(dynamicContainer);
            object profiler         = staticContainer2 != null ? GetPublicProperty(staticContainer2, "Profiler") : null;

            report.foundRealmNavigator = realmNavigator != null;
            report.foundChatBus        = chatBus != null;
            report.foundProfiler       = profiler != null;
            SchemaCheck(mainSceneLoader, staticContainer2, dynamicContainer);   // loud, named drift report

            // --- 4. Baseline perf sample at spawn ----------------------------
            SetGameViewSize16x9(captureW, captureH);             // force ~16:9 screenshots (2026-06-16)
            yield return SamplePhase("spawn", report, SAMPLE_SECONDS);

            // World-facing screenshot at the Genesis Plaza spawn (2026-06-18, user request): no panel open,
            // so this captures the rendered 3D world — the GP building cluster around (-3,-2) where the
            // Building_0X_Window content-residual lives. Lets us SEE the missing-window content gap.
            if (reachedInteractive) yield return CaptureShot("world_genesis");

            // --- 4b. Avatar visual check on the world avatar(s) ---------------
            if (reachedInteractive)
                yield return CheckAvatars("avatar_main", report, previewOnly: false);

            // --- 5. Teleport sequence (+ perf sample after each) -------------
            if (reachedInteractive && realmNavigator != null)
            {
                foreach (var parcel in TELEPORT_TARGETS)
                {
                    bool ok = TryTeleport(realmNavigator, parcel, out string tperr);
                    var pe = new PhaseMarker { label = $"teleport_{parcel.x}_{parcel.y}", ok = ok, error = tperr };
                    report.actions.Add(pe);
                    // allow streaming to settle, then sample
                    float until = UnityEngine.Time.realtimeSinceStartup + SETTLE_AFTER_TP;
                    while (UnityEngine.Time.realtimeSinceStartup < until) yield return null;
                    yield return SamplePhase($"after_tp_{parcel.x}_{parcel.y}", report, SAMPLE_SECONDS);

                    // Behavioral probe (2026-06-17, user steer "expand scope"): after a teleport to a
                    // NON-ORIGIN parcel, confirm the local player actually moved there and is NOT stuck
                    // at world origin / (0,0) — the "0,0 avatar flash" bug class. Read-only (reads
                    // CharacterObject.Position). PROMOTED TO GATING 2026-06-17 for the STUCK-AT-0,0
                    // condition ONLY (held across many clean ticks; a non-origin teleport never
                    // legitimately lands at 0,0, so this only trips on a genuine failed-teleport
                    // regression). pos-unreadable and off-by stay NON-FATAL notes (env/spawn-point
                    // variance — e.g. (74,-9) lands at the GP spawn ~82,-1, which is expected).
                    if (ok && (parcel.x != 0 || parcel.y != 0))
                    {
                        var ppm = new PhaseMarker { label = $"playerpos_{parcel.x}_{parcel.y}", ok = true };
                        if (!TryGetPlayerParcel(staticContainer2, out Vector2Int at, out string pperr))
                            ppm.error = "expand(non-gating): pos-unreadable: " + pperr;
                        else if (at == Vector2Int.zero)
                        {
                            ppm.ok = false; // GATING: genuine stuck-at-origin teleport failure
                            ppm.error = $"STUCK-AT-0,0 after teleport (expected ~{parcel.x},{parcel.y}) — 0,0 avatar-flash regression";
                        }
                        else
                        {
                            int dx = Mathf.Abs(at.x - parcel.x), dy = Mathf.Abs(at.y - parcel.y);
                            ppm.error = (dx <= 1 && dy <= 1)
                                ? $"expand(non-gating): at {at.x},{at.y} OK"
                                : $"expand(non-gating): at {at.x},{at.y} (expected ~{parcel.x},{parcel.y}; off {dx},{dy})";
                        }
                        report.actions.Add(ppm);
                    }
                }
            }

            // --- 5b. GAME teleport (2026-06-18, user steer "different worlds and games") --------------
            // Teleport into a real GAME scene from the live census (Antrom RPG / Dice Masters @144,-7),
            // give it extra settle (games are heavier), capture a world screenshot, and verify the scene
            // actually loaded (ScenesCache.CurrentScene + IsSceneReady). NON-GATING.
            if (reachedInteractive && realmNavigator != null)
            {
                var gm = new PhaseMarker { label = "game_144_-7", ok = true };
                report.actions.Add(gm);
                bool gok = TryTeleport(realmNavigator, new Vector2Int(144, -7), out string gerr);
                float gu = UnityEngine.Time.realtimeSinceStartup + 12f;   // games stream more; extra settle
                while (UnityEngine.Time.realtimeSinceStartup < gu) yield return null;
                yield return CaptureShot("world_game_antrom_144_-7");
                if (!gok) gm.error = "expand(non-gating): teleport-failed: " + gerr;
                else if (!TryGetSceneLoaded(staticContainer2, out string sname, out bool ready, out string serr))
                    gm.error = "expand(non-gating): scene-unreadable: " + serr;
                else gm.error = $"expand(non-gating): scene='{sname}' ready={ready}";
            }

            // --- 6. Send a chat line -----------------------------------------
            if (chatBus != null)
            {
                bool ok = TrySendChat(chatBus, "Harness automated session check " +
                                              DateTime.UtcNow.ToString("HH:mm:ss"), out string cerr);
                report.actions.Add(new PhaseMarker { label = "chat_send", ok = ok, error = cerr });
                // Also exercise the /goto chat command path (routes through the
                // command handler -> ChatTeleporter -> RealmNavigator):
                bool ok2 = TrySendChat(chatBus, "/goto 0,0", out string cerr2);
                report.actions.Add(new PhaseMarker { label = "chat_goto", ok = ok2, error = cerr2 });
                yield return SamplePhase("after_chat", report, 5f);
            }

            // --- 7. UI panel phases --------------------------------------------
            // Opens panels via the same MVC path the input shortcuts use
            // (XController.IssueCommand -> IMVCManager.ShowAsync) and samples errors/perf
            // with each panel open. Sections switch in-place; play-mode exit tears down.
            object mvcManager = dynamicContainer != null ? GetPublicProperty(dynamicContainer, "MvcManager") : null;

            if (reachedInteractive && mvcManager != null)
            {
                // 7a. Explore panel sections: backpack, map, camera reel, communities, places, events
                string[] uiSections = { "Backpack", "Navmap", "CameraReel", "Communities", "Places", "Events" };

                foreach (string section in uiSections)
                {
                    yield return CloseOpenPanels(mvcManager); // ensure the prior section is gone so this one actually opens
                    bool ok = TryOpenExplorePanel(mvcManager, section, null, out string uierr);
                    var marker = new PhaseMarker { label = "open_" + section.ToLowerInvariant(), ok = ok, error = uierr };
                    report.actions.Add(marker);
                    yield return SamplePhase("panel_" + section.ToLowerInvariant(), report, 10f);
                    if (ok && !VerifyShown(mvcManager, lastPanelKey, out string rerr))
                    { marker.ok = false; marker.error = (marker.error == null ? "" : marker.error + "; ") + "render: " + rerr; }

                    // The Backpack hosts the character-preview avatar - the same
                    // CharacterPreviewController path the new-user onboarding preview
                    // uses (the auth screen itself is unreachable here: the cached
                    // identity skips it). Verify it visually animates.
                    if (section == "Backpack")
                        yield return CheckAvatars("avatar_preview", report, previewOnly: true);
                }

                // 7b. Every settings tab
                string[] settingsTabs = { "GENERAL", "GRAPHICS", "SOUND", "CONTROLS", "CHAT" };

                foreach (string tab in settingsTabs)
                {
                    yield return CloseOpenPanels(mvcManager);
                    bool ok = TryOpenExplorePanel(mvcManager, "Settings", tab, out string uierr);
                    var marker = new PhaseMarker { label = "settings_" + tab.ToLowerInvariant(), ok = ok, error = uierr };
                    report.actions.Add(marker);
                    yield return SamplePhase("settings_" + tab.ToLowerInvariant(), report, 6f);
                    if (ok && !VerifyShown(mvcManager, lastPanelKey, out string rerr))
                    { marker.ok = false; marker.error = (marker.error == null ? "" : marker.error + "; ") + "render: " + rerr; }
                }

                // 7c. Friends panel tabs (exercises the friends-list/requests API fetch + UI;
                //     sending an actual DM needs a friend relationship - not automated yet)
                string[] friendTabs = { "FRIENDS", "REQUESTS" };

                foreach (string tab in friendTabs)
                {
                    yield return CloseOpenPanels(mvcManager);
                    bool ok = TryOpenFriendsPanel(mvcManager, tab, out string ferr);
                    var marker = new PhaseMarker { label = "friends_" + tab.ToLowerInvariant(), ok = ok, error = ferr };
                    report.actions.Add(marker);
                    yield return SamplePhase("friends_" + tab.ToLowerInvariant(), report, 8f);
                    if (ok && !VerifyShown(mvcManager, lastPanelKey, out string rerr))
                    {
                        // ENV-LIMITED (2026-06-16): the FriendsPanel is fullscreen and needs
                        // social-service/requests data the Evaristo account lacks; when that fetch
                        // is empty/throws, the view renders nothing and reports ViewHidden. The
                        // panel OPENED fine (ok==true) -> this is missing data, not a client
                        // regression, so a render-not-shown here is a NON-FATAL env note rather
                        // than a hard fail. A real friends bug still surfaces via the open-failure
                        // path (ok==false above) and the regression-error greps. (item-4 RESIDUAL)
                        marker.error = (marker.error == null ? "" : marker.error + "; ") + "env-limited render: " + rerr;
                    }
                }

                // 7e. EXPANDED surface coverage (2026-06-17, user steer "expand scope"). Added
                // NON-GATING while being validated: we open additional panels, screenshot them, and
                // record the render outcome in the marker note, but keep ok=true so a not-yet-reliable
                // probe can't fail the sentinel. Promote to gating (ok=opened&&shown) once a panel
                // proves reliable across ticks. The regression-error greps still apply to these phases.
                // Columns: { label, controllerFullName, paramTypeNameOrNull }. A non-null param-type
                // (use '+' for nested types) is DEFAULT-constructed (struct defaults) to drive the
                // 1-arg IssueCommand path; null drives the 0-arg path. MarketplaceCredits opens with
                // a default Params (isOpenedFromNotification=false) = no external data dependency, so
                // it is a self-contained surface to validate the 1-arg path.
                string[][] extraPanels =
                {
                    new[] { "notifications", "DCL.Notifications.NotificationsMenu.NotificationsPanelController", null },
                    new[] { "marketplacecredits", "DCL.MarketplaceCredits.MarketplaceCreditsMenuController", "DCL.MarketplaceCredits.MarketplaceCreditsMenuController+Params" },
                    // ATLAS Batch-1 (2026-06-19): 0-arg / default-param MVC controllers reachable via the same
                    // IssueCommand->ShowAsync path. From the screenshot-atlas research workflow (recipes in
                    // harness/atlas-recipes/). All NON-GATING.
                    new[] { "explore", "DCL.ExplorePanel.ExplorePanelController", "DCL.ExplorePanel.ExplorePanelParameter" },
                    new[] { "profilewidget", "DCL.UI.Profiles.ProfileMenuController", null },
                    new[] { "emotewheel", "DCL.EmotesWheel.EmotesWheelController", null },
                    new[] { "skybox", "DCL.UI.Skybox.SkyboxMenuController", null },
                };
                foreach (string[] p in extraPanels)
                {
                    yield return CloseOpenPanels(mvcManager);
                    object xparam = null;
                    string xerr = null;
                    if (p[2] != null)
                    {
                        Type pt = FindType(p[2]);
                        if (pt == null) xerr = "param type not found: " + p[2];
                        else { try { xparam = Activator.CreateInstance(pt); } catch (Exception pe) { xerr = "param ctor failed: " + pe.Message; } }
                    }
                    bool opened = xerr == null && TryShowPanelByName(mvcManager, p[1], xparam, out xerr);
                    var marker = new PhaseMarker { label = "panel_" + p[0], ok = true }; // non-gating
                    report.actions.Add(marker);
                    yield return SamplePhase("panel_" + p[0], report, 6f);
                    if (!opened) marker.error = "expand(non-gating): open-failed: " + xerr;
                    else if (!VerifyShown(mvcManager, lastPanelKey, out string rerr)) marker.error = "expand(non-gating): not-shown: " + rerr;
                    else marker.error = "expand(non-gating): shown OK";
                }

                // 7f. CONTENT ENUMERATION (2026-06-17, user steer "what places/events/games are there").
                // NON-GATING: query the live Events + Places APIs (Places filtered by category="game"
                // for mini-games) via reflection and record counts + sample names/coords as notes.
                // Read-only; wrapped + timeout-bounded so a network hiccup never fails the session.
                yield return EnumerateContent(mvcManager, report);

                // 7g. WORLD realm switch (2026-06-18, user steer "different worlds and games"): pull a live
                // World name from the Places API, TryChangeRealmAsync(isWorld:true) into it, verify the realm
                // became a World, and capture a world screenshot. NON-GATING + bounded (AwaitUniTask 15s).
                // Done LAST so the remote-realm switch can't pollute the Genesis-realm panels/census above.
                yield return SwitchToWorldAndShoot(mvcManager, staticContainer2, realmNavigator, report);

                // 7d. Voice chat: NOT yet automated. The nearby-voice mic toggle lives in
                // DI-held services (NearbyMicrophoneHandler / VoiceChatOrchestrator threaded into
                // plugins, no MVC command, no static access), and this VM has no audio device
                // (FMOD reports none), so activation would fail environmentally anyway.
                // Follow-up hook: walk dynamicContainer -> plugin graph -> voiceChatOrchestrator.
            }

            // --- 8. Done -----------------------------------------------------
            Finish(report, sw);
        }

        // =====================================================================
        //  PERF SAMPLING  (mirrors DCL.Profiling.Profiler counter set)
        // =====================================================================
        private static IEnumerator SamplePhase(string label, Report report, float seconds)
        {
            // We open our OWN ProfilerRecorders (independent of the game's) so we
            // don't disturb its sampling. Counter (category,name) pairs verified
            // against DCL.Profiling.Profiler; render counters are standard Unity.
            var mainThread = new ProfilerRecorder(ProfilerCategory.Internal, "Main Thread", 1024);
            var gpuFrame   = new ProfilerRecorder(ProfilerCategory.Render,   "GPU Frame Time", 1024);
            var gcAlloc    = new ProfilerRecorder(ProfilerCategory.Memory,   "GC Allocated In Frame", 1024);
            var sysMem     = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "System Used Memory");
            // Render counters: standard Unity ProfilerCategory.Render names. An unavailable counter
            // yields .Valid==false and is emitted as -1 (MISSING), never silently 0; the loud
            // 0-sample check after the loop surfaces a counter that produced nothing.
            var drawCalls  = new ProfilerRecorder(ProfilerCategory.Render, "Draw Calls Count", 1024);
            var batches    = new ProfilerRecorder(ProfilerCategory.Render, "Batches Count", 1024);
            var setPass    = new ProfilerRecorder(ProfilerCategory.Render, "SetPass Calls Count", 1024);
            var tris       = new ProfilerRecorder(ProfilerCategory.Render, "Triangles Count", 1024);

            mainThread.Start(); gpuFrame.Start(); gcAlloc.Start();
            drawCalls.Start(); batches.Start(); setPass.Start(); tris.Start();

            var cpuMs = new List<double>();
            var gpuMs = new List<double>();
            double gcSum = 0;
            long hiccups = 0;            // frames > 50ms (matches Profiler HICCUP_THRESHOLD)
            int frames = 0;
            float end = UnityEngine.Time.realtimeSinceStartup + seconds;

            while (UnityEngine.Time.realtimeSinceStartup < end && EditorApplication.isPlaying)
            {
                if (mainThread.Valid && mainThread.LastValue > 0)
                {
                    double ms = mainThread.LastValue * 1e-6;
                    cpuMs.Add(ms);
                    if (mainThread.LastValue > 50_000_000) hiccups++;
                }
                if (gpuFrame.Valid && gpuFrame.LastValue > 0) gpuMs.Add(gpuFrame.LastValue * 1e-6);
                if (gcAlloc.Valid) gcSum += gcAlloc.LastValue;
                frames++;
                yield return null;
            }

            if (cpuMs.Count == 0) Debug.LogError($"[Harness] phase '{label}': 0 CPU samples — 'Main Thread' counter unavailable; cpu/fps are MISSING, not zero.");
            if (gpuMs.Count == 0) Debug.LogWarning($"[Harness] phase '{label}': 0 GPU samples — 'GPU Frame Time' unavailable (common in-Editor); gpu is MISSING, not zero.");

            var phase = new PhaseMetrics
            {
                label              = label,
                frames             = frames,
                durationSeconds    = seconds,
                cpuMsAvg           = Avg(cpuMs),
                cpuMsP99Worst      = PercentWorst(cpuMs, 0.01),
                cpuMsMax           = cpuMs.Count > 0 ? cpuMs.Max() : 0,
                fpsAvg             = cpuMs.Count > 0 ? 1000.0 / Avg(cpuMs) : 0,
                gpuMsAvg           = Avg(gpuMs),
                gpuMsMax           = gpuMs.Count > 0 ? gpuMs.Max() : 0,
                hiccupFramesOver50ms = hiccups,
                gcAllocBytesTotal  = gcSum,
                systemUsedMemoryMB = sysMem.Valid ? sysMem.LastValue / (1024.0 * 1024.0) : 0,
                drawCallsLast      = drawCalls.Valid ? drawCalls.LastValue : -1,
                batchesLast        = batches.Valid ? batches.LastValue : -1,
                setPassLast        = setPass.Valid ? setPass.LastValue : -1,
                trianglesLast      = tris.Valid ? tris.LastValue : -1,
            };
            report.phases.Add(phase);
            Debug.Log($"[Harness] phase '{label}': fps~{phase.fpsAvg:F1} cpuAvg={phase.cpuMsAvg:F2}ms hiccups={hiccups} draws={phase.drawCallsLast}");

            mainThread.Dispose(); gpuFrame.Dispose(); gcAlloc.Dispose(); sysMem.Dispose();
            drawCalls.Dispose(); batches.Dispose(); setPass.Dispose(); tris.Dispose();

            // "after" screenshot: the settled state once the action's sample window
            // has elapsed (scene streamed in / panel fully shown). Some phase labels
            // already carry an "after_" prefix (teleport/chat) - strip it so we don't
            // double up to "after_after_*".
            // CAPTURE POLICY (2026-06-16, user steer): only the panel/settings/friends
            // settled states are kept (the false-pass-bug class the render assertion
            // guards). spawn/teleport/chat shots dropped as low-value; avatars are
            // captured separately in CheckAvatars. ~15 shots/session instead of 38.
            string baseLabel = label.StartsWith("after_") ? label.Substring("after_".Length) : label;
            if (baseLabel.StartsWith("panel_") || baseLabel.StartsWith("settings_") || baseLabel.StartsWith("friends_"))
                yield return CaptureShot("after_" + baseLabel);
        }

        // =====================================================================
        //  CLIENT API CALLS (via reflection)
        // =====================================================================

        // Reach the chat bus through the CURRENT container shape: DynamicWorldContainer holds a
        // private `chatContainer` (ChatContainer) whose public `ChatMessagesBus` is the bus. The old
        // direct `chatMessagesBus` field no longer exists; the legacy name is kept as a fallback.
        private static object ReachChatBus(object dynamicContainer)
        {
            if (dynamicContainer == null) return null;
            object chatContainer = GetPrivateField(dynamicContainer, "chatContainer");
            object bus = chatContainer != null ? GetMember(chatContainer, "ChatMessagesBus") : null;
            if (bus == null) bus = GetMember(dynamicContainer, "chatMessagesBus");   // legacy fallback
            return bus;
        }

        // One-time schema check: resolve every reflected member the harness depends on and emit a
        // LOUD, NAMED error for any that no longer resolves, so a client rename is reported as exactly
        // that — not as a downstream null / generic load timeout. Diagnostic only (never aborts a run).
        private static void SchemaCheck(object loader, object staticContainer, object dynamicContainer)
        {
            var missing = new List<string>();
            void Req(bool ok, string what) { if (!ok) missing.Add(what); }

            Req(loader != null, "MainSceneLoader");
            Req(staticContainer != null, "MainSceneLoader.staticContainer");
            Req(dynamicContainer != null, "MainSceneLoader.dynamicWorldContainer");
            object mvc = dynamicContainer != null ? GetPublicProperty(dynamicContainer, "MvcManager") : null;
            object nav = dynamicContainer != null ? GetPublicProperty(dynamicContainer, "RealmNavigator") : null;
            object ls  = staticContainer  != null ? GetPublicProperty(staticContainer, "LoadingStatus") : null;
            Req(mvc != null, "DynamicWorldContainer.MvcManager");
            Req(nav != null, "DynamicWorldContainer.RealmNavigator");
            Req(ls  != null, "StaticContainer.LoadingStatus");
            if (nav != null)
                Req(nav.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(m => m.Name == "TeleportToParcelAsync"), "IRealmNavigator.TeleportToParcelAsync");
            Req(ReachChatBus(dynamicContainer) != null, "DynamicWorldContainer.chatContainer.ChatMessagesBus");
            Req(FindType("DCL.ExplorePanel.ExplorePanelController") != null, "DCL.ExplorePanel.ExplorePanelController");
            Req(FindType("DCL.UI.ExploreSections") != null, "DCL.UI.ExploreSections");
            Req(FindType("DCL.Chat.History.ChatChannel") != null, "DCL.Chat.History.ChatChannel");

            if (missing.Count == 0) Debug.Log("[Harness] schema check OK — all reflected members resolved.");
            else Debug.LogError("[Harness] SCHEMA DRIFT — reflected members that no longer resolve (client likely renamed them): " + string.Join(", ", missing));
        }

        // IRealmNavigator.TeleportToParcelAsync(Vector2Int parcel, CancellationToken ct, bool isLocal, bool landOnParcel=false)
        //   returns UniTask<EnumResult<TaskError>>. We fire-and-observe; the
        //   perf-sample + settle window covers the async completion. We DON'T
        //   await the UniTask here (would need UniTask interop); instead we
        //   invoke and let it run, then verify via NavigationExecuted / position.
        private static bool TryTeleport(object realmNavigator, Vector2Int parcel, out string err)
        {
            err = null;
            try
            {
                // Robust overload resolution: the live RealmNavigator.TeleportToParcelAsync arity has
                // drifted from the source checkout (Invoke reported a param-count mismatch on the old
                // fixed 3-arg call). Pick the overload whose first param is Vector2Int and bind args by
                // TYPE to its actual parameter list (parcel, CancellationToken.None, isLocal=false; any
                // other/extra param gets its default), so we match whatever signature the build exposes.
                MethodInfo mi = null;
                foreach (var cand in realmNavigator.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (cand.Name != "TeleportToParcelAsync") continue;
                    var ps0 = cand.GetParameters();
                    if (ps0.Length >= 1 && ps0[0].ParameterType == typeof(Vector2Int)) { mi = cand; break; }
                }
                if (mi == null) { err = "TeleportToParcelAsync(Vector2Int,...) not found"; return false; }

                var ps = mi.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    Type pt = ps[i].ParameterType;
                    if (pt == typeof(Vector2Int)) args[i] = parcel;
                    else if (pt == typeof(System.Threading.CancellationToken)) args[i] = default(System.Threading.CancellationToken);
                    else if (pt == typeof(bool)) args[i] = false;                 // isLocal
                    else if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                    else if (pt.IsValueType) args[i] = Activator.CreateInstance(pt);
                    else args[i] = null;
                }
                mi.Invoke(realmNavigator, args);
                return true;
            }
            catch (Exception e) { err = e.InnerException?.Message ?? e.Message; return false; }
        }

        // Reads the local player's current world position and converts to a parcel (16u/parcel).
        // Primary path: staticContainer.CharacterContainer.CharacterObject.Position (a public
        // Vector3 = transform.position). Fallback: CharacterContainer.Transform (ExposedTransform)
        // .Position.Value (CanBeDirty<Vector3>). Read-only; used by the non-gating 0,0-flash probe.
        private static bool TryGetPlayerParcel(object staticContainer, out Vector2Int parcel, out string err)
        {
            parcel = Vector2Int.zero;
            err = null;
            try
            {
                if (staticContainer == null) { err = "staticContainer null"; return false; }
                // CharacterContainer is a PROPERTY on StaticContainer; CharacterObject a property on
                // it; Transform a public FIELD. Use GetMember (property-then-field) so field/property
                // differences don't matter.
                object charContainer = GetMember(staticContainer, "CharacterContainer");
                if (charContainer == null) { err = "CharacterContainer not found"; return false; }

                object posObj = null;
                // Primary: CharacterObject.Position (Vector3)
                object charObject = GetMember(charContainer, "CharacterObject");
                if (charObject != null) posObj = GetMember(charObject, "Position");
                // Fallback: Transform (ExposedTransform).Position.Value
                if (posObj == null)
                {
                    object exposed = GetMember(charContainer, "Transform");
                    object canBeDirty = exposed != null ? GetMember(exposed, "Position") : null;
                    if (canBeDirty != null) posObj = GetMember(canBeDirty, "Value");
                }
                if (posObj == null || !(posObj is Vector3)) { err = "position unreadable"; return false; }

                var p = (Vector3)posObj;
                parcel = new Vector2Int(Mathf.FloorToInt(p.x / 16f), Mathf.FloorToInt(p.z / 16f));
                return true;
            }
            catch (Exception e) { err = e.InnerException?.Message ?? e.Message; return false; }
        }

        // IChatMessagesBus.Send(ChatChannel channel, string message, ChatMessageOrigin origin, double timestamp)
        //   We use ChatChannel.NEARBY_CHANNEL (static field) and origin CHAT.
        //   There's also an extension SendWithUtcNowTimestamp, but extensions are
        //   harder via reflection, so we call Send directly.
        private static bool TrySendChat(object chatBus, string message, out string err)
        {
            err = null;
            try
            {
                // Resolve types by name from loaded assemblies.
                Type channelType = FindType("DCL.Chat.History.ChatChannel");
                Type originType  = FindType("DCL.Chat.MessageBus.ChatMessageOrigin");
                if (channelType == null || originType == null) { err = "Chat types not found"; return false; }

                object nearby = channelType.GetField("NEARBY_CHANNEL",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                object originChat = Enum.Parse(originType, "CHAT");
                double ts = DateTime.UtcNow.ToOADate();

                var send = chatBus.GetType().GetMethod("Send",
                    BindingFlags.Public | BindingFlags.Instance);
                if (send == null) { err = "IChatMessagesBus.Send not found"; return false; }
                send.Invoke(chatBus, new object[] { nearby, message, originChat, ts }); // TODO(LIVE): confirm signature on the concrete decorator
                return true;
            }
            catch (Exception e) { err = e.InnerException?.Message ?? e.Message; return false; }
        }

        // =====================================================================
        //  REFLECTION HELPERS
        // =====================================================================
        // Opens the Explore panel on a given section via the same MVC path the input
        // shortcuts use: ExplorePanelController.IssueCommand(new ExplorePanelParameter(section[, tab]))
        // -> IMVCManager.ShowAsync<TView,TInput>(command, ct). Reflection-only (see header).
        private static bool TryOpenExplorePanel(object mvcManager, string sectionName, string settingsTab, out string err)
        {
            err = null;
            try
            {
                Type controllerT = FindType("DCL.ExplorePanel.ExplorePanelController");
                Type paramT      = FindType("DCL.ExplorePanel.ExplorePanelParameter");
                Type sectionsT   = FindType("DCL.UI.ExploreSections");
                if (controllerT == null || paramT == null || sectionsT == null) { err = "ExplorePanel types not found"; return false; }

                object section = Enum.Parse(sectionsT, sectionName);

                // ctor: (ExploreSections section, BackpackSections? = null, SettingsSection? = null)
                ConstructorInfo ctor = paramT.GetConstructors()[0];
                object[] ctorArgs = new object[ctor.GetParameters().Length];
                ctorArgs[0] = section;

                if (settingsTab != null)
                {
                    Type settingsSectionT = FindType("DCL.Settings.SettingsController+SettingsSection");
                    if (settingsSectionT == null) { err = "SettingsSection enum not found"; return false; }
                    if (ctorArgs.Length > 2) ctorArgs[2] = Enum.Parse(settingsSectionT, settingsTab);
                }

                object param = ctor.Invoke(ctorArgs);
                return TryShowPanel(mvcManager, controllerT, param, out err);
            }
            catch (Exception e) { err = e.Message; return false; }
        }

        // Opens the Friends panel on a tab (FRIENDS / REQUESTS / BLOCKED) - exercises the
        // friends-list fetch + requests UI through the same IssueCommand/ShowAsync path.
        private static bool TryOpenFriendsPanel(object mvcManager, string tabName, out string err)
        {
            err = null;
            try
            {
                Type controllerT = FindType("DCL.Friends.UI.FriendPanel.FriendsPanelController");
                Type paramT      = FindType("DCL.Friends.UI.FriendPanel.FriendsPanelParameter");
                Type tabT        = FindType("DCL.Friends.UI.FriendPanel.FriendsPanelController+FriendsPanelTab");
                if (controllerT == null || paramT == null || tabT == null) { err = "Friends panel types not found"; return false; }

                object tab = Enum.Parse(tabT, tabName);
                object param = Activator.CreateInstance(paramT, tab);
                return TryShowPanel(mvcManager, controllerT, param, out err);
            }
            catch (Exception e) { err = e.Message; return false; }
        }

        // Shared: controllerT.IssueCommand(param) -> mvcManager.ShowAsync<TView,TInput>(cmd, ct)
        private static bool TryShowPanel(object mvcManager, Type controllerT, object param, out string err)
        {
            err = null;

            MethodInfo issue = controllerT.GetMethod("IssueCommand", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (issue == null) { err = "IssueCommand not found on " + controllerT.Name; return false; }
            object command = issue.Invoke(null, new[] { param });

            Type cmdType = command.GetType(); // ShowCommand<TView,TInput> carries the generic args
            MethodInfo showAsync = null;
            foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
            if (showAsync == null) { err = "ShowAsync not found on " + mvcManager.GetType().Name; return false; }

            // fire-and-forget: the async flow starts synchronously and continues on the player loop
            Type[] genArgs = cmdType.GetGenericArguments(); // [TView, TInputData]
            showAsync.MakeGenericMethod(genArgs)
                     .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });

            // Record the dict key MVCManager uses internally (controllers[typeof(IController<TView,TInputData>)])
            // so the caller can VerifyShown() after the settle and turn this invocation-only "ok"
            // into a real render assertion.
            Type ifaceOpen = FindType("MVC.IController`2");
            lastPanelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(genArgs) : null;
            return true;
        }

        // Set by TryShowPanel; the MVCManager.Controllers key for the most recently opened panel.
        private static Type lastPanelKey;
        // Open the ExplorePanel via the EXACT mechanism the WORKING AtlasCapture_backpackemotes /
        // backpackoutfits drivers use (build ExplorePanelParameter inline, IssueCommand -> resolve the
        // generic ShowAsync off command.GetType().GetGenericArguments(), fire-and-forget, record
        // lastPanelKey). It exists because re-opening the ExplorePanel through TryOpenExplorePanel /
        // TryShowPanelByName SILENTLY NO-OPS on this build for the settings/events/backpack/iteminfo
        // sections: AtlasCapture_explore (E01) shows the ExplorePanel first, the atlas loop hides it
        // with CloseOpenPanels between drivers, and MVCManager.ShowAsync bails ("if (State != ViewHidden)
        // return") while the controller is still mid-hide/ViewHiding -> the driver captures the bare
        // world (E02/E04/E06/E12). We FIRST call CloseAllNonPersistentViews synchronously here so the
        // controller is fully ViewHidden before the re-issue, then drive the proven inline open.
        //   exploreSection   : "Backpack" | "Settings" | "Events" | ... (DCL.UI.ExploreSections)
        //   backpackSubOrNull: "Emotes"/"Outfits"/... (DCL.UI.BackpackSections) or null
        //   settingsTabOrNull: "GRAPHICS"/"GENERAL"/... (SettingsController+SettingsSection) or null
        // Result of the last OpenExplorePanelDirectCo run: null on success, else the error string.
        // (A coroutine can't have an out param / bool return, so the 4 drivers read this static after yielding.)
        private static string openDirectErr;

        // COROUTINE form of the direct open. rerun7 proved the synchronous pre-close was insufficient:
        // CloseAllNonPersistentViews only PopFullscreens the ExplorePanel (its Layer is FULLSCREEN), which
        // signals the controller closure; the actual HideViewAsync that flips State to ViewHidden then runs
        // ASYNC over several frames (ControllerBase.HideViewAsync: State=ViewHiding -> await view.HideAsync ->
        // State=ViewHidden). The same-frame ShowAsync therefore saw State != ViewHidden and no-opped
        // (MVCManager.ShowAsync: "if (controller.State != ControllerState.ViewHidden) return;"), so the driver
        // captured the bare world. Here we pre-close, then POLL the ExplorePanelController.State until it is
        // actually ViewHidden (up to ~30 frames) BEFORE issuing the open. Drivers must `yield return` this
        // OUTSIDE their try and then read openDirectErr.
        private static IEnumerator OpenExplorePanelDirectCo(object mvcManager, string exploreSection, string backpackSubOrNull, string settingsTabOrNull)
        {
            openDirectErr = null;
            if (mvcManager == null) { openDirectErr = "mvcManager null"; yield break; }

            // 1) Pre-close: hide any open fullscreen/popup/overlay so the re-issue can re-open. Async hide starts here.
            try
            {
                MethodInfo closeAll = mvcManager.GetType().GetMethod("CloseAllNonPersistentViews", BindingFlags.Public | BindingFlags.Instance);
                closeAll?.Invoke(mvcManager, new object[] { System.Threading.CancellationToken.None });
            }
            catch (Exception ce) { Debug.LogWarning("[HARNESS] OpenExplorePanelDirectCo pre-close failed: " + ce.Message); }

            // 2) Wait until the ExplorePanelController has actually finished hiding (State == ViewHidden).
            //    The State read is wrapped in a no-yield try; the `yield return null` stays OUTSIDE it.
            for (int i = 0; i < 30; i++)
            {
                bool hidden = false;
                try
                {
                    object explore = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                    string state = explore != null ? (GetPublicProperty(explore, "State")?.ToString()) : null;
                    // No controller yet (never opened) also counts as "safe to open".
                    hidden = explore == null || state == null || state == "ViewHidden";
                }
                catch (Exception) { hidden = false; }
                if (hidden) break;
                yield return null;
            }

            // 3) Now the controller is ViewHidden -> the issue-only open can't no-op. (Errors -> openDirectErr.)
            if (!OpenExplorePanelDirect(mvcManager, exploreSection, backpackSubOrNull, settingsTabOrNull, out string issueErr))
                openDirectErr = issueErr;
        }

        private static bool OpenExplorePanelDirect(object mvcManager, string exploreSection, string backpackSubOrNull, string settingsTabOrNull, out string err)
        {
            err = null;
            try
            {
                if (mvcManager == null) { err = "mvcManager null"; return false; }

                // NOTE: the pre-close + ViewHidden poll now live in OpenExplorePanelDirectCo (the coroutine the
                // drivers call). By the time we get here the ExplorePanelController is already ViewHidden, so the
                // ShowAsync below can't be silently swallowed by MVCManager's "State != ViewHidden -> return" guard.
                Type paramType         = FindType("DCL.ExplorePanel.ExplorePanelParameter");
                Type exploreSectionsT  = FindType("DCL.UI.ExploreSections");
                Type controllerType    = FindType("DCL.ExplorePanel.ExplorePanelController");
                if (paramType == null || exploreSectionsT == null || controllerType == null)
                { err = "explore-panel types not found (ExplorePanelParameter/ExploreSections/ExplorePanelController)"; return false; }

                object section = Enum.Parse(exploreSectionsT, exploreSection);

                // ctor: ExplorePanelParameter(ExploreSections, BackpackSections? = null, SettingsSection? = null).
                // Fill positional args; nullable enum params accept the boxed enum directly (mirrors backpackemotes).
                ConstructorInfo ctor = paramType.GetConstructors()[0];
                object[] ctorArgs = new object[ctor.GetParameters().Length];
                ctorArgs[0] = section;
                if (backpackSubOrNull != null && ctorArgs.Length > 1)
                {
                    Type backpackSectionsT = FindType("DCL.UI.BackpackSections");
                    if (backpackSectionsT == null) { err = "BackpackSections enum not found"; return false; }
                    ctorArgs[1] = Enum.Parse(backpackSectionsT, backpackSubOrNull);
                }
                if (settingsTabOrNull != null && ctorArgs.Length > 2)
                {
                    Type settingsSectionT = FindType("DCL.Settings.SettingsController+SettingsSection");
                    if (settingsSectionT == null) { err = "SettingsSection enum not found"; return false; }
                    ctorArgs[2] = Enum.Parse(settingsSectionT, settingsTabOrNull);
                }
                object param = ctor.Invoke(ctorArgs);

                // Static IssueCommand(param) -> ShowCommand<TView,TInput> (inherited -> FlattenHierarchy).
                MethodInfo issueCommand = controllerType.GetMethod("IssueCommand", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (issueCommand == null) { err = "IssueCommand not found on ExplorePanelController"; return false; }
                object command = issueCommand.Invoke(null, new[] { param });
                if (command == null) { err = "IssueCommand returned null"; return false; }

                Type[] genArgs = command.GetType().GetGenericArguments(); // [TView, TInput]
                if (genArgs.Length != 2) { err = "command generic args count != 2"; return false; }

                MethodInfo showAsync = null;
                foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                if (showAsync == null) { err = "ShowAsync not found on MvcManager"; return false; }

                // fire-and-forget: starts synchronously, continues on the player loop
                showAsync.MakeGenericMethod(genArgs)
                         .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });

                Type ifaceOpen = FindType("MVC.IController`2");
                lastPanelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(genArgs) : null;
                return true;
            }
            catch (Exception e) { err = e.InnerException?.Message ?? e.Message; return false; }
        }

        // Open a panel by controller full-name, tolerant of a 0-arg or 1-arg static IssueCommand.
        // Used for EXPANDED (non-gating) surface coverage so we can broaden what we open without
        // hard-coding each controller's parameter type. Mirrors TryShowPanel's ShowAsync dance.
        private static bool TryShowPanelByName(object mvcManager, string controllerFullName, object paramOrNull, out string err)
        {
            err = null;
            try
            {
                Type controllerT = FindType(controllerFullName);
                if (controllerT == null) { err = "type not found: " + controllerFullName; return false; }

                MethodInfo issue = null;
                foreach (MethodInfo mi in controllerT.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                    if (mi.Name == "IssueCommand")
                    {
                        int np = mi.GetParameters().Length;
                        if ((paramOrNull == null && np == 0) || (paramOrNull != null && np == 1)) { issue = mi; break; }
                        if (issue == null) issue = mi; // fallback to any IssueCommand
                    }
                if (issue == null) { err = "IssueCommand not found on " + controllerT.Name; return false; }

                object command = issue.GetParameters().Length == 0
                    ? issue.Invoke(null, null)
                    : issue.Invoke(null, new[] { paramOrNull });
                if (command == null) { err = "IssueCommand returned null"; return false; }

                MethodInfo showAsync = null;
                foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                if (showAsync == null) { err = "ShowAsync not found"; return false; }

                Type[] genArgs = command.GetType().GetGenericArguments();
                showAsync.MakeGenericMethod(genArgs)
                         .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });
                Type ifaceOpen = FindType("MVC.IController`2");
                lastPanelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(genArgs) : null;
                return true;
            }
            catch (Exception e) { err = e.Message; return false; }
        }

        // Closes any open FULLSCREEN/POPUP/OVERLAY views (leaves PERSISTENT) so the NEXT ShowAsync
        // actually re-opens. MVCManager.ShowAsync no-ops when the controller is already shown
        // ("if (controller.State != ViewHidden) return") - without this, every section switch after
        // the first panel was a SILENT no-op (the Backpack stayed open through all later sections).
        private static IEnumerator CloseOpenPanels(object mvcManager)
        {
            if (mvcManager != null)
            {
                try
                {
                    MethodInfo m = mvcManager.GetType().GetMethod("CloseAllNonPersistentViews", BindingFlags.Public | BindingFlags.Instance);
                    m?.Invoke(mvcManager, new object[] { System.Threading.CancellationToken.None });
                }
                catch (Exception e) { Debug.LogWarning("[HARNESS] CloseOpenPanels failed: " + e.Message); }
            }
            // CloseAllNonPersistentViews only SIGNALS closure; the fullscreen ExplorePanel's HideViewAsync then
            // flips State ViewHiding -> ViewHidden over several ASYNC frames. A fixed 8-frame settle alone often
            // returned mid-hide, so the next driver's synchronous ShowAsync saw State != ViewHidden and no-opped
            // -> bare world (settings/map/communities/reel/gallery/... captured bare once the Folded dedup dropped
            // the per-driver pre-close). POLL the ExplorePanelController until it is actually ViewHidden — the same
            // deterministic gate OpenExplorePanelDirectCo uses — so each driver opens from a clean slot; capped so a
            // stuck hide can't hang. A short floor settle after covers non-Explore popups/overlays.
            for (int i = 0; i < 60; i++)
            {
                bool hidden = false;
                try
                {
                    object explore = mvcManager != null ? FindControllerByTypeName(mvcManager, "ExplorePanelController") : null;
                    string state = explore != null ? (GetPublicProperty(explore, "State")?.ToString()) : null;
                    hidden = explore == null || state == null || state == "ViewHidden";
                }
                catch (Exception) { hidden = false; }
                if (hidden) break;
                yield return null;
            }
            for (int i = 0; i < 8; i++) yield return null; // floor settle for non-Explore views
        }

        // Generic pre-show settle for STANDALONE (non-ExplorePanel) controllers that hit the same inter-driver
        // race OpenExplorePanelDirectCo solves: the loop's CloseOpenPanels starts an ASYNC hide of the prior view,
        // and a same-frame ShowAsync on the new controller can land while the fullscreen/popup slot is still
        // transitioning -> the show is swallowed and the target ends ViewHidden (creditsunlocked M07 failed this
        // way in runs 1 & 3). Pre-close, then POLL the target controller until it is ViewHidden/absent (so its own
        // ShowAsync guard can't no-op) before the caller issues its show. Drivers `yield return` this OUTSIDE any try.
        private static IEnumerator PreShowSettle(object mvcManager, string controllerTypeName)
        {
            if (mvcManager == null) yield break;
            try
            {
                MethodInfo closeAll = mvcManager.GetType().GetMethod("CloseAllNonPersistentViews", BindingFlags.Public | BindingFlags.Instance);
                closeAll?.Invoke(mvcManager, new object[] { System.Threading.CancellationToken.None });
            }
            catch (Exception e) { Debug.LogWarning("[HARNESS] PreShowSettle pre-close failed: " + e.Message); }

            for (int i = 0; i < 30; i++)
            {
                bool hidden = false;
                try
                {
                    object ctl = FindControllerByTypeName(mvcManager, controllerTypeName);
                    string state = ctl != null ? (GetPublicProperty(ctl, "State")?.ToString()) : null;
                    hidden = ctl == null || state == null || state == "ViewHidden";
                }
                catch (Exception) { hidden = false; }
                if (hidden) break;
                yield return null;
            }
        }

        // wrong-grid isolation: the ExplorePanel (Communities/Backpack/etc. full-screen grid) is a PERSISTENT MVC
        // view, so CloseAllNonPersistentViews / CloseOpenPanels does NOT close it. After the communities/explore
        // drivers run, that grid stays mounted and OCCLUDES later chat-overlay / popup captures (they render the
        // leftover Communities grid instead of their target). Hard-hide it the same way HideAuthSubViews isolates
        // stacked auth views: drive the controller's view GameObject inactive. The panel re-activates the next time
        // a section is opened via IssueCommand->ShowAsync, so this is safe for the tail drivers that never re-open it.
        private static IEnumerator HideExplorePanel(object mvcManager)
        {
            if (mvcManager != null)
            {
                try
                {
                    object explore = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                    object view = explore != null ? GetMember(explore, "viewInstance") : null;
                    object go = view != null ? GetMember(view, "gameObject") : null;
                    var setActive = go?.GetType().GetMethod("SetActive", new[] { typeof(bool) });
                    setActive?.Invoke(go, new object[] { false });
                }
                catch (Exception e) { Debug.LogWarning("[HARNESS] HideExplorePanel failed: " + e.Message); }
            }
            for (int i = 0; i < 6; i++) yield return null; // let the hide settle before the target opens
        }
        // Invoke a public 0-arg method on the chat controller's private ChatSharedAreaEventBus. This bus is the
        // robust entry into the chat FSM: the controller forwards UI intents onto it, and ChatPanelPresenter maps
        //   RaiseViewShowEvent() -> ChatStateMachine.OnViewShow()    -> fsm.Enter<DefaultChatState>()  (out of Init)
        //   RaiseFocusEvent()    -> ChatStateMachine.SetFocusState() -> fsm.Enter<FocusedChatState>()  (UNCONDITIONAL)
        // We drive the bus DIRECTLY instead of via MVC ShowAsync / ChatOpener because the chat is a PERSISTENT view:
        // ShowAsync no-ops on an already-shown view (so OnViewShow/ViewShowEvent never re-fires, leaving the FSM in
        // InitChatState), and the ChatOpener focus path is state-gated (OnFocusRequested no-ops in InitChatState).
        // The shared-area bus calls are not gated, so they work even after a HideChat. UI-only; no message is sent.
        private static void RaiseChatBus(object mvcManager, string raiseMethod)
        {
            try
            {
                object chatCtl = mvcManager != null ? FindControllerByTypeName(mvcManager, "ChatMainSharedAreaController") : null;
                object bus = chatCtl != null ? GetPrivateField(chatCtl, "chatSharedAreaEventBus") : null;
                if (bus == null) { Debug.LogWarning("[HARNESS] RaiseChatBus: chatSharedAreaEventBus not found (" + raiseMethod + ")"); return; }
                bus.GetType().GetMethod(raiseMethod, BindingFlags.Public | BindingFlags.Instance)?.Invoke(bus, null);
            }
            catch (Exception e) { Debug.LogWarning("[HARNESS] RaiseChatBus " + raiseMethod + " failed: " + (e.InnerException?.Message ?? e.Message)); }
        }

        // Restore the chat HUD to its DEFAULT (unfocused) visible state. Earlier clean-shot drivers call HideChat,
        // which DEACTIVATES the persistent chat view GameObject WITHOUT closing the MVC view — so the controller
        // stays ViewShown, a re-ShowAsync no-ops, and the chat widget would stay hidden -> bare world (the H03/chat
        // + chatwindow/reactions/inputsuggestions round-2/3 regressions). Re-activate the GO, ensure the MVC view
        // exists (first-run ShowAsync, harmless if already shown), then RaiseViewShowEvent so the FSM advances
        // Init -> DefaultChatState and the default HUD renders. Chat-overlay drivers chain ShowAndFocusChat on top.
        private static IEnumerator ShowChatDefault(object mvcManager)
        {
            // Re-activate the persistent chat view GameObject if a HideChat left it inactive.
            try
            {
                object chatCtl = mvcManager != null ? FindControllerByTypeName(mvcManager, "ChatMainSharedAreaController") : null;
                object view = chatCtl != null ? GetMember(chatCtl, "viewInstance") : null;
                if (view is UnityEngine.MonoBehaviour mb && mb != null && !mb.gameObject.activeSelf)
                    mb.gameObject.SetActive(true);
            }
            catch (Exception e) { Debug.LogWarning("[HARNESS] ShowChatDefault reactivate failed: " + e.Message); }

            // First-run safety: ShowAsync the persistent chat controller so its view is instantiated/registered.
            // No-ops if already shown (which is the usual case in-world). Mirrors the inputsuggestions path.
            try
            {
                Type controllerT = FindType("DCL.ChatArea.ChatMainSharedAreaController");
                if (controllerT != null && mvcManager != null)
                {
                    MethodInfo issueCmd = null;
                    foreach (MethodInfo mi in controllerT.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                        if (mi.Name == "IssueCommand" && mi.GetParameters().Length == 0) { issueCmd = mi; break; }
                    object command = issueCmd != null ? issueCmd.Invoke(null, null) : null;
                    if (command != null)
                    {
                        Type[] genArgs = command.GetType().GetGenericArguments();
                        MethodInfo showAsync = null;
                        foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                            if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                        if (showAsync != null && genArgs.Length >= 1)
                            showAsync.MakeGenericMethod(genArgs)
                                     .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });
                    }
                }
            }
            catch (Exception e) { Debug.LogWarning("[HARNESS] ShowChatDefault show failed: " + (e.InnerException?.Message ?? e.Message)); }

            for (int i = 0; i < 10; i++) yield return null;   // let the view instantiate/activate

            // Drive the FSM out of InitChatState into DefaultChatState (the default visible HUD).
            RaiseChatBus(mvcManager, "RaiseViewShowEvent");
            for (int i = 0; i < 8; i++) yield return null;
        }

        // Make the chat VISIBLE for the chat-overlay drivers (chatwindow/reactions/emoji/inputsuggestions).
        // IMPORTANT — we intentionally leave the chat in DefaultChatState and do NOT force FocusedChatState:
        // empirically (round-4 visual verify) FocusedChatState.Enter -> SetupForFocusedState -> ShowFocusedAsync
        // does NOT render in this editor-driven context (it needs a real focused input field), and worse it
        // REPLACES the working default HUD with nothing -> bare world (it even regressed emoji, which had rendered
        // fine over the default HUD). DefaultChatState renders reliably (see H03 chat). The overlay drivers then
        // force-reveal their OWN sub-view (EmojiPanelView, ChatReactionButtonView, the input/suggestion panel)
        // directly, so they only need the chat panel present, not focused. We DO fire RaiseFocusEvent as a
        // best-effort AFTER the panel is up (some builds honor it without tearing down the HUD), but never rely
        // on it. UI-only; no message is sent.
        private static IEnumerator ShowAndFocusChat(object mvcManager)
        {
            yield return ShowChatDefault(mvcManager);          // reactivate + Init -> DefaultChatState (renders)
            // NOTE: deliberately NO RaiseFocusEvent here — SetFocusState() enters FocusedChatState UNCONDITIONALLY,
            // and on this build that state renders bare (needs a real focused input) AND tears down the default HUD,
            // regressing emoji. The overlay drivers reveal their own sub-views over the visible default HUD instead.
            for (int i = 0; i < 14; i++) yield return null;     // settle before the driver reveals its sub-view
        }


        // Real render assertion: true only if the panel's controller is actually showing
        // (State != ViewHidden/ViewHiding). Turns "ShowAsync didn't throw" into "the view rendered".
        private static bool VerifyShown(object mvcManager, Type keyType, out string err)
        {
            err = null;
            if (mvcManager == null || keyType == null) { err = "no panel key"; return false; }
            // The runtime MvcManager is MVCManagerAnalyticsDecorator: it implements IMVCManager (so
            // CloseAllNonPersistentViews works) but does NOT expose the concrete Controllers dict.
            // Read Controllers off the decorator if present, else off its inner 'core' MVCManager.
            object dict = GetPublicProperty(mvcManager, "Controllers"); // IReadOnlyDictionary<Type,IController>
            if (dict == null)
            {
                object core = GetPrivateField(mvcManager, "core");
                if (core != null) dict = GetPublicProperty(core, "Controllers");
            }
            if (dict == null) { err = "Controllers unavailable"; return false; }
            MethodInfo tryGet = dict.GetType().GetMethod("TryGetValue");
            if (tryGet == null) { err = "TryGetValue unavailable"; return false; }
            object[] a = new object[] { keyType, null };
            bool found = (bool)tryGet.Invoke(dict, a);
            if (!found || a[1] == null) { err = "controller not registered"; return false; }
            string state = GetPublicProperty(a[1], "State")?.ToString() ?? "?";
            if (state == "ViewHidden" || state == "ViewHiding") { err = "view not shown (State=" + state + ")"; return false; }
            return true;
        }

        // =====================================================================
        //  CONTENT ENUMERATION (what places / events / games exist right now)
        //  Reflection into the Explore panel's data services. All NON-GATING.
        // =====================================================================
        private static object awaitedResult;
        private static string awaitedError;

        private static IEnumerator EnumerateContent(object mvcManager, Report report)
        {
            object explore = FindControllerByTypeName(mvcManager, "ExplorePanelController");
            if (explore == null)
            {
                report.actions.Add(new PhaseMarker { label = "data_content", ok = true,
                    error = "expand(non-gating): ExplorePanelController not found" });
                yield break;
            }

            object eventsApi = GetPrivateField(explore, "eventsApiService");
            object placesController = GetMember(explore, "PlacesController");
            object placesResults = placesController != null ? GetMember(placesController, "PlacesResultsController") : null;
            object placesApi = placesResults != null ? GetPrivateField(placesResults, "placesAPIService") : null;
            var ctNone = System.Threading.CancellationToken.None;

            // --- EVENTS: GetEventsAsync(ct, onlyLiveEvents:false) -> IReadOnlyList<EventDTO> ---
            var em = new PhaseMarker { label = "data_events", ok = true };
            report.actions.Add(em);
            if (eventsApi == null) em.error = "expand(non-gating): eventsApiService not found";
            else
            {
                object task = TryInvoke(eventsApi, "GetEventsAsync", new object[] { ctNone, false }, out string ierr);
                if (task == null) em.error = "expand(non-gating): invoke-failed: " + ierr;
                else
                {
                    yield return AwaitUniTask(task);
                    if (awaitedError != null) em.error = "expand(non-gating): " + awaitedError;
                    else em.error = "expand(non-gating): " + SummarizeEvents(awaitedResult);
                }
            }

            // --- PLACES (most active) + GAMES (category="game") via SearchDestinationsAsync ---
            yield return EnumeratePlaces(placesApi, "data_places", null, report);
            yield return EnumeratePlaces(placesApi, "data_games", "game", report);
        }

        // Calls SearchDestinationsAsync(pageNumber, pageSize, ct, [defaults...], category) and reports
        // the response Total + a sample of titles@coords. categoryOrNull=null => general most-active.
        private static IEnumerator EnumeratePlaces(object placesApi, string label, string categoryOrNull, Report report)
        {
            var pm = new PhaseMarker { label = label, ok = true };
            report.actions.Add(pm);
            if (placesApi == null) { pm.error = "expand(non-gating): placesAPIService not found"; yield break; }

            MethodInfo mi = null;
            foreach (MethodInfo m in placesApi.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "SearchDestinationsAsync") { mi = m; break; }
            if (mi == null) { pm.error = "expand(non-gating): SearchDestinationsAsync not found"; yield break; }

            // Build args: required by position (pageNumber, pageSize, ct), Type.Missing for optionals,
            // set the 'category' parameter (by name) for the games query.
            ParameterInfo[] ps = mi.GetParameters();
            object[] args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(int)) args[i] = (i == 0) ? 0 : 20;
                else if (ps[i].ParameterType == typeof(System.Threading.CancellationToken)) args[i] = System.Threading.CancellationToken.None;
                else if (categoryOrNull != null && ps[i].Name == "category") args[i] = categoryOrNull;
                else args[i] = ps[i].HasDefaultValue ? Type.Missing : null;
            }

            object task = null; string ierr = null;
            try { task = mi.Invoke(placesApi, args); }
            catch (Exception e) { ierr = e.InnerException?.Message ?? e.Message; }
            if (task == null) { pm.error = "expand(non-gating): invoke-failed: " + ierr; yield break; }

            yield return AwaitUniTask(task);
            if (awaitedError != null) { pm.error = "expand(non-gating): " + awaitedError; yield break; }
            pm.error = "expand(non-gating): " + SummarizePlaces(awaitedResult);
        }

        // Drives a UniTask<T> (boxed) to completion via its awaiter inside the editor coroutine.
        // Sets awaitedResult / awaitedError. Never throws; 15s timeout. (No yield inside try/catch.)
        private static IEnumerator AwaitUniTask(object uniTask)
        {
            awaitedResult = null; awaitedError = null;
            object awaiter = null; PropertyInfo isDone = null;
            try
            {
                if (uniTask == null) awaitedError = "null unitask";
                else
                {
                    awaiter = uniTask.GetType().GetMethod("GetAwaiter", Type.EmptyTypes)?.Invoke(uniTask, null);
                    isDone = awaiter?.GetType().GetProperty("IsCompleted");
                }
            }
            catch (Exception e) { awaitedError = "getawaiter: " + (e.InnerException?.Message ?? e.Message); }
            if (awaiter == null || isDone == null) { if (awaitedError == null) awaitedError = "no awaiter"; yield break; }

            float timeout = UnityEngine.Time.realtimeSinceStartup + 15f;
            bool failed = false;
            while (true)
            {
                bool done = false;
                try { done = (bool)isDone.GetValue(awaiter); }
                catch (Exception e) { awaitedError = "iscompleted: " + e.Message; failed = true; }
                if (failed || done) break;
                if (UnityEngine.Time.realtimeSinceStartup > timeout) { awaitedError = "timeout(15s)"; failed = true; break; }
                yield return null;
            }
            if (failed) yield break;

            try { awaitedResult = awaiter.GetType().GetMethod("GetResult").Invoke(awaiter, null); }
            catch (Exception e) { awaitedError = "getresult: " + (e.InnerException?.Message ?? e.Message); }
        }

        // Scans MVCManager.Controllers values for a controller whose runtime type name matches.
        private static object FindControllerByTypeName(object mvcManager, string typeName)
        {
            object dict = GetPublicProperty(mvcManager, "Controllers");
            if (dict == null) { object core = GetPrivateField(mvcManager, "core"); if (core != null) dict = GetPublicProperty(core, "Controllers"); }
            if (dict == null) return null;
            object values = GetPublicProperty(dict, "Values");
            if (!(values is System.Collections.IEnumerable en)) return null;
            foreach (object c in en)
                if (c != null && c.GetType().Name == typeName) return c;
            return null;
        }

        private static object TryInvoke(object target, string method, object[] args, out string err)
        {
            err = null;
            try
            {
                MethodInfo mi = null;
                foreach (MethodInfo m in target.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (m.Name == method) { mi = m; break; }
                if (mi == null) { err = method + " not found"; return null; }
                return mi.Invoke(target, args);
            }
            catch (Exception e) { err = e.InnerException?.Message ?? e.Message; return null; }
        }

        // IReadOnlyList<EventDTO> -> "total=N live=M | live: name@x,y; ... (top5)"
        private static string SummarizeEvents(object listObj)
        {
            if (!(listObj is System.Collections.IEnumerable en)) return "events: <unreadable>";
            int total = 0, live = 0, shown = 0;
            var sb = new System.Text.StringBuilder();
            foreach (object ev in en)
            {
                total++;
                bool isLive = (GetMember(ev, "live") as bool?) ?? false;
                if (isLive) live++;
                if (isLive && shown < 5)
                {
                    string name = AsciiClean(GetMember(ev, "name") as string ?? "?");
                    sb.Append(name).Append('@').Append(CoordStr(GetMember(ev, "coordinates"))).Append("; ");
                    shown++;
                }
            }
            string head = $"events total={total} live={live}";
            return live > 0 ? head + " | live: " + sb.ToString().TrimEnd(' ', ';') : head;
        }

        // PlacesData.IPlacesAPIResponse (Total + Data:IReadOnlyList<PlaceInfo>) -> "total=N | top: title@pos (U users); ..."
        private static string SummarizePlaces(object respObj)
        {
            if (respObj == null) return "places: <null response>";
            // PlacesAPIResponse implements Total/Data EXPLICITLY (invisible as public props on the
            // concrete type) but exposes public backing fields 'total'/'data' (lowercase).
            object total = GetMember(respObj, "total") ?? GetMember(respObj, "Total");
            object data = GetMember(respObj, "data") ?? GetMember(respObj, "Data");
            if (!(data is System.Collections.IEnumerable en)) return "places total=" + total + " | <data unreadable>";
            var sb = new System.Text.StringBuilder();
            int shown = 0, count = 0;
            foreach (object pl in en)
            {
                count++;
                if (shown < 6)
                {
                    string title = AsciiClean(GetMember(pl, "title") as string ?? "?");
                    string pos = AsciiClean(GetMember(pl, "base_position") as string ?? "?");
                    object users = GetMember(pl, "user_count");
                    sb.Append(title).Append('@').Append(pos).Append(" (").Append(users).Append("u); ");
                    shown++;
                }
            }
            return $"total={total} returned={count} | top: " + sb.ToString().TrimEnd(' ', ';');
        }

        private static string CoordStr(object coordsObj)
        {
            if (coordsObj is int[] c && c.Length >= 2) return c[0] + "," + c[1];
            return "?";
        }

        // CDN-sourced titles/names can contain emoji (surrogate pairs) + control chars that mangle the
        // report JSON when round-tripped through the guest's file encoding. Keep only printable ASCII
        // (0x20-0x7E) minus quote/backslash, so the data_* notes can never corrupt the report.
        private static string AsciiClean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "?";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (c >= 0x20 && c <= 0x7E && c != '"' && c != '\\') sb.Append(c);
            string r = sb.ToString().Trim();
            return r.Length == 0 ? "?" : r;
        }

        // Reads staticContainer.ScenesCache.CurrentScene (ReactiveProperty<ISceneFacade?>) to confirm a real
        // scene is loaded at the current parcel after a teleport. ready = ISceneFacade.IsSceneReady().
        private static bool TryGetSceneLoaded(object staticContainer, out string sceneName, out bool ready, out string err)
        {
            sceneName = "?"; ready = false; err = null;
            try
            {
                if (staticContainer == null) { err = "staticContainer null"; return false; }
                object scenesCache = GetMember(staticContainer, "ScenesCache");
                if (scenesCache == null) { err = "ScenesCache not found"; return false; }
                object curProp = GetMember(scenesCache, "CurrentScene");
                object facade = curProp != null ? GetMember(curProp, "Value") : null;
                if (facade == null) { err = "no current scene (null)"; return false; }
                try { object r = facade.GetType().GetMethod("IsSceneReady", Type.EmptyTypes)?.Invoke(facade, null); if (r is bool b) ready = b; } catch { }
                sceneName = facade.GetType().Name;
                return true;
            }
            catch (Exception e) { err = e.InnerException?.Message ?? e.Message; return false; }
        }

        // NON-GATING: find a live World name from the Places API, switch realm into it via
        // RealmNavigator.TryChangeRealmAsync(isWorld:true), verify RealmData became a World, screenshot.
        // Every step try/caught; the realm-change await is bounded by AwaitUniTask's 15s timeout. Run LAST.
        private static IEnumerator SwitchToWorldAndShoot(object mvcManager, object staticContainer, object realmNavigator, Report report)
        {
            var wm = new PhaseMarker { label = "world_switch", ok = true }; // non-gating
            report.actions.Add(wm);

            object placesApi = null;
            try
            {
                object explore = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                object pc = explore != null ? GetMember(explore, "PlacesController") : null;
                object prc = pc != null ? GetMember(pc, "PlacesResultsController") : null;
                placesApi = prc != null ? GetPrivateField(prc, "placesAPIService") : null;
            }
            catch (Exception e) { wm.error = "expand(non-gating): places-api lookup failed: " + e.Message; }
            if (placesApi == null || realmNavigator == null)
            { if (wm.error == null) wm.error = "expand(non-gating): placesAPIService/realmNavigator unavailable"; yield break; }

            MethodInfo search = null;
            foreach (MethodInfo m in placesApi.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (m.Name == "SearchDestinationsAsync") { search = m; break; }
            if (search == null) { wm.error = "expand(non-gating): SearchDestinationsAsync not found"; yield break; }
            ParameterInfo[] ps = search.GetParameters();
            object[] args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].ParameterType == typeof(int)) args[i] = (i == 0) ? 0 : 30;
                else if (ps[i].ParameterType == typeof(System.Threading.CancellationToken)) args[i] = System.Threading.CancellationToken.None;
                else args[i] = ps[i].HasDefaultValue ? Type.Missing : null;
            }
            object task = null; string ierr = null;
            try { task = search.Invoke(placesApi, args); } catch (Exception e) { ierr = e.InnerException?.Message ?? e.Message; }
            if (task == null) { wm.error = "expand(non-gating): places query invoke-failed: " + ierr; yield break; }
            yield return AwaitUniTask(task);
            if (awaitedError != null) { wm.error = "expand(non-gating): places query: " + awaitedError; yield break; }

            string worldName = null;
            try
            {
                object data = GetMember(awaitedResult, "data") ?? GetMember(awaitedResult, "Data");
                if (data is System.Collections.IEnumerable en)
                    foreach (object pl in en)
                    {
                        string wn = GetMember(pl, "world_name") as string;
                        if (!string.IsNullOrEmpty(wn)) { worldName = wn; break; }
                    }
            }
            catch (Exception e) { wm.error = "expand(non-gating): world-name scan failed: " + e.Message; yield break; }
            if (string.IsNullOrEmpty(worldName)) { wm.error = "expand(non-gating): no live World in places top-30 (skipped)"; yield break; }
            worldName = AsciiClean(worldName);

            object task2 = null; string cerr = null;
            try
            {
                Type urlT = FindType("CommunicationData.URLHelpers.URLDomain");
                MethodInfo fromStr = urlT?.GetMethod("FromString", BindingFlags.Public | BindingFlags.Static);
                if (fromStr == null) wm.error = "expand(non-gating): URLDomain.FromString not found";
                else
                {
                    string url = "https://worlds-content-server.decentraland.org/world/" + worldName.ToLowerInvariant();
                    object urlDom = fromStr.Invoke(null, new object[] { url });
                    MethodInfo change = null;
                    foreach (MethodInfo m in realmNavigator.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        if (m.Name == "TryChangeRealmAsync" && m.GetParameters().Length == 5) { change = m; break; }
                    if (change == null) wm.error = "expand(non-gating): TryChangeRealmAsync(5-arg) not found";
                    else task2 = change.Invoke(realmNavigator, new object[] { urlDom, System.Threading.CancellationToken.None, default(Vector2Int), true, false });
                }
            }
            catch (Exception e) { cerr = e.InnerException?.Message ?? e.Message; }
            if (task2 == null) { if (wm.error == null) wm.error = "expand(non-gating): change-realm invoke-failed: " + cerr; yield break; }

            yield return AwaitUniTask(task2);
            string awaitErr = awaitedError;
            for (int i = 0; i < 90; i++) yield return null;   // settle while the World streams in
            yield return CaptureShot("world_" + worldName);

            string realmName = "?", realmKind = "?";
            try
            {
                object realmData = GetMember(staticContainer, "RealmData");
                realmName = GetMember(realmData, "RealmName") as string ?? "?";
                object rtype = GetMember(realmData, "RealmType");
                object rval = rtype != null ? GetMember(rtype, "Value") : null;
                realmKind = rval?.ToString() ?? "?";
            }
            catch (Exception e) { realmKind = "verify-failed:" + e.Message; }
            wm.error = $"expand(non-gating): target='{worldName}' realm='{realmName}' kind={realmKind}" + (awaitErr != null ? " awaitErr=" + awaitErr : "");
        }

        // Static teleport entry for IPC-driven fidelity tours: find the running
        // RealmNavigator and teleport to parcel (x,y). exec calls this with arg.x/arg.y;
        // result is reported via Debug.Log (ClaudeIPC exec only signals invoked/threw).
        // Must be in Play with the world booted (gate on world-ready first).
        // [Mac-parity addition — keep in sync with the VM harness master.]
        public static void TeleportTo(int x, int y)
        {
            object loader = FindMainSceneLoader();
            if (loader == null) { Debug.LogError("[Harness] TeleportTo: MainSceneLoader not found (in Play?)"); return; }
            object dyn = GetPrivateField(loader, "dynamicWorldContainer");
            object realmNavigator = dyn != null ? GetPublicProperty(dyn, "RealmNavigator") : null;
            if (realmNavigator == null) { Debug.LogError("[Harness] TeleportTo: RealmNavigator not ready (world booted?)"); return; }
            bool ok = TryTeleport(realmNavigator, new Vector2Int(x, y), out string err);
            Debug.Log(ok ? $"[Harness] TeleportTo {x},{y} ok" : $"[Harness] TeleportTo {x},{y} FAILED: {err}");
        }

        private static object FindMainSceneLoader()
        {
            Type t = FindType("Global.Dynamic.MainSceneLoader");
            if (t == null) return null;
#if UNITY_2023_1_OR_NEWER
            return UnityEngine.Object.FindAnyObjectByType(t);
#else
            return UnityEngine.Object.FindObjectOfType(t);
#endif
        }

        private static object GetPrivateField(object o, string name)
        {
            if (o == null) return null;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        private static object GetPublicProperty(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return p?.GetValue(o);
        }

        private static object GetPublicField(object o, string name)
        {
            if (o == null) return null;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                var f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                if (f != null) return f.GetValue(o);
            }
            return null;
        }

        // Public member by name, tolerant to property-vs-field: tries property first, then field.
        private static object GetMember(object o, string name)
        {
            // public property -> public field -> private field -> private property (walks base types).
            // Atlas drivers traverse many private readonly fields (e.g. ExplorePanelController.backpackController),
            // so GetMember must reach nonpublic members, not just public ones.
            object v = GetPublicProperty(o, name) ?? GetPublicField(o, name) ?? GetPrivateField(o, name);
            if (v != null || o == null) return v;
            for (Type t = o.GetType(); t != null; t = t.BaseType)
            {
                var p = t.GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance);
                if (p != null) { try { return p.GetValue(o); } catch { return null; } }
            }
            return null;
        }

        // Resolve the user's own SelfProfile across the #8996 API move: DynamicWorldContainer.selfProfile [old]
        // -> profileContainer.SelfProfile [new]. Chained old-then-new so it tolerates clients on either side;
        // all the Get* helpers null-guard their input, so the chain is NRE-safe.
        private static object ReachSelfProfile(object dynamicContainer)
            => GetMember(dynamicContainer, "selfProfile")
               ?? GetPublicProperty(GetPrivateField(dynamicContainer, "profileContainer"), "SelfProfile");

        // Reads ILoadingStatus.CurrentStage.Value (ReactiveProperty<LoadingStage>) -> enum name
        private static string ReadLoadingStage(object loadingStatus)
        {
            try
            {
                object currentStage = GetPublicProperty(loadingStatus, "CurrentStage"); // ReactiveProperty<LoadingStage>
                if (currentStage == null) return "?";
                object val = GetPublicProperty(currentStage, "Value");                  // LoadingStage enum
                return val?.ToString() ?? "?";
            }
            catch { return "?"; }
        }

        private static readonly Dictionary<string, Type> typeCache = new();
        private static Type FindType(string fullName)
        {
            if (typeCache.TryGetValue(fullName, out var cached)) return cached;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName, false);
                if (t != null) { typeCache[fullName] = t; return t; }
            }
            typeCache[fullName] = null;
            return null;
        }

        // =====================================================================
        //  AVATAR VISUAL CHECKS  (steer 2026-06-12: "verify avatars look
        //  correct and onboarding has no errors")
        // =====================================================================
        // Per avatar: AvatarAnimator must exist, be active+enabled, be driven by
        // a controller, and have cullingMode == AlwaysAnimate (DCL renders via
        // custom GPU skinning - and the preview via RenderTexture - so Unity's
        // renderer-visibility heuristic would wrongly cull transform writes;
        // exactly the d38621d4 regression that broke the new-user preview, fixed
        // by revert 7ca01ad1). Then bones must actually move within ~1.2s,
        // proving the animator writes transforms.
        // previewOnly=true  -> only avatars under CharacterPreviewAvatarContainer,
        //                      ALL must pass (run with the Backpack open).
        // previewOnly=false -> world avatars; at least ONE must fully pass (remote
        //                      avatars may legitimately be mid-load/pooled).
        private class AvatarProbe
        {
            public string name; public string fail;
            public readonly List<Transform> bones = new();
            public readonly List<Quaternion> rot = new();
            public bool Moved()
            {
                for (int i = 0; i < bones.Count; i++)
                    if (bones[i] != null && Quaternion.Angle(rot[i], bones[i].localRotation) > 0.05f) return true;
                return false;
            }
        }

        private static IEnumerator CheckAvatars(string label, Report report, bool previewOnly)
        {
            string err = null;
            var probes = new List<AvatarProbe>();

            Type avatarT  = FindType("DCL.AvatarRendering.AvatarShape.UnityInterface.AvatarBase");
            Type previewT = FindType("DCL.CharacterPreview.CharacterPreviewAvatarContainer");

            if (avatarT == null) err = "AvatarBase type not found";

            if (err == null)
            {
                foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsByType(avatarT))
                {
                    var c = (Component)o;
                    bool isPreview = previewT != null && c.GetComponentInParent(previewT) != null;
                    if (isPreview != previewOnly) continue;

                    var probe = new AvatarProbe { name = c.name };
                    probes.Add(probe);

                    var animator = GetPublicProperty(c, "AvatarAnimator") as Animator;
                    if (animator == null) { probe.fail = "AvatarAnimator is null"; continue; }
                    if (!animator.isActiveAndEnabled) { probe.fail = "animator inactive/disabled"; continue; }
                    if (animator.runtimeAnimatorController == null) { probe.fail = "no animator controller"; continue; }
                    if (animator.cullingMode != AnimatorCullingMode.AlwaysAnimate) { probe.fail = "cullingMode=" + animator.cullingMode; continue; }

                    Transform[] sub = animator.GetComponentsInChildren<Transform>();
                    int step = Mathf.Max(1, sub.Length / 32);
                    for (int i = 0; i < sub.Length; i += step) { probe.bones.Add(sub[i]); probe.rot.Add(sub[i].localRotation); }
                }

                if (probes.Count == 0)
                    err = previewOnly ? "no character-preview avatar found with the Backpack open"
                                      : "no world avatar found after spawn";
            }

            if (err == null)
            {
                float until = UnityEngine.Time.realtimeSinceStartup + 1.2f;
                while (UnityEngine.Time.realtimeSinceStartup < until) yield return null;

                foreach (AvatarProbe p in probes)
                    if (p.fail == null && !p.Moved())
                        p.fail = "bones static over 1.2s (" + p.bones.Count + " sampled) - animator not writing transforms";

                int okCount = probes.Count(p => p.fail == null);

                // Categorize failures. Structural failures (null/inactive/no-controller/
                // wrong cull mode) are real defects on ANY avatar - they're the
                // d38621d4 regression class. "static" (no bone motion in 1.2s) is
                // EXPECTED for distant/idle/just-spawned remote avatars (verified
                // 2026-06-13: a stable ~10 world avatars are static while structural
                // counts stay 0), so it's informational for world avatars and only a
                // hard failure for the close-up character preview (which must animate).
                int nNull = 0, nInactive = 0, nNoCtrl = 0, nCulled = 0, nStatic = 0;
                AvatarProbe structuralBad = null;
                foreach (AvatarProbe p in probes)
                {
                    if (p.fail == null) continue;
                    if (p.fail.StartsWith("AvatarAnimator is null")) { nNull++; structuralBad ??= p; }
                    else if (p.fail.StartsWith("animator inactive")) { nInactive++; structuralBad ??= p; }
                    else if (p.fail.StartsWith("no animator controller")) { nNoCtrl++; structuralBad ??= p; }
                    else if (p.fail.StartsWith("cullingMode=")) { nCulled++; structuralBad ??= p; }
                    else if (p.fail.StartsWith("bones static")) nStatic++;
                }

                bool structurallySound = nNull == 0 && nInactive == 0 && nNoCtrl == 0 && nCulled == 0;
                bool pass = previewOnly ? okCount == probes.Count : structurallySound;

                if (!pass)
                {
                    AvatarProbe bad = structuralBad ?? probes.First(p => p.fail != null);
                    err = bad.name + ": " + bad.fail + " (" + okCount + "/" + probes.Count + " avatars ok; "
                        + "structural fails null=" + nNull + " inactive=" + nInactive + " noCtrl=" + nNoCtrl + " culled=" + nCulled + ")";
                }

                Debug.Log("[Harness] " + label + ": avatars=" + probes.Count + " ok=" + okCount
                    + " | fails: nullAnim=" + nNull + " inactive=" + nInactive + " noCtrl=" + nNoCtrl
                    + " culled=" + nCulled + " static=" + nStatic + " (static=informational for world avatars)"
                    + (err == null ? "" : " FAIL: " + err));
            }

            report.actions.Add(new PhaseMarker { label = label, ok = err == null, error = err });

            // Visual record of the avatar(s) just checked (world avatar / preview).
            yield return CaptureShot(label);
        }

        // =====================================================================
        //  COMMS HOLD  (live multi-client comms test, 2026-06-22 user request)
        // =====================================================================
        // Genesis Plaza spawn is ~(0,0); teleport there explicitly so all three test
        // clients converge on the same parcel. CENSUS_S = how often we log the remote-
        // avatar count + refresh the world shot while holding.
        private const float CENSUS_S = 5f;
        private static readonly Vector2Int GENESIS_PLAZA = new Vector2Int(0, 0);
        internal static int commsLastTotalAvatars = -1, commsLastRemoteAvatars = -1;

        // Count world (non-preview) avatars currently instantiated. The local player is
        // one of them, so remote = total - 1. Mirrors the CheckAvatars enumeration.
        public static int CountWorldAvatars(out string names)
        {
            names = "";
            Type avatarT  = FindType("DCL.AvatarRendering.AvatarShape.UnityInterface.AvatarBase");
            Type previewT = FindType("DCL.CharacterPreview.CharacterPreviewAvatarContainer");
            if (avatarT == null) return -1;
            var found = new List<string>();
            foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsByType(avatarT))
            {
                var c = (Component)o;
                bool isPreview = previewT != null && c.GetComponentInParent(previewT) != null;
                if (isPreview) continue;   // backpack character-preview avatar, not a world peer
                found.Add(c.name);
            }
            names = string.Join(", ", found);
            return found.Count;
        }

        // Join -> interactive -> teleport to GP(0,0) -> HOLD forever (until Play exits).
        // Never teleports away, never switches realm, never exits Play. The HOLD loop is
        // what keeps this client present so peers see it; the census is how we report who
        // we see. On our stack, NOT reaching Completed means the livekit room was refused
        // (parks at GlobalPXsLoading) — we still hold + shoot so the failure is visible.
        private static IEnumerator RunCommsHoldCoroutine()
        {
            var report = new Report { startedUtc = DateTime.UtcNow.ToString("o") };
            var sw = System.Diagnostics.Stopwatch.StartNew();

            shotIndex = 0;
            try
            {
                if (Directory.Exists(SHOTS_DIR)) Directory.Delete(SHOTS_DIR, true);
                Directory.CreateDirectory(SHOTS_DIR);
            }
            catch (Exception e) { Debug.LogWarning("[Harness] comms: could not reset shots dir: " + e.Message); }

            for (int i = 0; i < 3 && EditorApplication.isPlaying; i++) yield return null;

            object mainSceneLoader = null;
            float findDeadline = UnityEngine.Time.realtimeSinceStartup + 30f;
            while (mainSceneLoader == null && UnityEngine.Time.realtimeSinceStartup < findDeadline)
            {
                mainSceneLoader = FindMainSceneLoader();
                if (mainSceneLoader == null) yield return null;
            }
            if (mainSceneLoader == null) { report.fatal = "comms: no MainSceneLoader"; Finish(report, sw); yield break; }

            // Wait for interactive (Completed) — the livekit join gate on our stack.
            object loadingStatus = null;
            float ttiStart = UnityEngine.Time.realtimeSinceStartup;
            float ttiDeadline = ttiStart + LOAD_TIMEOUT_S;
            bool reachedInteractive = false;
            while (UnityEngine.Time.realtimeSinceStartup < ttiDeadline)
            {
                if (loadingStatus == null)
                {
                    var sc = GetPrivateField(mainSceneLoader, "staticContainer");
                    if (sc != null) loadingStatus = GetPublicProperty(sc, "LoadingStatus");
                }
                if (loadingStatus != null)
                {
                    string stage = ReadLoadingStage(loadingStatus);
                    report.lastLoadingStage = stage;
                    if (stage == "Completed") { reachedInteractive = true; break; }
                }
                yield return null;
            }
            report.reachedInteractive = reachedInteractive;
            report.timeToInteractiveSeconds = reachedInteractive ? UnityEngine.Time.realtimeSinceStartup - ttiStart : -1f;
            Debug.Log($"[Harness] COMMS reachedInteractive={reachedInteractive} stage={report.lastLoadingStage} TTI={report.timeToInteractiveSeconds:F1}s");

            object dynamicContainer = GetPrivateField(mainSceneLoader, "dynamicWorldContainer");
            object staticContainer2 = GetPrivateField(mainSceneLoader, "staticContainer");
            object realmNavigator   = dynamicContainer != null ? GetPublicProperty(dynamicContainer, "RealmNavigator") : null;

            SetGameViewSize16x9(captureW, captureH);
            HideDebugPanel(staticContainer2);

            if (!reachedInteractive)
                Debug.LogWarning("[Harness] COMMS not interactive (likely livekit room refused at GlobalPXsLoading); holding anyway to report state.");

            // Teleport to Genesis Plaza (0,0) so all three clients converge, then settle.
            if (realmNavigator != null)
            {
                bool tok = TryTeleport(realmNavigator, GENESIS_PLAZA, out string terr);
                report.actions.Add(new PhaseMarker { label = "comms_teleport_gp", ok = tok, error = tok ? "to 0,0" : terr });
                Debug.Log("[Harness] COMMS teleport to GP 0,0 ok=" + tok + (tok ? "" : " err=" + terr));
                for (int i = 0; i < 300 && tok; i++) yield return null;   // ~5s settle for scene + peer streaming
            }

            // ----- HOLD: census every CENSUS_S, shot every ~30s, until Play exits -----
            Debug.Log("[Harness] COMMS HOLD begins at GP 0,0. Census every " + CENSUS_S + "s; shot every ~30s. Exit Play to stop.");
            int tick = 0;
            while (EditorApplication.isPlaying)
            {
                int total = CountWorldAvatars(out string names);
                int remote = Mathf.Max(0, total - 1);   // subtract the local player avatar
                commsLastTotalAvatars = total; commsLastRemoteAvatars = remote;
                Debug.Log($"[Harness] COMMS census tick={tick} world_avatars={total} remote={remote} names=[{names}]");
                if (tick % 6 == 0) yield return CaptureShot("comms_gp");   // ~every 30s, keep a fresh world shot
                float until = UnityEngine.Time.realtimeSinceStartup + CENSUS_S;
                while (UnityEngine.Time.realtimeSinceStartup < until && EditorApplication.isPlaying) yield return null;
                tick++;
            }
            Finish(report, sw);
        }

        // =====================================================================
        //  SCREENSHOTS  (steer 2026-06-13: capture settled state; 2026-06-16:
        //  panels/settings/friends + avatars only, forced ~16:9 game view)
        // =====================================================================
        // Force the editor Game View to a fixed 16:9 resolution so ScreenCapture
        // grabs ~16:9 PNGs instead of whatever thin strip the docked window is
        // (was 733x282 = 2.6:1). Uses the GameViewSizes reflection API; any failure
        // is non-fatal (screenshots still capture, just at the window aspect).
        private static void SetGameViewSize16x9(int width, int height)
        {
            try
            {
                Assembly editorAsm = typeof(UnityEditor.Editor).Assembly; // UnityEditor.dll
                Type sizesType = editorAsm.GetType("UnityEditor.GameViewSizes");
                Type singleton = typeof(ScriptableSingleton<>).MakeGenericType(sizesType);
                object sizes = singleton.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);

                object currentGroupType = sizesType.GetProperty("currentGroupType").GetValue(sizes);
                object group = sizesType.GetMethod("GetGroup").Invoke(sizes, new object[] { (int)currentGroupType });
                Type groupType = group.GetType();

                int builtin = (int)groupType.GetMethod("GetBuiltinCount").Invoke(group, null);
                int custom  = (int)groupType.GetMethod("GetCustomCount").Invoke(group, null);
                int total   = builtin + custom;
                MethodInfo getSize = groupType.GetMethod("GetGameViewSize");

                Type gvsType = editorAsm.GetType("UnityEditor.GameViewSize");
                PropertyInfo wProp = gvsType.GetProperty("width");
                PropertyInfo hProp = gvsType.GetProperty("height");

                int index = -1;
                for (int i = 0; i < total; i++)
                {
                    object s = getSize.Invoke(group, new object[] { i });
                    if ((int)wProp.GetValue(s) == width && (int)hProp.GetValue(s) == height) { index = i; break; }
                }

                if (index < 0)
                {
                    Type gvsTypeEnum = editorAsm.GetType("UnityEditor.GameViewSizeType");
                    ConstructorInfo ctor = gvsType.GetConstructor(new[] { gvsTypeEnum, typeof(int), typeof(int), typeof(string) });
                    object newSize = ctor.Invoke(new object[] { Enum.Parse(gvsTypeEnum, "FixedResolution"), width, height, "Harness16x9" });
                    groupType.GetMethod("AddCustomSize").Invoke(group, new object[] { newSize });
                    index = builtin + (int)groupType.GetMethod("GetCustomCount").Invoke(group, null) - 1;
                }

                Type gameViewType = editorAsm.GetType("UnityEditor.GameView");
                EditorWindow gv = EditorWindow.GetWindow(gameViewType, false, null, false);
                MethodInfo sizeSel = gameViewType.GetMethod("SizeSelectionCallback",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (sizeSel != null) sizeSel.Invoke(gv, new object[] { index, null });
                if (gv != null) gv.Repaint();
                Debug.Log("[Harness] GameView size -> " + width + "x" + height + " (index " + index + ")");
            }
            catch (Exception e) { Debug.LogWarning("[Harness] SetGameViewSize16x9 failed (non-fatal): " + e.Message); }
        }

        // ScreenCapture.CaptureScreenshot writes the Game View to a PNG at the
        // next end-of-frame, so we yield a few frames for the file to flush.
        // Files are prefixed with a zero-padded global index to keep capture
        // order obvious when reviewing the folder.
        private static IEnumerator CaptureShot(string label)
        {
            // SETTLE PROGRESSION (wall-clock, framerate-independent): for
            // atlasSettleSeconds seconds (default 8) take ONE screenshot per second
            // so we can watch the screen populate. The LAST shot is the canonical
            // fully-settled frame and keeps the bare route name (consolidates to the
            // named atlas); the per-second intermediates are saved as <label>_sN for
            // review and ignored by consolidation. Env: DCL_ATLAS_SETTLE_SECONDS.
            // [Mac-parity addition — keep in sync with the VM harness master.]
            int secs = atlasSettleSeconds < 1 ? 1 : atlasSettleSeconds;
            for (int s = 1; s <= secs; s++)
            {
                float until = UnityEngine.Time.realtimeSinceStartup + 1f;          // wait ~1 second
                while (UnityEngine.Time.realtimeSinceStartup < until) yield return null;
                WriteShot(s == secs ? label : (label + "_s" + s));     // last = final/canonical
            }
            for (int i = 0; i < 16; i++) yield return null;            // flush the final PNG
        }

        // One screenshot to SHOTS_DIR/NNN_<label>.png (zero-padded global index keeps
        // capture order obvious). ScreenCapture writes the Game View at end-of-frame.
        private static void WriteShot(string label)
        {
            string safe = label;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            string file = Path.Combine(SHOTS_DIR, shotIndex.ToString("D3") + "_" + safe + ".png");
            shotIndex++;
            try { ScreenCapture.CaptureScreenshot(file, captureSuper); }  // captureW*super x captureH*super
            catch (Exception e) { Debug.LogWarning("[Harness] screenshot '" + label + "' failed: " + e.Message); }
        }

        // =====================================================================
        //  LOG CAPTURE + REPORT WRITE
        // =====================================================================
        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            totalLogCount++;
            if (type == LogType.Warning)
                lock (warnings) { if (warnings.Count < 500) warnings.Add(new LogEntry { message = condition, stack = Trim(stackTrace), type = type.ToString(), t = Now() }); }
            else if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                lock (errors) { if (errors.Count < 500) errors.Add(new LogEntry { message = condition, stack = Trim(stackTrace), type = type.ToString(), t = Now() }); }
        }

        private static double Now() => EditorApplication.timeSinceStartup;
        private static string Trim(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 600 ? s.Substring(0, 600) : s);

        private static void Finish(Report report, System.Diagnostics.Stopwatch sw)
        {
            Application.logMessageReceivedThreaded -= OnLog;
            report.totalLogMessages = totalLogCount;
            report.warningCount = warnings.Count;
            report.errorCount   = errors.Count;
            report.warnings     = warnings.ToList();
            report.errors       = errors.ToList();
            report.finishedUtc  = DateTime.UtcNow.ToString("o");
            report.totalWallSeconds = sw.Elapsed.TotalSeconds;

            try
            {
                string json = report.ToJson();
                File.WriteAllText(REPORT_PATH, json, new UTF8Encoding(false));
                Debug.Log($"[Harness] Report written to {REPORT_PATH} ({json.Length} bytes). " +
                          $"warnings={report.warningCount} errors={report.errorCount} tti={report.timeToInteractiveSeconds:F1}s");
            }
            catch (Exception e) { Debug.LogError("[Harness] Failed to write report: " + e); }

            // The report is already written above (in Play mode). ExitPlaymode()
            // triggers a play->edit domain reload that would kill any coroutine here,
            // so we DON'T quit from a coroutine. Instead arm a flag; after the reload
            // OnPlayModeStateChanged(EnteredEditMode) performs EditorApplication.Exit.
            if (_exitOnFinish)
            {
                SessionState.SetBool(KEY_QUIT, true);
                SessionState.SetBool(KEY_EXIT, true);
            }
            if (EditorApplication.isPlaying)
                EditorApplication.ExitPlaymode();
        }

        // =====================================================================
        //  PERF MEASUREMENT INFRASTRUCTURE  (shared by perf/cpu/shadow/render)
        // =====================================================================
        //  Frame-pacing control. With vSync on, "Main Thread" frame time is capped to
        //  the display refresh, so a genuinely-cheaper A block reports the SAME ms as B
        //  and the paired A/B delta collapses toward zero — silently UNDER-reporting the
        //  cost of every toggle. Force vSync off + uncap the frame rate for the duration
        //  of a measurement so frame time reflects real work, then restore.
        private static int  _savedVSync;
        private static int  _savedTargetFps;
        private static bool _pacingOverridden;
        private static void BeginPerfPacing()
        {
            if (_pacingOverridden) return;
            _savedVSync     = QualitySettings.vSyncCount;
            _savedTargetFps = Application.targetFrameRate;
            QualitySettings.vSyncCount  = 0;     // do not block on present
            Application.targetFrameRate = -1;    // uncap
            _pacingOverridden = true;
        }
        private static void EndPerfPacing()
        {
            if (!_pacingOverridden) return;
            QualitySettings.vSyncCount  = _savedVSync;
            Application.targetFrameRate = _savedTargetFps;
            _pacingOverridden = false;
        }

        // Verify the gating ProfilerRecorders actually produce data on THIS Unity/URP
        // build. An unknown counter name yields a permanently-invalid recorder that
        // samples nothing — so a benchmark that measured NOTHING would otherwise report
        // 0ms and look "fast". Logs a loud, named error/warning and returns a note for
        // the run-meta sidecar so the miss is visible, never silent.
        private static string VerifyGatingCounters(string tag, ProfilerRecorder cpu, ProfilerRecorder gpu)
        {
            bool cpuOk = cpu.Valid, gpuOk = gpu.Valid;
            if (!cpuOk) Debug.LogError($"[{tag}] CPU counter 'Main Thread' UNAVAILABLE on this build — cpu_ms cannot be measured (not 'fast', MISSING). Check ProfilerCategory/name for this Unity version.");
            if (!gpuOk) Debug.LogWarning($"[{tag}] GPU counter 'GPU Frame Time' unavailable (common in-Editor / GPU profiling off) — gpu_ms will be empty/invalid, NOT zero.");
            return $"cpu={(cpuOk ? "ok" : "UNAVAILABLE")} gpu={(gpuOk ? "ok" : "unavailable")}";
        }

        // Reproducibility: write a sidecar <csv>.meta.json capturing the environment the
        // numbers were measured in. Without it the CSV is not comparable across machines.
        private static void WriteRunMeta(string csvPath, string tag, string counterNote, string extra)
        {
            try
            {
                string Q(string s) => "\"" + (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
                var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
                string meta =
                    "{" +
                    "\"tag\":" + Q(tag) +
                    ",\"utc\":" + Q(DateTime.UtcNow.ToString("o")) +
                    ",\"unityVersion\":" + Q(Application.unityVersion) +
                    ",\"platform\":" + Q(Application.platform.ToString()) +
                    ",\"graphicsDevice\":" + Q(SystemInfo.graphicsDeviceName) +
                    ",\"graphicsApi\":" + Q(SystemInfo.graphicsDeviceType.ToString()) +
                    ",\"renderPipeline\":" + Q(urp != null ? urp.GetType().Name : "BuiltIn") +
                    ",\"vSyncCount\":" + QualitySettings.vSyncCount +
                    ",\"targetFrameRate\":" + Application.targetFrameRate +
                    ",\"screen\":" + Q(Screen.width + "x" + Screen.height) +
                    ",\"counters\":" + Q(counterNote) +
                    (string.IsNullOrEmpty(extra) ? "" : "," + extra) +
                    "}";
                File.WriteAllText(csvPath + ".meta.json", meta, new UTF8Encoding(false));
            }
            catch (Exception e) { Debug.LogWarning($"[{tag}] meta write failed: " + e.Message); }
        }

        // Shared "wait until interactive" bootstrap, replacing the verbatim copy that used
        // to open each telemetry coroutine: 3 frames -> find MainSceneLoader (30s) -> poll
        // LoadingStatus to Completed (LOAD_TIMEOUT_S) -> resolve the containers every mode
        // needs. On failure sets ctx.err to a NAMED reason so client drift (a renamed
        // member -> null reach) is distinguishable from a genuine load timeout.
        private sealed class BootCtx
        {
            public object loader, staticContainer, loadingStatus, dynamicContainer, mvcManager, realmNavigator;
            public string err;
        }
        private static IEnumerator Bootstrap(BootCtx ctx)
        {
            for (int i = 0; i < 3 && EditorApplication.isPlaying; i++) yield return null;

            float findDeadline = UnityEngine.Time.realtimeSinceStartup + 30f;
            while (ctx.loader == null && UnityEngine.Time.realtimeSinceStartup < findDeadline)
            {
                ctx.loader = FindMainSceneLoader();
                if (ctx.loader == null) yield return null;
            }
            if (ctx.loader == null) { ctx.err = "MainSceneLoader not found (renamed, or boot scene not loaded)"; yield break; }

            float ttiDeadline = UnityEngine.Time.realtimeSinceStartup + LOAD_TIMEOUT_S;
            bool reached = false;
            while (UnityEngine.Time.realtimeSinceStartup < ttiDeadline && EditorApplication.isPlaying)
            {
                if (ctx.staticContainer == null) ctx.staticContainer = GetPrivateField(ctx.loader, "staticContainer");
                if (ctx.staticContainer != null)
                {
                    if (ctx.loadingStatus == null) ctx.loadingStatus = GetPublicProperty(ctx.staticContainer, "LoadingStatus");
                    if (ctx.loadingStatus != null && ReadLoadingStage(ctx.loadingStatus) == "Completed") { reached = true; break; }
                }
                yield return null;
            }
            if (!reached) { ctx.err = "never reached interactive (LoadingStatus.CurrentStage != Completed within " + LOAD_TIMEOUT_S + "s)"; yield break; }

            ctx.dynamicContainer = GetPrivateField(ctx.loader, "dynamicWorldContainer");
            if (ctx.dynamicContainer != null)
            {
                ctx.mvcManager     = GetPublicProperty(ctx.dynamicContainer, "MvcManager");
                ctx.realmNavigator = GetPublicProperty(ctx.dynamicContainer, "RealmNavigator");
            }
            SchemaCheck(ctx.loader, ctx.staticContainer, ctx.dynamicContainer);   // loud, named drift report
        }

        // Unified abort path for the telemetry modes (was 4 identical PerfFail/CpuFail/
        // ShadowFail/RenderFail bodies). Restores frame-pacing, writes the ERROR row the
        // analyzers special-case, and triggers the deferred quit.
        private static void FailMode(string tag, string csvPath, string why)
        {
            Debug.LogError($"[{tag}] aborted: {why}");
            EndPerfPacing();
            try { File.WriteAllText(csvPath, "ERROR," + why, new UTF8Encoding(false)); } catch { }
            if (_exitOnFinish) { SessionState.SetBool(KEY_QUIT, true); SessionState.SetBool(KEY_EXIT, true); }
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
        }

        // =====================================================================
        //  PERF BENCHMARK COROUTINE  (paired within-session A/B; differential
        //  isolation of the Backpack character-preview render pipeline)
        // =====================================================================
        private static IEnumerator RunPerfCoroutine()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (PERF_WINDOWS % 4 != 0) Debug.LogWarning($"[Perf] PERF_WINDOWS={PERF_WINDOWS} is not a multiple of 4 — ABBA blocks will be unbalanced.");

            // --- bootstrap: shared wait-until-interactive ---
            var ctx = new BootCtx();
            yield return Bootstrap(ctx);
            if (ctx.err != null) { PerfFail(ctx.err); yield break; }
            object mvcManager = ctx.mvcManager;
            if (mvcManager == null) { PerfFail("no MvcManager (dynamicWorldContainer.MvcManager unreachable)"); yield break; }

            // --- open the Backpack so the preview avatar + its camera exist ---
            if (!TryOpenExplorePanel(mvcManager, "Backpack", null, out string operr)) { PerfFail("open Backpack: " + operr); yield break; }
            // let the panel show + preview avatar stream in + idle anim start
            float settle = UnityEngine.Time.realtimeSinceStartup + 8f;
            while (UnityEngine.Time.realtimeSinceStartup < settle) yield return null;

            // --- locate the preview camera (renders to the "Preview Texture" RT) ---
            Camera previewCam = FindPreviewCamera();
            if (previewCam == null) { PerfFail("preview camera not found"); yield break; }
            Debug.Log($"[Perf] preview camera = '{previewCam.name}' targetTex='{previewCam.targetTexture?.name}'");

            // --- recorders + frame-pacing control ---
            var mainThread = new ProfilerRecorder(ProfilerCategory.Internal, "Main Thread", 64);
            var gpuFrame   = new ProfilerRecorder(ProfilerCategory.Render,   "GPU Frame Time", 64);
            mainThread.Start(); gpuFrame.Start();
            BeginPerfPacing();
            yield return null; yield return null;                          // let pacing + recorders settle
            string counterNote = VerifyGatingCounters("Perf", mainThread, gpuFrame);

            // --- warmup (discarded) ---
            float warmEnd = UnityEngine.Time.realtimeSinceStartup + PERF_WARMUP_S;
            while (UnityEngine.Time.realtimeSinceStartup < warmEnd && EditorApplication.isPlaying) yield return null;

            // --- benchmark loop: ABBA-counterbalanced windows, A=cam on, B=cam off ---
            var rows = new List<string>(8192);
            rows.Add("window,cond,frame_in_window,t_ms,cpu_ms,gpu_ms,drop");
            int frameGlobal = 0;
            bool prevEnabled = previewCam.enabled;
            for (int w = 0; w < PERF_WINDOWS && EditorApplication.isPlaying; w++)
            {
                int block = w % 4;                          // ABBA: 0->A 1->B 2->B 3->A
                bool condA = (block == 0 || block == 3);
                if (previewCam != null) previewCam.enabled = condA;   // A renders the preview, B skips it

                int fInWin = 0;
                float wEnd = UnityEngine.Time.realtimeSinceStartup + PERF_WINDOW_S;
                while (UnityEngine.Time.realtimeSinceStartup < wEnd && EditorApplication.isPlaying)
                {
                    double cpu = (mainThread.Valid && mainThread.LastValue > 0) ? mainThread.LastValue * 1e-6 : -1;
                    double gpu = (gpuFrame.Valid && gpuFrame.LastValue > 0) ? gpuFrame.LastValue * 1e-6 : -1;
                    int drop = fInWin < PERF_DROP_FRAMES ? 1 : 0;   // transition frames flagged for the analyzer
                    rows.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F1},{4:F4},{5:F4},{6}",
                                           w, condA ? "A" : "B", fInWin, sw.Elapsed.TotalMilliseconds, cpu, gpu, drop));
                    fInWin++; frameGlobal++;
                    yield return null;
                }
            }

            if (previewCam != null) previewCam.enabled = prevEnabled;   // restore
            EndPerfPacing();
            mainThread.Dispose(); gpuFrame.Dispose();

            try
            {
                File.WriteAllText(PERF_CSV_PATH, string.Join("\n", rows), new UTF8Encoding(false));
                WriteRunMeta(PERF_CSV_PATH, "Perf", counterNote,
                             $"\"windows\":{PERF_WINDOWS},\"windowSeconds\":{PERF_WINDOW_S.ToString(CultureInfo.InvariantCulture)},\"dropFrames\":{PERF_DROP_FRAMES},\"samples\":{rows.Count - 1}");
                Debug.Log($"[Perf] wrote {rows.Count - 1} samples over {PERF_WINDOWS} windows to {PERF_CSV_PATH} ({sw.Elapsed.TotalSeconds:F0}s wall)");
            }
            catch (Exception e) { Debug.LogError("[Perf] CSV write failed: " + e); }

            // exit play + quit (mirror Finish's deferred-quit mechanism)
            if (_exitOnFinish) { SessionState.SetBool(KEY_QUIT, true); SessionState.SetBool(KEY_EXIT, true); }
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
        }

        private static void PerfFail(string why) => FailMode("Perf", PERF_CSV_PATH, why);

        // The character-preview camera renders to an off-screen RT named "Preview Texture"
        // (CharacterPreviewControllerBase.cs). The main-world camera renders to the screen
        // (targetTexture == null), so this uniquely identifies the preview camera.
        private static Camera FindPreviewCamera()
        {
            var cams = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include);
            Camera anyRT = null;
            foreach (var c in cams)
            {
                if (c.targetTexture == null) continue;
                anyRT = c;
                if (c.targetTexture.name == "Preview Texture") return c;
            }
            return anyRT; // fall back to any RT camera if the name ever changes
        }

        // =====================================================================
        //  CPU BREAKDOWN COROUTINE  (enumerate every time-unit profiler marker at
        //  runtime, attribute per-frame ms at steady idle -> what eats the frame)
        // =====================================================================
        private static IEnumerator RunCpuBreakdownCoroutine()
        {
            var ctx = new BootCtx();
            yield return Bootstrap(ctx);
            if (ctx.err != null) { CpuFail(ctx.err); yield break; }

            // settle to steady state at spawn (idle, no panel)
            float settle = UnityEngine.Time.realtimeSinceStartup + CPU_SETTLE_S;
            while (UnityEngine.Time.realtimeSinceStartup < settle && EditorApplication.isPlaying) yield return null;

            // enumerate ALL time-unit (CPU-timer) markers and open a recorder for each
            var handles = new List<Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle>();
            Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetAvailable(handles);
            var names = new List<string>(); var cats = new List<string>(); var recs = new List<ProfilerRecorder>();
            foreach (var h in handles)
            {
                Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderDescription d;
                try { d = Unity.Profiling.LowLevel.Unsafe.ProfilerRecorderHandle.GetDescription(h); }
                catch { continue; }
                if (d.UnitType.ToString() != "TimeNanoseconds") continue;   // CPU-time markers only (the enum's namespace is version-unstable across Unity 6 point releases; string compare is the deliberate portable choice)
                ProfilerRecorder r;
                try { r = new ProfilerRecorder(h, 1, ProfilerRecorderOptions.Default); r.Start(); }
                catch { continue; }
                names.Add(d.Name); cats.Add(d.Category.ToString()); recs.Add(r);
                if (recs.Count >= CPU_MAX_MARKERS) break;
            }
            Debug.Log($"[CPU] sampling {recs.Count} time markers");
            if (recs.Count == 0) { CpuFail("no TimeNanoseconds profiler markers found (ProfilerMarkerDataUnit mismatch / profiler off?)"); yield break; }
            BeginPerfPacing();

            float we = UnityEngine.Time.realtimeSinceStartup + CPU_WARMUP_S;
            while (UnityEngine.Time.realtimeSinceStartup < we && EditorApplication.isPlaying) yield return null;

            var sum = new double[recs.Count]; var present = new long[recs.Count];
            int frames = 0;
            float end = UnityEngine.Time.realtimeSinceStartup + CPU_SAMPLE_S;
            while (UnityEngine.Time.realtimeSinceStartup < end && EditorApplication.isPlaying)
            {
                for (int i = 0; i < recs.Count; i++)
                {
                    var r = recs[i];
                    if (r.Valid && r.LastValue > 0) { sum[i] += r.LastValue * 1e-6; present[i]++; }
                }
                frames++;
                yield return null;
            }

            EndPerfPacing();
            var idx = new List<int>();
            for (int i = 0; i < recs.Count; i++) idx.Add(i);
            idx.Sort((a, b) => sum[b].CompareTo(sum[a]));   // by total ms desc
            // avg_ms_when_present (sum/present) sits alongside avg_ms_per_frame (sum/frames): a
            // bursty marker that costs 8ms but fires 1-in-10 frames reads 0.8 per-frame but 8.0
            // when-present, so it can be ranked on the intended one (column appended for compat).
            var rows = new List<string> { "marker,category,avg_ms_per_frame,frames_present,frames_total,avg_ms_when_present" };
            int written = 0, dropped = 0;
            foreach (int i in idx)
            {
                if (written >= CPU_TOP_N) break;
                double avg = frames > 0 ? sum[i] / frames : 0;
                double avgPresent = present[i] > 0 ? sum[i] / present[i] : 0;
                if (avg <= 0) continue;
                if (avg > 5000) { Debug.LogWarning($"[CPU] dropped suspicious marker '{names[i]}' ({cats[i]}) avg={avg:F1}ms (>5000ms; likely a non-time unit mislabeled) — NOT silently hidden"); dropped++; continue; }
                string nm = names[i].Replace(",", ";").Replace("\n", " ");
                rows.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2:F4},{3},{4},{5:F4}", nm, cats[i], avg, present[i], frames, avgPresent));
                written++;
            }
            try
            {
                File.WriteAllText(CPU_CSV_PATH, string.Join("\n", rows), new UTF8Encoding(false));
                WriteRunMeta(CPU_CSV_PATH, "CPU", $"markers={recs.Count}", $"\"markersWritten\":{written},\"markersDropped\":{dropped},\"framesSampled\":{frames}");
                Debug.Log($"[CPU] wrote top {written} markers to {CPU_CSV_PATH} ({frames} frames sampled, {dropped} dropped)");
            }
            catch (Exception e) { Debug.LogError("[CPU] csv write failed: " + e); }

            foreach (var r in recs) r.Dispose();
            if (_exitOnFinish) { SessionState.SetBool(KEY_QUIT, true); SessionState.SetBool(KEY_EXIT, true); }
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
        }

        private static void CpuFail(string why) => FailMode("CPU", CPU_CSV_PATH, why);

        // =====================================================================
        //  SHADOW A/B COROUTINE  (paired within-session toggle of all shadow-casting
        //  lights at the rendered spawn scene -> true CPU+GPU cost of shadows)
        // =====================================================================
        private static IEnumerator RunShadowPerfCoroutine()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (PERF_WINDOWS % 4 != 0) Debug.LogWarning($"[Shadow] PERF_WINDOWS={PERF_WINDOWS} is not a multiple of 4 — ABBA blocks will be unbalanced.");

            var ctx = new BootCtx();
            yield return Bootstrap(ctx);
            if (ctx.err != null) { ShadowFail(ctx.err); yield break; }

            // settle so the world (shadow casters) streams in
            float settle = UnityEngine.Time.realtimeSinceStartup + SHADOW_SETTLE_S;
            while (UnityEngine.Time.realtimeSinceStartup < settle && EditorApplication.isPlaying) yield return null;

            // snapshot all lights' original shadow setting; A = original (on), B = None (off)
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
            var orig = new LightShadows[lights.Length];
            int casters = 0;
            for (int i = 0; i < lights.Length; i++) { orig[i] = lights[i].shadows; if (orig[i] != LightShadows.None) casters++; }
            if (casters == 0) { ShadowFail("no shadow-casting lights present"); yield break; }
            Debug.Log($"[Shadow] {lights.Length} lights, {casters} casting shadows");

            var mainThread = new ProfilerRecorder(ProfilerCategory.Internal, "Main Thread", 64);
            var gpuFrame   = new ProfilerRecorder(ProfilerCategory.Render,   "GPU Frame Time", 64);
            mainThread.Start(); gpuFrame.Start();
            BeginPerfPacing();
            yield return null; yield return null;
            string counterNote = VerifyGatingCounters("Shadow", mainThread, gpuFrame);

            float we = UnityEngine.Time.realtimeSinceStartup + PERF_WARMUP_S;
            while (UnityEngine.Time.realtimeSinceStartup < we && EditorApplication.isPlaying) yield return null;

            var rows = new List<string>(8192);
            rows.Add("window,cond,frame_in_window,t_ms,cpu_ms,gpu_ms,drop");
            for (int w = 0; w < PERF_WINDOWS && EditorApplication.isPlaying; w++)
            {
                int block = w % 4; bool condA = (block == 0 || block == 3);  // A = shadows ON, B = OFF
                for (int i = 0; i < lights.Length; i++) if (lights[i] != null) lights[i].shadows = condA ? orig[i] : LightShadows.None;

                int fInWin = 0; float wEnd = UnityEngine.Time.realtimeSinceStartup + PERF_WINDOW_S;
                while (UnityEngine.Time.realtimeSinceStartup < wEnd && EditorApplication.isPlaying)
                {
                    double cpu = (mainThread.Valid && mainThread.LastValue > 0) ? mainThread.LastValue * 1e-6 : -1;
                    double gpu = (gpuFrame.Valid && gpuFrame.LastValue > 0) ? gpuFrame.LastValue * 1e-6 : -1;
                    int drop = fInWin < PERF_DROP_FRAMES ? 1 : 0;
                    rows.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3:F1},{4:F4},{5:F4},{6}",
                                           w, condA ? "A" : "B", fInWin, sw.Elapsed.TotalMilliseconds, cpu, gpu, drop));
                    fInWin++; yield return null;
                }
            }
            for (int i = 0; i < lights.Length; i++) if (lights[i] != null) lights[i].shadows = orig[i];  // restore
            EndPerfPacing();
            mainThread.Dispose(); gpuFrame.Dispose();

            try
            {
                File.WriteAllText(SHADOW_CSV_PATH, string.Join("\n", rows), new UTF8Encoding(false));
                WriteRunMeta(SHADOW_CSV_PATH, "Shadow", counterNote, $"\"windows\":{PERF_WINDOWS},\"shadowCasters\":{casters},\"samples\":{rows.Count - 1}");
                Debug.Log($"[Shadow] wrote {rows.Count - 1} samples ({casters} shadow lights) to {SHADOW_CSV_PATH}");
            }
            catch (Exception e) { Debug.LogError("[Shadow] csv write failed: " + e); }

            if (_exitOnFinish) { SessionState.SetBool(KEY_QUIT, true); SessionState.SetBool(KEY_EXIT, true); }
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
        }

        private static void ShadowFail(string why) => FailMode("Shadow", SHADOW_CSV_PATH, why);

        // =====================================================================
        //  RENDER DECOMPOSITION  (A/B each rendering knob on/off in its own block;
        //  per-knob marginal CPU+GPU ms via the same paired differential)
        // =====================================================================
        private static bool SetProp(object o, string name, object val)
        {
            if (o == null) return false;
            var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (p == null || !p.CanWrite) return false;
            try { p.SetValue(o, val); return true; } catch { return false; }
        }

        private static IEnumerator RunRenderDecompCoroutine()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (RENDER_WINDOWS_PER_KNOB % 4 != 0) Debug.LogWarning($"[Render] RENDER_WINDOWS_PER_KNOB={RENDER_WINDOWS_PER_KNOB} is not a multiple of 4 — ABBA blocks will be unbalanced.");

            var ctx = new BootCtx();
            yield return Bootstrap(ctx);
            if (ctx.err != null) { RenderFail(ctx.err); yield break; }

            float settle = UnityEngine.Time.realtimeSinceStartup + SHADOW_SETTLE_S;
            while (UnityEngine.Time.realtimeSinceStartup < settle && EditorApplication.isPlaying) yield return null;

            // --- gather targets + originals ---
            var lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include);
            var origShadows = new LightShadows[lights.Length];
            int casters = 0;
            for (int i = 0; i < lights.Length; i++) { origShadows[i] = lights[i].shadows; if (origShadows[i] != LightShadows.None) casters++; }

            Camera mainCam = FindMainCamera();
            object camData = mainCam != null ? mainCam.GetComponent("UniversalAdditionalCameraData") : null;
            bool origPostFx = false; bool postFxAvail = false;
            if (camData != null) { object v = GetPublicProperty(camData, "renderPostProcessing"); if (v is bool b) { origPostFx = b; postFxAvail = true; } }

            object urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline;
            int origMsaa = 0; bool msaaAvail = false;
            { object v = urp != null ? GetPublicProperty(urp, "msaaSampleCount") : null; if (v is int mi) { origMsaa = mi; msaaAvail = true; } }
            float origScale = 1f; bool scaleAvail = false;
            { object v = urp != null ? GetPublicProperty(urp, "renderScale") : null; if (v is float fv) { origScale = fv; scaleAvail = true; } }
            bool origBatch = UnityEngine.Rendering.GraphicsSettings.useScriptableRenderPipelineBatching;

            // which knobs are available
            var knobs = new List<string>();
            if (casters > 0) knobs.Add("shadows");
            if (postFxAvail && origPostFx) knobs.Add("postfx");   // only measurable if currently on
            knobs.Add("srpbatcher");
            if (msaaAvail && origMsaa > 1) knobs.Add("msaa");
            if (scaleAvail) knobs.Add("renderscale");
            Debug.Log($"[Render] knobs: {string.Join(",", knobs)} | shadowCasters={casters} postfx={origPostFx} msaa={origMsaa} scale={origScale} batch={origBatch}");

            var mainThread = new ProfilerRecorder(ProfilerCategory.Internal, "Main Thread", 64);
            var gpuFrame   = new ProfilerRecorder(ProfilerCategory.Render,   "GPU Frame Time", 64);
            mainThread.Start(); gpuFrame.Start();
            BeginPerfPacing();
            yield return null; yield return null;
            string counterNote = VerifyGatingCounters("Render", mainThread, gpuFrame);

            // local: set a knob to on(baseline)/off(reduced); everything else stays at baseline
            void Apply(string knob, bool on)
            {
                switch (knob)
                {
                    case "shadows": for (int i = 0; i < lights.Length; i++) if (lights[i] != null) lights[i].shadows = on ? origShadows[i] : LightShadows.None; break;
                    case "postfx": SetProp(camData, "renderPostProcessing", on ? origPostFx : false); break;
                    case "srpbatcher": UnityEngine.Rendering.GraphicsSettings.useScriptableRenderPipelineBatching = on ? origBatch : false; break;
                    case "msaa": SetProp(urp, "msaaSampleCount", on ? origMsaa : 1); break;
                    case "renderscale": SetProp(urp, "renderScale", on ? origScale : 0.7f); break;
                }
            }
            void RestoreAll()
            {
                for (int i = 0; i < lights.Length; i++) if (lights[i] != null) lights[i].shadows = origShadows[i];
                if (postFxAvail) SetProp(camData, "renderPostProcessing", origPostFx);
                UnityEngine.Rendering.GraphicsSettings.useScriptableRenderPipelineBatching = origBatch;
                if (msaaAvail) SetProp(urp, "msaaSampleCount", origMsaa);
                if (scaleAvail) SetProp(urp, "renderScale", origScale);
            }

            float we = UnityEngine.Time.realtimeSinceStartup + PERF_WARMUP_S;
            while (UnityEngine.Time.realtimeSinceStartup < we && EditorApplication.isPlaying) yield return null;

            var rows = new List<string>(16384);
            rows.Add("knob,window,cond,frame_in_window,t_ms,cpu_ms,gpu_ms,drop");
            foreach (string knob in knobs)
            {
                if (!EditorApplication.isPlaying) break;
                RestoreAll();
                for (int w = 0; w < RENDER_WINDOWS_PER_KNOB && EditorApplication.isPlaying; w++)
                {
                    int block = w % 4; bool condA = (block == 0 || block == 3);  // A = on(baseline), B = off(reduced)
                    Apply(knob, condA);
                    int fInWin = 0; float wEnd = UnityEngine.Time.realtimeSinceStartup + PERF_WINDOW_S;
                    while (UnityEngine.Time.realtimeSinceStartup < wEnd && EditorApplication.isPlaying)
                    {
                        double cpu = (mainThread.Valid && mainThread.LastValue > 0) ? mainThread.LastValue * 1e-6 : -1;
                        double gpu = (gpuFrame.Valid && gpuFrame.LastValue > 0) ? gpuFrame.LastValue * 1e-6 : -1;
                        int drop = fInWin < PERF_DROP_FRAMES ? 1 : 0;
                        rows.Add(string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3},{4:F1},{5:F4},{6:F4},{7}",
                                               knob, w, condA ? "A" : "B", fInWin, sw.Elapsed.TotalMilliseconds, cpu, gpu, drop));
                        fInWin++; yield return null;
                    }
                }
            }
            RestoreAll();
            EndPerfPacing();
            mainThread.Dispose(); gpuFrame.Dispose();

            try
            {
                File.WriteAllText(RENDER_CSV_PATH, string.Join("\n", rows), new UTF8Encoding(false));
                WriteRunMeta(RENDER_CSV_PATH, "Render", counterNote, $"\"knobs\":\"{string.Join(";", knobs)}\",\"windowsPerKnob\":{RENDER_WINDOWS_PER_KNOB},\"samples\":{rows.Count - 1}");
                Debug.Log($"[Render] wrote {rows.Count - 1} samples over {knobs.Count} knobs to {RENDER_CSV_PATH}");
            }
            catch (Exception e) { Debug.LogError("[Render] csv write failed: " + e); }

            if (_exitOnFinish) { SessionState.SetBool(KEY_QUIT, true); SessionState.SetBool(KEY_EXIT, true); }
            if (EditorApplication.isPlaying) EditorApplication.ExitPlaymode();
        }

        private static Camera FindMainCamera()
        {
            if (Camera.main != null) return Camera.main;
            Camera best = null;
            foreach (var c in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Exclude))
            {
                if (c.targetTexture != null) continue;          // skip the preview RT camera
                if (best == null || c.depth > best.depth) best = c;
            }
            return best;
        }

        private static void RenderFail(string why) => FailMode("Render", RENDER_CSV_PATH, why);

        // ----- math helpers ---------------------------------------------------
        private static double Avg(List<double> xs) => xs.Count == 0 ? 0 : xs.Average();
        private static double PercentWorst(List<double> xs, double frac)
        {
            if (xs.Count == 0) return 0;
            int k = Math.Max(1, (int)(xs.Count * frac));
            return xs.OrderByDescending(x => x).Take(k).Average();
        }

        // =====================================================================
        //  ATLAS ROUTE TABLE  (data-driven driver for the "open -> settle -> capture ->
        //  verify" routes — replaces a set of near-identical AtlasCapture_ bodies with one
        //  generic driver. Routes with content polling, param construction, auth sub-views,
        //  or bespoke logic stay as their own coroutines, dispatched as Kind.Custom. The big
        //  win beyond dedup: `ok` is now driven by the VerifyShown render assertion in ONE
        //  place, instead of being hardcoded true in every method.)
        // =====================================================================
        private enum AtlasKind { ExploreSection, Popup, Friends, Custom }
        private sealed class AtlasRoute
        {
            public string name;
            public AtlasKind kind;
            public string controllerType;     // Popup: full controller type name
            public string section, subTab;     // ExploreSection (section[,subTab]) / Friends (tab)
            public System.Func<object> paramFactory;   // Popup param (null => 0-arg IssueCommand)
            public int settleFrames = 18;
        }

        // Generic capture driver: open by kind -> settle -> capture -> VerifyShown -> set ok.
        private static IEnumerator RunRoute(AtlasRoute r, object mvc, Report report)
        {
            var m = new PhaseMarker { label = "atlas_" + r.name, ok = false };
            report.actions.Add(m);

            string err = null; bool opened = false;
            // ExploreSection routes (settings/map/backpack/controls/marketplace) MUST open via the COROUTINE
            // form: it pre-closes and polls ExplorePanelController.State until ViewHidden before ShowAsync,
            // dodging the inter-driver async-hide race where a synchronous ShowAsync sees State != ViewHidden
            // and no-ops -> bare world (the exact bug places/events already fixed). The Folded() dedup had
            // routed these through the synchronous TryOpenExplorePanel and reintroduced the race (settings +
            // map captured bare). A yield can't sit inside a try, so this case lives outside it.
            if (r.kind == AtlasKind.ExploreSection)
            {
                yield return OpenExplorePanelDirectCo(mvc, r.section, null, r.subTab);
                opened = openDirectErr == null; err = openDirectErr;
            }
            else
            {
                try
                {
                    object param = r.kind == AtlasKind.Popup && r.paramFactory != null ? r.paramFactory() : null;
                    switch (r.kind)
                    {
                        case AtlasKind.Friends: opened = TryOpenFriendsPanel(mvc, r.subTab, out err); break;
                        case AtlasKind.Popup:   opened = TryShowPanelByName(mvc, r.controllerType, param, out err); break;
                    }
                }
                catch (System.Exception e) { err = e.InnerException?.Message ?? e.Message; }
            }
            if (!opened) { m.error = "open-failed: " + (err ?? "?"); yield break; }

            for (int i = 0; i < r.settleFrames; i++) yield return null;
            yield return CaptureShot(r.name);

            string verr = "panel key unavailable";
            bool shown = lastPanelKey != null && VerifyShown(mvc, lastPanelKey, out verr);
            m.ok = shown;                                  // means something now (render assertion), not hardcoded true
            m.error = shown ? "shown" : "not-shown: " + (verr ?? "?");
        }

        // Folded routes: behaviorally identical to the AtlasCapture_ bodies they replace
        // (same helper calls + settle), so the dedup carries zero capture-behavior risk.
        private static AtlasRoute Folded(string name)
        {
            switch (name)
            {
                case "settings":      return new AtlasRoute { name = "settings",      kind = AtlasKind.ExploreSection, section = "Settings", subTab = "GRAPHICS", settleFrames = 24 };
                case "map":           return new AtlasRoute { name = "map",           kind = AtlasKind.ExploreSection, section = "Navmap",   settleFrames = 18 };
                case "backpack":      return new AtlasRoute { name = "backpack",      kind = AtlasKind.ExploreSection, section = "Backpack", settleFrames = 18 };
                case "controls":      return new AtlasRoute { name = "controls",      kind = AtlasKind.ExploreSection, section = "Settings", subTab = "CONTROLS", settleFrames = 18 };
                case "marketplace":   return new AtlasRoute { name = "marketplace",   kind = AtlasKind.ExploreSection, section = "Backpack", settleFrames = 300 };
                case "friends":       return new AtlasRoute { name = "friends",       kind = AtlasKind.Friends,        subTab = "FRIENDS",   settleFrames = 24 };
                case "voice":         return new AtlasRoute { name = "voice",         kind = AtlasKind.Popup, controllerType = "DCL.VoiceChat.UI.NearbyVoicePanelController", settleFrames = 18 };
                case "profilewidget": return new AtlasRoute { name = "profilewidget", kind = AtlasKind.Popup, controllerType = "DCL.UI.Profiles.ProfileMenuController", settleFrames = 18 };
                case "emotewheel":    return new AtlasRoute { name = "emotewheel",    kind = AtlasKind.Popup, controllerType = "DCL.EmotesWheel.EmotesWheelController", settleFrames = 18 };
                case "skybox":        return new AtlasRoute { name = "skybox",        kind = AtlasKind.Popup, controllerType = "DCL.UI.Skybox.SkyboxMenuController", settleFrames = 18 };
                default: throw new ArgumentException("unknown folded atlas route: " + name);
            }
        }

        // =====================================================================
        //  ATLAS CAPTURE DRIVERS (one per UI sub-state; non-gating, capture-only)
        // =====================================================================
        // ATLAS_METHODS_BEGIN
        // E01 "explore": the Explore panel as it opens from the sidebar/hotkey. Per ExplorePanelController the
        // default landing is the discover/Events section (lastShownSection = includeDiscover ? Events : ...), so
        // we open that — it is the authentic "Explore" entry point. The events-by-day grid renders skeleton tiles
        // (EventsByDayView.SetEventsGridAsLoading(true) -> SkeletonLoadingView) until HttpEventsApiService returns
        // and EventsByDayController.OnSectionOpen calls eventsStateService.AddEvents(...). The old driver captured
        // mid-load (only 18 frames), so the shot showed empty skeleton cards. FIX (loadwait): after opening, POLL a
        // reflectable results-count — ExplorePanelController.EventsController (public prop) -> private eventsStateService
        // (EventsStateService) -> private currentEvents (Dictionary).Count > 0 — up to ~6s, then a short thumbnail
        // settle, so REAL event cards render. If the path is unreachable, fall back to a generous fixed settle.
        // Capture-only, no noise (no RSVP/join/create/share).
        private static IEnumerator AtlasCapture_explore(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_explore", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            string err = null;
            try
            {
                if (mvcManager == null) err = "explore: mvcManager null";
                else if (!TryOpenExplorePanel(mvcManager, "Events", null, out string openErr))
                    err = "explore: " + openErr;
            }
            catch (System.Exception e) { err = "explore: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Resolve the reflectable events-state-service ONCE (no yields in this try).
            object stateService = null;
            string pollNote = null;
            try
            {
                object explore = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                object eventsController = explore != null ? GetMember(explore, "EventsController") : null;
                stateService = eventsController != null ? GetPrivateField(eventsController, "eventsStateService") : null;
                if (stateService == null) pollNote = "no-state-service(fallback-settle)";
            }
            catch (System.Exception e) { pollNote = "resolve-failed(fallback-settle): " + (e.InnerException?.Message ?? e.Message); }

            // CONTENT-LOAD POLL (outside any try): wait for real events to populate currentEvents.
            // Reads the count inside a tiny try WITHOUT yields; loop + yield return null stay outside.
            int loadedCount = 0;
            if (stateService != null)
            {
                const int maxFrames = 360; // ~6s at 60fps
                for (int i = 0; i < maxFrames; i++)
                {
                    int c = 0;
                    try
                    {
                        object dict = GetPrivateField(stateService, "currentEvents");
                        object cnt = dict != null ? GetMember(dict, "Count") : null;
                        if (cnt is int ci) c = ci;
                    }
                    catch { c = 0; }
                    loadedCount = c;
                    if (c > 0) break;
                    yield return null;
                }
                // Short settle so thumbnails/layout of the now-real cards finish drawing.
                for (int i = 0; i < 48; i++) yield return null;
                pollNote = loadedCount > 0 ? ("events-loaded=" + loadedCount) : "poll-timeout(empty-or-no-events)";
            }
            else
            {
                // Generous fixed settle for the open animation + events fetch when the count isn't reachable.
                for (int i = 0; i < 270; i++) yield return null;
            }

            yield return CaptureShot("explore");

            string verifyErr = "no panel key";
            bool shown = lastPanelKey != null && VerifyShown(mvcManager, lastPanelKey, out verifyErr);
            m.error = shown ? ("shown" + (pollNote != null ? " (" + pollNote + ")" : "")) : ("not-shown: " + (verifyErr ?? "no panel key"));
        }

        // Open the Explore panel on the Places section (discover places list) via TryOpenExplorePanel
        // (ExplorePanelController.IssueCommand(new ExplorePanelParameter(ExploreSections.Places)) -> MvcManager.ShowAsync),
        // then POLL the live places listings until they finish loading before capture (current capture was
        // mid-load skeleton tiles). Loaded-flag chain (all reflection): ExplorePanelController.PlacesController
        // (public prop) -> PlacesResultsController (public prop) -> placesStateService (private field) ->
        // CurrentPlaces (public Dictionary prop, .Count) AND PlacesResultsController.isPlacesGridLoadingItems
        // (private bool). Done = CurrentPlaces.Count>0 && !isPlacesGridLoadingItems. Poll up to ~6s, else a
        // generous settle so real data renders. Capture-only / no-noise: opens & shows the panel only.
        private static IEnumerator AtlasCapture_places(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_places", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            // ---- Open the ExplorePanel on the Places section. TryOpenExplorePanel builds the
            // ExplorePanelParameter(ExploreSections.Places) and runs IssueCommand->ShowAsync, setting lastPanelKey.
            // Open via the COROUTINE form (pre-close + poll until ViewHidden, THEN ShowAsync). The prior
            // TryOpenExplorePanel raced the inter-driver async hide -> ShowAsync saw State != ViewHidden and
            // no-opped -> bare world (E03 captured bare in the round-2 analysis). Same fix events/settings use.
            string err = null;
            if (mvcManager == null) err = "places: mvcManager null";
            if (err == null)
            {
                yield return OpenExplorePanelDirectCo(mvcManager, "Places", null, null);   // sets lastPanelKey
                if (openDirectErr != null) err = "places: " + openDirectErr;
            }
            if (err != null) { m.error = err; yield break; }

            // ---- Brief settle so the ExplorePanelController is registered + the Places section activates
            // (Activate() triggers the initial LoadPlaces via OnAnyFilterChanged).
            for (int i = 0; i < 18; i++) yield return null;

            // ---- POLL the places content load. Up to ~6s (360 frames). 'Done' = at least one place landed in
            // CurrentPlaces AND the grid is no longer flagged loading. The read happens inside a tiny try with
            // NO yields; the 'yield return null' driving the loop stays OUTSIDE any try.
            bool loaded = false;
            int placesCount = 0;
            for (int i = 0; i < 360 && !loaded; i++)
            {
                try
                {
                    object explore = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                    object placesCtrl = explore != null ? GetMember(explore, "PlacesController") : null;
                    object resultsCtrl = placesCtrl != null ? GetMember(placesCtrl, "PlacesResultsController") : null;
                    if (resultsCtrl != null)
                    {
                        object stateSvc = GetMember(resultsCtrl, "placesStateService");
                        object current = stateSvc != null ? GetMember(stateSvc, "CurrentPlaces") : null;
                        object cnt = current != null ? GetMember(current, "Count") : null;
                        placesCount = (cnt as int?) ?? 0;
                        bool gridLoading = (GetMember(resultsCtrl, "isPlacesGridLoadingItems") as bool?) ?? true;
                        if (placesCount > 0 && !gridLoading) loaded = true;
                    }
                }
                catch { /* tolerate transient reflection/null during load; keep polling */ }
                yield return null;
            }

            // ---- Extra settle so card thumbnails/likes paint after the data lands (or a generous fallback
            // settle if the poll timed out with no readable data).
            int extra = loaded ? 90 : 240;
            for (int i = 0; i < extra; i++) yield return null;

            // ---- Capture (outside any try). Always capture, even on degraded/empty data.
            yield return CaptureShot("places");

            // ---- Verify the panel actually rendered (non-gating note via m.error). VerifyShown outside any try.
            string verifyErr = "no panel key";
            bool shown = lastPanelKey != null && VerifyShown(mvcManager, lastPanelKey, out verifyErr);
            m.error = shown ? "shown" : ("not-shown: " + (verifyErr ?? "no panel key"));
        }

        // Open the Explore panel on the Events section (ExploreSections.Events) and capture it (UI-only, no noise).
        // Same MVC path the sidebar/hotkey uses: ExplorePanelController.IssueCommand(new ExplorePanelParameter(ExploreSections.Events))
        // -> MvcManager.ShowAsync. Reuses the harness TryOpenExplorePanel helper (it builds the 3-arg ctor, fires
        // ShowAsync fire-and-forget, and records lastPanelKey for VerifyShown). Capture-only: no event join/RSVP/share.
        //
        // LOAD-WAIT FIX: the prior driver only settled 18 frames, so the calendar grid captured as skeletons
        // (SkeletonLoadingView per column). Activate() lands on EventsSection.CALENDAR, whose EventsCalendarController
        // fetches highlighted + by-date-range + live events asynchronously (OnSectionOpenedAsync), then calls
        // EventsCalendarView.SetAsLoading(false) once data is populated. There is no reflectable public "IsLoading",
        // but every loaded event is stored in EventsController -> private EventsStateService 'eventsStateService'
        // -> private Dictionary<string,EventDTO> 'currentEvents'. We poll that count: when it is > 0 the grid has real
        // cards (skeletons are gone). Reach the controller via ExplorePanelController.EventsController (public prop).
        // POLL up to ~6s; then a generous settle for the card thumbnails/layout before capture. Degraded (empty
        // account / data-gated) -> we still capture whatever renders after the fixed settle.
        private static IEnumerator AtlasCapture_events(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_events", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            string err = null;
            if (mvcManager == null) err = "events: mvcManager null";
            if (err == null)
            {
                // Open via the COROUTINE form so the pre-close's async hide reaches ViewHidden before ShowAsync.
                yield return OpenExplorePanelDirectCo(mvcManager, "Events", null, null);
                if (openDirectErr != null) err = "events: " + openDirectErr;
            }
            if (err != null) { m.error = err; yield break; }

            // Locate the EventsStateService.currentEvents dictionary so we can poll for loaded cards.
            // (sync setup, inside try; no yields here.) Failure is non-fatal -> falls back to fixed settle.
            object eventsState = null;
            try
            {
                object explore = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                object eventsController = explore != null ? GetPublicProperty(explore, "EventsController") : null;
                if (eventsController != null) eventsState = GetPrivateField(eventsController, "eventsStateService");
            }
            catch (System.Exception) { eventsState = null; }

            // CONTENT-LOAD POLL (outside try): wait up to ~6s (360 frames) for at least one loaded event.
            // The currentEvents read is wrapped in a tiny no-yield try; the 'yield return null' stays outside it.
            int loadedCount = 0;
            int maxFrames = 360;
            for (int i = 0; i < maxFrames; i++)
            {
                int c = 0;
                try
                {
                    if (eventsState != null)
                    {
                        object dict = GetPrivateField(eventsState, "currentEvents");
                        object cnt = dict != null ? GetMember(dict, "Count") : null;
                        if (cnt is int ci) c = ci;
                    }
                }
                catch (System.Exception) { c = 0; }
                loadedCount = c;
                if (loadedCount > 0) break;
                yield return null;
            }

            // Generous settle for thumbnails/layout to render the populated cards (or for the degraded
            // empty-data case to finish its open animation) before capturing.
            int settle = loadedCount > 0 ? 90 : 240;
            for (int i = 0; i < settle; i++) yield return null;

            yield return CaptureShot("events");

            // Verify the explore panel actually rendered (non-gating render note). VerifyShown outside any try.
            string verifyErr = "no panel key";
            bool shown = lastPanelKey != null && VerifyShown(mvcManager, lastPanelKey, out verifyErr);
            if (shown)
                m.error = loadedCount > 0 ? "shown" : "shown:degraded-no-events-loaded";
            else
                m.error = "not-shown: " + (verifyErr ?? "no panel key");
        }

        // Open the Backpack panel on the Emotes tab via ExplorePanelController.IssueCommand -> MvcManager.ShowAsync and capture it (UI-only, no noise)
        private static IEnumerator AtlasCapture_backpackemotes(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_backpackemotes", ok = true };
            report.actions.Add(m);

            // Open via the COROUTINE form (pre-close + poll until ViewHidden, THEN ShowAsync) on the Backpack
            // section + Emotes subsection. The prior inline IssueCommand->ShowAsync raced the inter-driver async
            // hide -> ShowAsync saw State != ViewHidden and no-opped -> bare world (E07 failed ViewHidden in
            // runs 1 & 3). Same fix events/settings/places/map use; OpenExplorePanelDirectCo sets lastPanelKey.
            string err = null;
            if (mvcManager == null) err = "backpackemotes: mvcManager null";
            if (err == null)
            {
                yield return OpenExplorePanelDirectCo(mvcManager, "Backpack", "Emotes", null);
                if (openDirectErr != null) err = "backpackemotes: " + openDirectErr;
            }
            if (err != null) { m.error = err; yield break; }

            // Settle for the open animation, then verify the panel actually rendered.
            for (int i = 0; i < 18; i++) yield return null;

            if (lastPanelKey != null && !VerifyShown(mvcManager, lastPanelKey, out string verifyErr))
            {
                m.error = "backpackemotes: " + verifyErr;
                yield break;
            }

            yield return CaptureShot("backpackemotes");
            m.error = "shown";
        }

        // Opens Backpack in ExplorePanel, switches the Avatar section's Outfits tab on, then screenshots the outfits UI.
        private static IEnumerator AtlasCapture_backpackoutfits(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_backpackoutfits", ok = true };  // NON-GATING
            report.actions.Add(m);

            // --- Step 1: synchronous reflection setup to OPEN the Backpack section of the Explore panel.
            // Same MVC path the hotkey uses: ExplorePanelController.IssueCommand(new ExplorePanelParameter(Backpack))
            // -> mvcManager.ShowAsync<TView,TInput>(cmd, ct). Read-only UI open; no noise.
            string err = null;
            try
            {
                Type controllerT = FindType("DCL.ExplorePanel.ExplorePanelController");
                Type paramT      = FindType("DCL.ExplorePanel.ExplorePanelParameter");
                Type sectionsT   = FindType("DCL.UI.ExploreSections");
                if (controllerT == null) err = "backpackoutfits: ExplorePanelController type not found";
                else if (paramT == null) err = "backpackoutfits: ExplorePanelParameter type not found";
                else if (sectionsT == null) err = "backpackoutfits: ExploreSections type not found";
                else
                {
                    object backpackSection = Enum.Parse(sectionsT, "Backpack");
                    // ctor: ExplorePanelParameter(ExploreSections, BackpackSections?=null, SettingsSection?=null)
                    ConstructorInfo ctor = paramT.GetConstructors()[0];
                    object[] ctorArgs = new object[ctor.GetParameters().Length];
                    ctorArgs[0] = backpackSection;
                    object param = ctor.Invoke(ctorArgs);

                    // Static IssueCommand is inherited from ControllerBase -> FlattenHierarchy.
                    MethodInfo issue = controllerT.GetMethod("IssueCommand",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                    if (issue == null) err = "backpackoutfits: IssueCommand not found on ExplorePanelController";
                    else
                    {
                        object command = issue.Invoke(null, new[] { param });
                        if (command == null) err = "backpackoutfits: IssueCommand returned null";
                        else
                        {
                            MethodInfo showAsync = null;
                            foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                            if (showAsync == null) err = "backpackoutfits: ShowAsync not found on mvcManager";
                            else
                            {
                                Type[] genArgs = command.GetType().GetGenericArguments(); // [TView, TInput]
                                showAsync.MakeGenericMethod(genArgs)
                                         .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });
                                Type ifaceOpen = FindType("MVC.IController`2");
                                lastPanelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(genArgs) : null;
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "backpackoutfits: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Let the Backpack panel render (the Avatar section is the default sub-section).
            // Longer settle than before: the OutfitsTabSelector Toggle + its onValueChanged listener
            // are only live once the panel has finished animating in; reaching the toggle at frame-20
            // could land while the GameObject was still inactive, so the isOn write below no-opped.
            for (int i = 0; i < 48; i++) yield return null;

            // --- Step 2: reach the Outfits tab toggle and turn it on.
            // ExplorePanelController.backpackController (private field) -> BackpackController.avatarController
            // (private field) -> AvatarController.view (private AvatarView) -> AvatarView.OutfitsTabSelector
            // (public TabSelectorView) -> TabSelectorToggle (public Toggle) -> isOn = true.
            object toggle = null;
            try
            {
                object explorePanelCtl = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                if (explorePanelCtl == null) err = "backpackoutfits: ExplorePanelController not found at runtime";
                else
                {
                    object backpackCtl = GetMember(explorePanelCtl, "backpackController");
                    if (backpackCtl == null) err = "backpackoutfits: backpackController field not found";
                    else
                    {
                        object avatarCtl = GetMember(backpackCtl, "avatarController");
                        if (avatarCtl == null) err = "backpackoutfits: avatarController field not found";
                        else
                        {
                            object avatarView = GetMember(avatarCtl, "view");
                            if (avatarView == null) err = "backpackoutfits: AvatarController.view not found";
                            else
                            {
                                object outfitsTabSelector = GetMember(avatarView, "OutfitsTabSelector");
                                if (outfitsTabSelector == null) err = "backpackoutfits: OutfitsTabSelector not found on AvatarView";
                                else
                                {
                                    toggle = GetMember(outfitsTabSelector, "TabSelectorToggle");
                                    if (toggle == null) err = "backpackoutfits: TabSelectorToggle not found on OutfitsTabSelector";
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "backpackoutfits: " + (e.InnerException?.Message ?? e.Message); }

            // --- Step 3: set toggle.isOn = true to switch to the Outfits tab (triggers the in-place tab swap).
            if (err == null && toggle != null)
            {
                try
                {
                    PropertyInfo isOnProp = toggle.GetType().GetProperty("isOn", BindingFlags.Public | BindingFlags.Instance);
                    if (isOnProp == null) err = "backpackoutfits: isOn property not found on Toggle";
                    else
                    {
                        // Setting Toggle.isOn via reflection only fires onValueChanged when the value
                        // TRANSITIONS. If the toggle already reports isOn (or its group hadn't settled) the
                        // single SetValue(true) no-ops and the CATEGORIES->SAVED OUTFITS content swap (driven
                        // off that callback) never runs -> the capture stays on CATEGORIES. Force a real
                        // transition (false-without-notify, then true) and ALSO manually Invoke onValueChanged(true),
                        // mirroring the AtlasCapture_inputsuggestions manual-invoke pattern. UI-only, no noise.
                        MethodInfo setWithoutNotify = toggle.GetType().GetMethod("SetIsOnWithoutNotify", BindingFlags.Public | BindingFlags.Instance);
                        if (setWithoutNotify != null) setWithoutNotify.Invoke(toggle, new object[] { false });
                        isOnProp.SetValue(toggle, true);

                        object onValueChanged = GetMember(toggle, "onValueChanged");
                        if (onValueChanged != null)
                        {
                            MethodInfo invokeMethod = onValueChanged.GetType().GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance);
                            if (invokeMethod != null) invokeMethod.Invoke(onValueChanged, new object[] { true });
                        }
                    }
                }
                catch (System.Exception e) { err = "backpackoutfits: " + (e.InnerException?.Message ?? e.Message); }
            }

            // Let outfits data load + the tab animate in (outfits may need a live fetch). Longer than
            // before so the saved-outfit slots finish their async populate after the tab swap.
            for (int i = 0; i < 90; i++) yield return null;

            yield return CaptureShot("backpackoutfits");

            // Non-gating: record what happened but always succeed. Capture whatever rendered even if the
            // tab-switch reflection missed (the Backpack/Avatar section still shows).
            m.error = err != null ? err : "shown";
        }

        // Open PlaceDetailPanelController for the first place returned by SearchDestinationsAsync (read-only; no noise).
        // FIX vs atlas-drivers-clean/placedetail.cs ("response Data not enumerable"):
        //   IPlacesAPIResponse.Data is an EXPLICIT interface implementation on PlacesAPIResponse
        //   (IReadOnlyList<PlaceInfo> IPlacesAPIResponse.Data => data;), so GetMember(response,"Data") can never
        //   find it (no public/private member literally named "Data"; the explicit prop is name-mangled).
        //   We instead resolve the value through the interface property (interface dispatch -> explicit impl),
        //   with a fallback to the concrete public field "data".
        //   Also: query GLOBAL MOST_ACTIVE places (pageSize=20, no location/searchText filter) so a populated area
        //   like genesis plaza is returned even though our last capture was the quiet parcel 140,140.
        private static IEnumerator AtlasCapture_placedetail(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_placedetail", ok = true };
            report.actions.Add(m);

            string err = null;
            object placesApi = null;
            MethodInfo searchMethod = null;
            object[] searchArgs = null;

            // Phase 1: reach IPlacesAPIService + bind SearchDestinationsAsync (no yields here)
            try
            {
                // ExplorePanelController -> PlacesController -> PlacesResultsController -> placesAPIService (private field)
                object explorePanelCtl = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                if (explorePanelCtl == null) err = "placedetail: ExplorePanelController not found";

                object placesController = err != null ? null : GetMember(explorePanelCtl, "PlacesController");
                if (err == null && placesController == null) err = "placedetail: PlacesController not found";

                object placesResultsCtl = err != null ? null : GetMember(placesController, "PlacesResultsController");
                if (err == null && placesResultsCtl == null) err = "placedetail: PlacesResultsController not found";

                placesApi = err != null ? null : GetPrivateField(placesResultsCtl, "placesAPIService");
                if (err == null && placesApi == null) err = "placedetail: placesAPIService not found";

                if (err == null)
                {
                    foreach (MethodInfo mi in placesApi.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        if (mi.Name == "SearchDestinationsAsync") { searchMethod = mi; break; }
                    if (searchMethod == null) err = "placedetail: SearchDestinationsAsync not found";
                }

                if (err == null)
                {
                    // SearchDestinationsAsync(int pageNumber, int pageSize, CancellationToken ct, string? searchText=null,
                    //   SortBy=MOST_ACTIVE, SortDirection=DESC, string? category=null, bool? withConnectedUsers=null,
                    //   bool? onlySdk7=null, bool? withLiveEvents=null, bool? onlyPlaces=null)
                    // pageNumber=0, pageSize=20 (default MOST_ACTIVE sort => populated places, e.g. genesis plaza);
                    // leave every optional at its default so we make NO location/search filtering assumptions.
                    ParameterInfo[] ps = searchMethod.GetParameters();
                    searchArgs = new object[ps.Length];
                    int intSeen = 0;
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (ps[i].ParameterType == typeof(int)) searchArgs[i] = (intSeen++ == 0) ? 0 : 20; // pageNumber=0, pageSize=20
                        else if (ps[i].ParameterType == typeof(System.Threading.CancellationToken)) searchArgs[i] = System.Threading.CancellationToken.None;
                        else searchArgs[i] = ps[i].HasDefaultValue ? Type.Missing : null;
                    }
                }
            }
            catch (System.Exception e) { err = "placedetail: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Phase 2: invoke + await search (UniTask awaited OUTSIDE try)
            object task = null;
            try { task = searchMethod.Invoke(placesApi, searchArgs); }
            catch (System.Exception e) { err = "placedetail: invoke SearchDestinationsAsync: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }
            if (task == null) { m.error = "placedetail: SearchDestinationsAsync returned null"; yield break; }

            yield return AwaitUniTask(task);
            if (awaitedError != null) { m.error = "placedetail: SearchDestinationsAsync failed: " + awaitedError; yield break; }

            // Phase 3: extract first PlaceInfo via the interface property, build param, show command (no yields here)
            object placeInfo = null;
            object command = null;
            MethodInfo showAsync = null;
            Type[] genArgs = null;
            try
            {
                object response = awaitedResult;
                if (response == null) err = "placedetail: no response from SearchDestinationsAsync";

                // --- THE FIX: read Data via the explicit interface property, with a public-field fallback. ---
                object data = null;
                if (err == null)
                {
                    // PlacesData.IPlacesAPIResponse.Data  (reflection name uses '+' for the nested type)
                    Type respIface = FindType("DCL.PlacesAPIService.PlacesData+IPlacesAPIResponse");
                    if (respIface != null)
                    {
                        PropertyInfo dataProp = respIface.GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
                        if (dataProp != null)
                        {
                            try { data = dataProp.GetValue(response); } catch { data = null; } // interface dispatch -> explicit impl
                        }
                    }
                    // Fallback: concrete PlacesAPIResponse exposes the backing list as a public field 'data'.
                    if (data == null) data = GetPublicField(response, "data");
                    if (data == null) err = "placedetail: could not read IPlacesAPIResponse.Data";
                }

                System.Collections.IEnumerable seq = data as System.Collections.IEnumerable;
                if (err == null && seq == null) err = "placedetail: response Data not enumerable";

                if (err == null)
                {
                    foreach (object item in seq) { placeInfo = item; break; }
                    if (placeInfo == null) err = "placedetail: no places in response data";
                }

                // PlaceDetailPanelParameter(PlaceInfo placeData, PlaceCardView?=null, List<CompactInfo>?=null, EventDTO?=null)
                object paramObj = null;
                if (err == null)
                {
                    Type paramType = FindType("DCL.Places.PlaceDetailPanelParameter");
                    if (paramType == null) err = "placedetail: PlaceDetailPanelParameter type not found";
                    else
                    {
                        ConstructorInfo ctor = null;
                        foreach (ConstructorInfo ci in paramType.GetConstructors())
                            if (ci.GetParameters().Length == 4) { ctor = ci; break; }
                        if (ctor == null) err = "placedetail: PlaceDetailPanelParameter ctor(4) not found";
                        else paramObj = ctor.Invoke(new object[] { placeInfo, null, null, null }); // 3 optionals null
                    }
                }
                if (err == null && paramObj == null) err = "placedetail: parameter construction returned null";

                // PlaceDetailPanelController.IssueCommand(param) -> ShowCommand<TView, TParam> (inherited static)
                if (err == null)
                {
                    Type controllerT = FindType("DCL.Places.PlaceDetailPanelController");
                    if (controllerT == null) err = "placedetail: PlaceDetailPanelController not found";
                    else
                    {
                        MethodInfo issueCmd = null;
                        foreach (MethodInfo mi in controllerT.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                            if (mi.Name == "IssueCommand" && mi.GetParameters().Length == 1) { issueCmd = mi; break; }
                        if (issueCmd == null) err = "placedetail: IssueCommand(param) not found";
                        else command = issueCmd.Invoke(null, new[] { paramObj });
                    }
                }
                if (err == null && command == null) err = "placedetail: IssueCommand returned null";

                if (err == null)
                {
                    foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                    if (showAsync == null) err = "placedetail: ShowAsync not found";
                    else genArgs = command.GetType().GetGenericArguments(); // [TView, TParam]
                }

                if (err == null)
                {
                    showAsync.MakeGenericMethod(genArgs)
                             .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });

                    Type ifaceOpen = FindType("MVC.IController`2");
                    lastPanelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(genArgs) : null;
                }
            }
            catch (System.Exception e) { err = "placedetail: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Settle for panel animation, then verify + capture
            for (int i = 0; i < 18; i++) yield return null;

            if (lastPanelKey != null && !VerifyShown(mvcManager, lastPanelKey, out string verr))
            {
                m.error = "placedetail: not-shown: " + verr;
                yield break;
            }

            yield return CaptureShot("placedetail");
            m.error = "shown";
        }

        // Fetches upcoming events from the live events API, then opens EventDetailPanelController for the first event (read-only display)
        private static IEnumerator AtlasCapture_eventdetail(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_eventdetail", ok = true };
            report.actions.Add(m);

            string err = null;
            object eventsApi = null;

            // --- Synchronous setup: locate the events API service on ExplorePanelController ---
            try
            {
                object explore = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                if (explore != null)
                    eventsApi = GetPrivateField(explore, "eventsApiService");

                if (eventsApi == null)
                    err = "eventdetail: eventsApiService not found";
            }
            catch (System.Exception e) { err = "eventdetail: " + (e.InnerException?.Message ?? e.Message); }

            if (err != null)
            {
                m.error = err + "; skipped:no-api";
                yield return CaptureShot("eventdetail");
                yield break;
            }

            // --- Synchronous setup: build the GetEventsByDateRangeAsync(now, now+7d, true, ct) task ---
            object eventTask = null;
            try
            {
                System.DateTime now = System.DateTime.UtcNow;
                System.DateTime toDate = now.AddDays(7);
                System.Reflection.MethodInfo getEventsMethod = null;
                foreach (var mi in eventsApi.GetType().GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (mi.Name == "GetEventsByDateRangeAsync") { getEventsMethod = mi; break; }
                }
                if (getEventsMethod != null)
                    eventTask = getEventsMethod.Invoke(eventsApi, new object[] { now, (object)toDate, (object)true, System.Threading.CancellationToken.None });
                else
                    err = "eventdetail: GetEventsByDateRangeAsync not found";
            }
            catch (System.Exception e) { err = "eventdetail: " + (e.InnerException?.Message ?? e.Message); }

            if (err != null || eventTask == null)
            {
                m.error = (err ?? "eventdetail: event fetch returned null") + "; skipped:no-events";
                yield return CaptureShot("eventdetail");
                yield break;
            }

            // --- Await the events fetch OUTSIDE any try/catch ---
            yield return AwaitUniTask(eventTask);
            if (awaitedError != null || awaitedResult == null)
            {
                m.error = "eventdetail: event fetch await failed: " + (awaitedError ?? "no result") + "; skipped:fetch-error";
                yield return CaptureShot("eventdetail");
                yield break;
            }

            // --- Synchronous: extract first event and construct the panel parameter ---
            object parameter = null;
            try
            {
                object eventData = null;
                if (awaitedResult is System.Collections.IEnumerable eventList)
                {
                    foreach (object ev in eventList) { eventData = ev; break; }
                }

                if (eventData == null)
                    err = "eventdetail: no events available; skipped:empty-list";
                else
                {
                    Type paramType = FindType("DCL.Communities.EventInfo.EventDetailPanelParameter");
                    Type eventDtoType = FindType("DCL.EventsApi.IEventDTO");
                    Type placeInfoType = FindType("DCL.PlacesAPIService.PlacesData+PlaceInfo");
                    Type eventCardType = FindType("DCL.Events.EventCardView");

                    if (paramType != null && eventDtoType != null && placeInfoType != null && eventCardType != null)
                    {
                        // ctor: (IEventDTO eventData, PlacesData.PlaceInfo? placeData, EventCardView? summonerPlaceCard)
                        // PlaceInfo is a reference type, so the nullable parameter is just the PlaceInfo type itself.
                        var ctor = paramType.GetConstructor(new[] { eventDtoType, placeInfoType, eventCardType });
                        if (ctor != null)
                            parameter = ctor.Invoke(new[] { eventData, null, null });
                        else
                            err = "eventdetail: EventDetailPanelParameter ctor not found";
                    }
                    else
                        err = "eventdetail: required types not resolved";
                }
            }
            catch (System.Exception e) { err = "eventdetail: " + (e.InnerException?.Message ?? e.Message); }

            if (err != null || parameter == null)
            {
                m.error = (err ?? "eventdetail: parameter construction failed; skipped:param-failed");
                if (m.error.IndexOf("skipped:") < 0) m.error += "; skipped:param-failed";
                yield return CaptureShot("eventdetail");
                yield break;
            }

            // --- Show the panel (read-only display) OUTSIDE any try/catch ---
            string showErr;
            if (!TryShowPanelByName(mvcManager, "DCL.Communities.EventInfo.EventDetailPanelController", parameter, out showErr))
            {
                m.error = "eventdetail: show panel failed: " + showErr + "; skipped:show-failed";
                yield return CaptureShot("eventdetail");
                yield break;
            }

            for (int i = 0; i < 18; i++) yield return null;             // settle
            HideChat(mvcManager);   // hide the persistent chat HUD bleeding into this isolated shot
            yield return CaptureShot("eventdetail");
            m.error = "shown";                                          // sentinel meaning success
        }

// Opens the Navmap (map) explore section and issues a read-only place search to populate results, then captures the state
        private static IEnumerator AtlasCapture_navigation(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_navigation", ok = true };  // NON-GATING: ok ALWAYS stays true
            report.actions.Add(m);

            string err = null;
            object searchTask = null;   // UniTask from SearchForPlaceAsync (awaited outside try)

            // ---- Synchronous setup: open panel + build the search task via reflection ----
            try
            {
                // Open the Navmap section through the same MVC path the input shortcuts use.
                if (!TryOpenExplorePanel(mvcManager, "Navmap", null, out string openErr))
                {
                    err = "navigation: panel open failed: " + openErr;
                }
                else
                {
                    // Locate the NavmapController and its INavmapBus (private field "navmapBus").
                    object navmapController = FindControllerByTypeName(mvcManager, "NavmapController");
                    object navmapBus = navmapController != null ? GetPrivateField(navmapController, "navmapBus") : null;

                    if (navmapBus != null)
                    {
                        Type searchParamsType = FindType("DCL.Navmap.INavmapBus+SearchPlaceParams");
                        Type filterType = FindType("DCL.Navmap.NavmapSearchPlaceFilter");
                        Type sortingType = FindType("DCL.Navmap.NavmapSearchPlaceSorting");

                        if (searchParamsType != null && filterType != null && sortingType != null)
                        {
                            // SearchPlaceParams.CreateWithDefaultParams(int page, int pageSize, string text,
                            //   NavmapSearchPlaceFilter filter, NavmapSearchPlaceSorting sorting, string category)
                            MethodInfo factoryMethod = searchParamsType.GetMethod("CreateWithDefaultParams",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                            if (factoryMethod != null)
                            {
                                object allFilter = Enum.Parse(filterType, "All");
                                object mostActiveSorting = Enum.Parse(sortingType, "MostActive");

                                // Reflection Invoke supplies ALL parameters (no optional defaults).
                                object searchParams = factoryMethod.Invoke(null, new object[]
                                {
                                    0,                    // page
                                    50,                   // pageSize
                                    null,                 // text (read-only browse; no query string)
                                    allFilter,            // filter
                                    mostActiveSorting,    // sorting
                                    null,                 // category
                                });

                                // SearchForPlaceAsync(SearchPlaceParams @params, CancellationToken ct) -> UniTask
                                MethodInfo searchMethod = navmapBus.GetType().GetMethod("SearchForPlaceAsync",
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                                if (searchMethod != null)
                                {
                                    searchTask = searchMethod.Invoke(navmapBus, new object[]
                                    {
                                        searchParams,
                                        System.Threading.CancellationToken.None,
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "navigation: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Settle frames for the panel open animation (outside any try).
            for (int i = 0; i < 18; i++) yield return null;

            // Verify the panel actually shown (non-gating informational; do not abort capture).
            if (!VerifyShown(mvcManager, lastPanelKey, out string verifyErr))
                m.error = "navigation(non-gating): not-shown: " + verifyErr;

            // Drive the read-only search UniTask if we built one (outside any try).
            if (searchTask != null)
            {
                yield return AwaitUniTask(searchTask);
                if (awaitedError != null) m.error = "navigation(non-gating): search error: " + awaitedError;
            }

            // Let search results render / animate in.
            for (int i = 0; i < 18; i++) yield return null;
            yield return CaptureShot("navigation");
            if (m.error == null) m.error = "shown";   // sentinel meaning success
        }

        // Opens the Backpack (Avatar section) and selects a wearable that is ACTUALLY rendered in the grid so the
        // right-hand ItemInfo detail pane (BackpackInfoPanelView.FullPanel) populates instead of "No item selected".
        //
        // FLOW (verified in source):
        //   click cell -> BackpackItemView.OnSelectItem -> BackpackGridController.SelectItem(slot,itemId)
        //     -> commandBus.SendCommand(BackpackSelectWearableCommand(itemId))
        //     -> BackpackCommandBus.SendCommand (OVERLOAD by struct type) -> SelectWearableMessageReceived
        //     -> BackpackBusController.HandleSelectWearableCommand
        //     -> ElementProviderHelper.FetchElementByPointerAndExecuteAsync(id) [storage hit -> wait IsLoading -> cb]
        //     -> SelectWearable -> backpackEventBus.SendWearableSelect(wearable)
        //     -> BackpackInfoPanelController.SetPanelContent: EmptyPanel.SetActive(false); FullPanel.SetActive(true);
        //        Name/Rarity/Description populated. <-- that toggle is what turns off "No item selected".
        //
        // ROBUSTNESS vs prior captures:
        //   - The authoritative "what is rendered" is BackpackGridController.usedPoolItems (Dictionary<URN,BackpackItemView>);
        //     its KEYS are the URNs genuinely placed into cells. We harvest those first (they are guaranteed storage-resident,
        //     so FetchElementByPointerAndExecuteAsync takes the fast storage path and the callback always fires), then fall
        //     back to the private List<ITrimmedWearable> "results" / "currentPageWearables" (GetUrn() -> URN struct).
        //   - We try MULTIPLE candidate URNs in sequence (dispatch + short wait each) so one unresolvable URN can't blank
        //     the pane. SendCommand is overload-resolved by the exact struct type to avoid AmbiguousMatchException.
        //   - "shown" is asserted when FullPanel.activeInHierarchy && !EmptyPanel.activeInHierarchy (the real toggle).
        //
        // NO NOISE: BackpackSelectWearableCommand is UI-only (it just fires SendWearableSelect to populate the detail pane).
        //   It does NOT equip/unequip/publish the profile, so nothing is visible to others. Read-only / select-for-display.
        private static IEnumerator AtlasCapture_iteminfo(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_iteminfo", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            string err = null;
            object backpackController = null;
            object avatarController = null;
            object gridController = null;
            object backpackCommandBus = null;

            // ---- 1) Open the Backpack section of the Explore panel via the COROUTINE form ----
            // The coroutine pre-closes then POLLS State until ViewHidden before ShowAsync, so the re-issue
            // can't be swallowed by MVCManager's "State != ViewHidden -> return" guard (the previous in-line
            // OpenExplorePanelDirect no-opped -> bare world, see E12 capture). yield is OUTSIDE any try.
            yield return OpenExplorePanelDirectCo(mvcManager, "Backpack", null, null);
            if (openDirectErr != null) err = "iteminfo: open backpack failed: " + openDirectErr;

            // Give the panel a moment to build (open anim + controller wiring) before we reach into it.
            for (int i = 0; i < 30; i++) yield return null;

            // ---- 2) Reach BackpackController -> AvatarController -> BackpackGridController + command bus ----
            try
            {
                if (err == null)
                {
                    object explore = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                    if (explore != null)
                        backpackController = GetPrivateField(explore, "backpackController");
                    if (backpackController == null)
                        err = "iteminfo: BackpackController not reachable from ExplorePanelController";
                }

                if (err == null)
                {
                    backpackCommandBus = GetPrivateField(backpackController, "backpackCommandBus");
                    avatarController = GetPrivateField(backpackController, "avatarController");
                    if (avatarController != null)
                        gridController = GetPrivateField(avatarController, "backpackGridController");

                    if (gridController == null)
                        err = "iteminfo: BackpackGridController not reachable";
                    else if (backpackCommandBus == null)
                        err = "iteminfo: backpackCommandBus not found";
                }
            }
            catch (System.Exception e) { err = "iteminfo: reach: " + (e.InnerException?.Message ?? e.Message); }

            // ---- 3) Poll the grid until it has rendered wearables, then harvest candidate URNs (OUTSIDE try) ----
            // Primary source: usedPoolItems (Dictionary<URN,BackpackItemView>) keys = URNs genuinely placed in cells.
            // Fallbacks: private List<ITrimmedWearable> "results" / IReadOnlyList "currentPageWearables" via GetUrn().
            var candidates = new System.Collections.Generic.List<string>();
            if (err == null)
            {
                for (int i = 0; i < 360 && candidates.Count == 0; i++)
                {
                    try
                    {
                        // 3a) usedPoolItems keys (most authoritative -> guaranteed storage-resident -> fast callback path)
                        object pool = GetPrivateField(gridController, "usedPoolItems");
                        if (pool is System.Collections.IDictionary dict)
                        {
                            foreach (object key in dict.Keys)
                            {
                                if (key == null) continue;
                                string s = key.ToString();
                                // skip numeric placeholder keys ("0".."15") used for still-loading slots
                                if (string.IsNullOrEmpty(s)) continue;
                                if (s.Length <= 2 && int.TryParse(s, out _)) continue;
                                if (!candidates.Contains(s)) candidates.Add(s);
                            }
                        }

                        // 3b) trimmed-wearable lists (GetUrn() -> URN struct -> ToString())
                        if (candidates.Count == 0)
                        {
                            object list = GetPrivateField(gridController, "results");
                            for (int pass = 0; pass < 2 && candidates.Count == 0; pass++)
                            {
                                if (list is System.Collections.IEnumerable items)
                                {
                                    foreach (object item in items)
                                    {
                                        if (item == null) continue;
                                        MethodInfo getUrn = item.GetType().GetMethod("GetUrn",
                                            BindingFlags.Public | BindingFlags.Instance, null, System.Type.EmptyTypes, null);
                                        object urnObj = getUrn?.Invoke(item, null);
                                        if (urnObj == null) continue;
                                        string s = urnObj.ToString();
                                        if (!string.IsNullOrEmpty(s) && !candidates.Contains(s)) candidates.Add(s);
                                    }
                                }
                                if (candidates.Count == 0)
                                    list = GetPrivateField(gridController, "currentPageWearables");
                            }
                        }
                    }
                    catch { candidates.Clear(); }

                    if (candidates.Count == 0) yield return null;
                }
            }

            if (err == null && candidates.Count == 0)
                err = "iteminfo: grid produced no wearable (empty/loading inventory)";

            // Resolve the overload-resolved SendCommand + command type once (synchronous, NO yield).
            Type cmdType = null;
            MethodInfo send = null;
            if (err == null)
            {
                try
                {
                    cmdType = FindType("DCL.Backpack.BackpackBus.BackpackSelectWearableCommand");
                    if (cmdType == null)
                        err = "iteminfo: BackpackSelectWearableCommand type not found";
                    else
                    {
                        // SendCommand is OVERLOADED by struct type; pick the exact overload to avoid AmbiguousMatch.
                        send = backpackCommandBus.GetType().GetMethod(
                            "SendCommand", BindingFlags.Public | BindingFlags.Instance, null, new[] { cmdType }, null);
                        if (send == null)
                            err = "iteminfo: SendCommand(BackpackSelectWearableCommand) overload not found";
                    }
                }
                catch (System.Exception e) { err = "iteminfo: resolve: " + (e.InnerException?.Message ?? e.Message); }
            }

            // ---- 4) For each candidate: dispatch select, then wait for FullPanel to activate (loop, dispatch in try) ----
            bool infoShown = false;
            if (err == null)
            {
                for (int c = 0; c < candidates.Count && !infoShown; c++)
                {
                    string wearableId = candidates[c];

                    // dispatch (synchronous, in try -- no yield inside)
                    try
                    {
                        // BackpackSelectWearableCommand(string id, Action endAction = null) - readonly struct.
                        object selectCommand = Activator.CreateInstance(cmdType, new object[] { wearableId, null });
                        send.Invoke(backpackCommandBus, new[] { selectCommand });
                    }
                    catch (System.Exception e) { m.error = "iteminfo: dispatch: " + (e.InnerException?.Message ?? e.Message); }

                    // wait for the detail pane to populate (~90 frames per candidate) OUTSIDE try.
                    // Success == FullPanel active && EmptyPanel inactive (the exact toggle SetPanelContent performs).
                    for (int i = 0; i < 90 && !infoShown; i++)
                    {
                        bool full = false, empty = true;
                        try
                        {
                            object infoPanelController = GetPrivateField(avatarController, "backpackInfoPanelController");
                            object view = infoPanelController != null ? GetPrivateField(infoPanelController, "view") : null;
                            object fullPanel = view != null ? GetMember(view, "FullPanel") : null;
                            object emptyPanel = view != null ? GetMember(view, "EmptyPanel") : null;
                            if (fullPanel != null)
                                full = (bool)(fullPanel.GetType()
                                    .GetProperty("activeInHierarchy", BindingFlags.Public | BindingFlags.Instance)
                                    ?.GetValue(fullPanel) ?? false);
                            if (emptyPanel != null)
                                empty = (bool)(emptyPanel.GetType()
                                    .GetProperty("activeInHierarchy", BindingFlags.Public | BindingFlags.Instance)
                                    ?.GetValue(emptyPanel) ?? true);
                        }
                        catch { full = false; empty = true; }

                        if (full && !empty) { infoShown = true; break; }
                        yield return null;
                    }
                }
            }
            else
            {
                // Still settle so we capture whatever rendered (the backpack grid itself).
                for (int i = 0; i < 30; i++) yield return null;
            }

            // Extra settle so the thumbnail/name/rarity finish painting before the shot.
            for (int i = 0; i < 30; i++) yield return null;

            if (err != null)
                m.error = err;
            else
                m.error = infoShown ? "shown" : "atlas: info panel inactive (captured backpack grid)";

            yield return CaptureShot("iteminfo");
        }

        // Open Explore panel to Navmap section, then toggle the map filter panel (layer toggles). Local UI only, no noise.
        private static IEnumerator AtlasCapture_mapfilters(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_mapfilters", ok = true };
            report.actions.Add(m);

            string err = null;
            Type explorePanelT = null;
            object param = null;

            // Synchronous setup: build ExplorePanelParameter(ExploreSections.Navmap). NEVER yield in here.
            try
            {
                Type paramT = FindType("DCL.ExplorePanel.ExplorePanelParameter");
                Type sectionsT = FindType("DCL.UI.ExploreSections");
                explorePanelT = FindType("DCL.ExplorePanel.ExplorePanelController");
                if (paramT == null || sectionsT == null || explorePanelT == null)
                { err = "mapfilters: types not found"; }
                else
                {
                    object section = Enum.Parse(sectionsT, "Navmap");
                    ConstructorInfo ctor = paramT.GetConstructors()[0];
                    object[] ctorArgs = new object[ctor.GetParameters().Length];
                    ctorArgs[0] = section;
                    param = ctor.Invoke(ctorArgs);
                }
            }
            catch (System.Exception e) { err = "mapfilters: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Ensure a fresh state (IEnumerator -> must be OUTSIDE try).
            yield return CloseOpenPanels(mvcManager);

            // Show the Explore panel (TryShowPanel is synchronous bool; wrap to catch reflection throws).
            try
            {
                if (!TryShowPanel(mvcManager, explorePanelT, param, out string panelErr))
                    err = "mapfilters: open explore-panel failed: " + panelErr;
            }
            catch (System.Exception e) { err = "mapfilters: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Settle panel animation.
            for (int i = 0; i < 18; i++) yield return null;

            // Navigate to NavmapLocationController and invoke ToggleFilterPanel() to reveal the filter panel.
            try
            {
                object exploreCtl = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                if (exploreCtl == null) { err = "mapfilters: ExplorePanelController not found"; }
                else
                {
                    object navmapCtl = GetPublicProperty(exploreCtl, "NavmapController");
                    if (navmapCtl == null) { err = "mapfilters: NavmapController not found"; }
                    else
                    {
                        object navmapLocationCtl = GetPrivateField(navmapCtl, "navmapLocationController");
                        if (navmapLocationCtl == null) { err = "mapfilters: navmapLocationController not found"; }
                        else
                        {
                            MethodInfo toggleMethod = navmapLocationCtl.GetType().GetMethod("ToggleFilterPanel", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (toggleMethod == null) { err = "mapfilters: ToggleFilterPanel not found"; }
                            else toggleMethod.Invoke(navmapLocationCtl, null);
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "mapfilters: " + (e.InnerException?.Message ?? e.Message); }
            // Non-gating: even if the filter toggle failed, capture whatever the Navmap renders.
            // Settle filter panel animation (ANIMATION_DURATION = 0.2f).
            for (int i = 0; i < 18; i++) yield return null;
            yield return CaptureShot("mapfilters");
            m.error = err ?? "shown";
        }

        // Isolate the always-on left SIDEBAR HUD. SidebarController is PERSISTENT (Layer => CanvasOrdering.SortingLayer.PERSISTENT,
        // SidebarController.cs:81) so it survives CloseAllNonPersistentViews. We close every non-persistent panel/overlay so the
        // bare HUD remains, settle, and capture. Capture-only, zero noise: no IssueCommand/ShowAsync, no commands, no mutations.
        private static IEnumerator AtlasCapture_sidebar(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_sidebar", ok = true };  // NON-GATING: ok ALWAYS stays true
            report.actions.Add(m);

            // mvcManager == null is a plain reference check (cannot throw), so no try/catch / err var is needed.
            if (mvcManager == null) { m.error = "sidebar: mvcManager is null"; yield break; }

            // Close all non-persistent views so only the persistent sidebar HUD is on screen.
            // CloseOpenPanels -> MvcManager.CloseAllNonPersistentViews + 8 settle frames (helper is IEnumerator -> OUTSIDE try).
            yield return CloseOpenPanels(mvcManager);

            // Settle: let any hide animations finish and the bare HUD re-render cleanly.
            for (int i = 0; i < 18; i++) yield return null;

            HideChat(mvcManager);   // chat HUD is PERSISTENT (CloseOpenPanels won't drop it) — hide for the bare-sidebar shot
            yield return CaptureShot("sidebar");
            m.error = "shown";   // sentinel meaning success
        }

        // Capture the always-on minimap HUD widget (bottom/corner of the in-world HUD).
        // Close non-persistent panels so the persistent-layer minimap is unobscured, then
        // (best-effort, non-gating) call MinimapController.ExpandMinimap() to show its expanded
        // state. ExpandMinimap only toggles local view buttons + an animator trigger (no noise:
        // no chat, no network, no account mutation). The minimap canvas renders by default in-world
        // (MinimapController activates it from the player-position query), so the bare HUD shot
        // suffices even if the controller is not reachable.
        private static IEnumerator AtlasCapture_minimap(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_minimap", ok = true };  // NON-GATING: ok ALWAYS stays true
            report.actions.Add(m);

            // Ensure a clean HUD: close any open non-persistent panels (IEnumerator -> OUTSIDE try).
            yield return CloseOpenPanels(mvcManager);

            // Best-effort: drive the minimap into its expanded state. Entirely non-gating —
            // if the controller is unreachable, the always-on minimap still renders, so we
            // never abort the capture; we just record the reason in m.error (informational).
            string note = null;
            try
            {
                object minimapController = FindControllerByTypeName(mvcManager, "MinimapController");
                if (minimapController == null)
                    note = "minimap(non-gating): MinimapController not registered (capturing bare HUD)";
                else
                {
                    MethodInfo expand = minimapController.GetType().GetMethod(
                        "ExpandMinimap",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (expand == null)
                        note = "minimap(non-gating): ExpandMinimap not found (capturing default state)";
                    else
                        expand.Invoke(minimapController, null);
                }
            }
            catch (System.Exception e) { note = "minimap(non-gating): " + (e.InnerException?.Message ?? e.Message); }

            // Settle the expand animation (ANIMATION_TIME = 0.2f) + let the place-info UI populate.
            for (int i = 0; i < 24; i++) yield return null;

            yield return CaptureShot("minimap");
            m.error = note ?? "shown";   // sentinel ("shown") on success; non-gating note otherwise
        }

        // Capture the DEFAULT (unfocused/blurred) in-world CHAT widget on the HUD.
        // Distinct from chatwindow (the focused/expanded state). We deliberately do NOT
        // call ChatOpener.CloseAllViewsAndFocusChat() (that raises a FocusRequestedEvent and
        // drives the chat into FocusedChatState). Instead we only close any non-persistent
        // panels covering the HUD via IMVCManager.CloseAllNonPersistentViews(ct) — the chat
        // panel is PERSISTENT, so it stays mounted in its resting DefaultChatState (the
        // blurred/unfocused state per DCL.Chat.ChatStates.DefaultChatState). No focus, no
        // input, no message sent — pure capture-only, zero noise.
        private static IEnumerator AtlasCapture_chat(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_chat", ok = true };  // NON-GATING: ok ALWAYS stays true
            report.actions.Add(m);

            string err = null;
            try
            {
                if (mvcManager == null)
                {
                    err = "chat: mvcManager is null";
                }
                else
                {
                    // IMVCManager.CloseAllNonPersistentViews(CancellationToken ct = default).
                    // This closes FULLSCREEN/POPUP/OVERLAY views but LEAVES PERSISTENT ones
                    // (the chat HUD widget) mounted, without focusing the chat.
                    MethodInfo closeM = mvcManager.GetType().GetMethod(
                        "CloseAllNonPersistentViews",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (closeM == null)
                        err = "chat: CloseAllNonPersistentViews method not found";
                    else
                        closeM.Invoke(mvcManager, new object[] { System.Threading.CancellationToken.None });
                }
            }
            catch (System.Exception e) { err = "chat: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }   // yield break OUTSIDE the try

            // Restore the default chat HUD: an earlier HideChat (clean-shot driver) deactivated the persistent
            // chat GameObject, so without this H03 captured a bare world. ShowChatDefault re-activates it and
            // advances the FSM to DefaultChatState (the default blurred layout). We do NOT raise focus here.
            yield return ShowChatDefault(mvcManager);

            // Settle: let the hide coroutines for any closed panels complete and the chat
            // widget settle into its default (blurred) layout.
            for (int i = 0; i < 18; i++) yield return null;

            yield return CaptureShot("chat");
            m.error = "shown";   // sentinel meaning success
        }

        // Open the Notifications panel/feed READ-ONLY, WAIT for the initial fetch to finish, then capture.
        // NotificationsPanelController extends ControllerBase<NotificationsMenuView> (single generic) -> its
        // IssueCommand() is the 0-arg form returning ShowCommand<NotificationsMenuView, ControllerNoData>, so
        // we drive the null-param (0-arg) branch of TryShowPanelByName (mirrors DuplicateIdentity/NearbyVoice).
        // It is a POPUP-layer view. Opening it fires InitialNotificationRequestAsync (a read-only
        // GetMostRecentNotificationsAsync fetch); while that fetch is in flight the view shows a LoadingSpinner.
        // The previous driver settled only 24 frames and captured the spinner. FIX: poll the view's loading
        // state until the fetch resolves (NotificationsMenuView.SetLoading toggles LoadingSpinner/ContentContainer;
        // controller's private 'notifications' list + 'needsInitialRequest' flag confirm completion), then the
        // rows-or-empty-state are rendered. We add NO mark-as-read calls and issue no chat/purchase/friend/
        // profile noise; any mark-read is the panel's own intrinsic OnViewShow lifecycle. Capture-only.
        private static IEnumerator AtlasCapture_notifications(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_notifications", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            string err = null;
            bool opened = false;
            try
            {
                if (mvcManager == null) err = "notifications: mvcManager null";
                else
                {
                    // 0-arg IssueCommand() path (ControllerNoData). TryShowPanelByName has its own try/catch
                    // and records lastPanelKey for the post-settle VerifyShown. Read-only: open the feed only.
                    opened = TryShowPanelByName(mvcManager, "DCL.Notifications.NotificationsMenu.NotificationsPanelController", null, out err);
                    if (!opened && err == null) err = "notifications: TryShowPanelByName failed";
                }
            }
            catch (System.Exception e) { err = "notifications: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // ---- Poll the initial notifications fetch to completion (up to ~6s), then capture + verify ----
            // The list may render empty (account has no notifications); the empty-state still renders once the
            // spinner clears, so we capture either way. We never yield inside the try that reads reflectable
            // state; the wait loop's 'yield return null' lives OUTSIDE try (per harness no-yield-in-try rule).
            for (int i = 0; i < 8; i++) yield return null;   // let the open animation + OnBeforeViewShow kick off the fetch

            const int MAX_POLL = 360;                        // ~6s at 60fps cap on the load wait
            bool loadDone = false;
            for (int i = 0; i < MAX_POLL; i++)
            {
                bool spinnerActive = true;   // assume still loading until proven otherwise
                bool sawState = false;
                try
                {
                    object ctl = FindControllerByTypeName(mvcManager, "NotificationsPanelController");
                    if (ctl != null)
                    {
                        // needsInitialRequest flips false the instant InitialNotificationRequestAsync starts;
                        // the spinner is the authoritative "fetch resolved" signal (SetLoading(false) on success).
                        object view = GetMember(ctl, "viewInstance");
                        object spinner = view != null ? GetMember(view, "LoadingSpinner") : null;
                        if (spinner != null)
                        {
                            object active = GetMember(spinner, "activeSelf");
                            if (active is bool b) { spinnerActive = b; sawState = true; }
                        }
                        // Fallback signal: if the private notifications list is populated the rows are ready.
                        if (!sawState)
                        {
                            object list = GetPrivateField(ctl, "notifications");
                            object cnt = list != null ? GetMember(list, "Count") : null;
                            if (cnt is int n) { sawState = true; spinnerActive = (n == 0); }
                        }
                    }
                }
                catch { /* reflection hiccup: keep polling, fall through to fixed settle */ }

                if (sawState && !spinnerActive) { loadDone = true; break; }
                yield return null;
            }

            // If we could never read the loading state, give a generous fixed settle so real data renders.
            if (!loadDone)
                for (int i = 0; i < 60; i++) yield return null;

            // Small post-load settle so list items lay out (LoopList) before the shot.
            for (int i = 0; i < 24; i++) yield return null;
            HideChat(mvcManager);   // hide the persistent chat HUD bleeding into this isolated shot
            yield return CaptureShot("notifications");

            string verifyErr = "no panel key";
            bool shown = lastPanelKey != null && VerifyShown(mvcManager, lastPanelKey, out verifyErr);
            m.error = shown ? "shown" : ("not-shown: " + (verifyErr ?? "no panel key"));
        }

        // Force-show the Connection Status PANEL (the expanded ConnectionPanel overlay), not just the
        // small toggle indicator. ConnectionStatusPanelController is a MonoBehaviour (UIToolkit panel),
        // NOT an MVC controller -> there is no mvcManager.ShowAsync path. Visibility has TWO layers:
        //   1. root container "connection-panel--hidden" USS class, toggled by SetPanelEnabled(bool)
        //      (shows the small ConnectionButton indicator). EnablePanel(true) via the bus does this and
        //      also runs InitializePanel() the first time (activates the GameObject + wires status feeds).
        //   2. the expanded "ConnectionPanel" element, toggled by OnToggleButtonClicked()->panelView.Toggle()
        //      (starts visible=false). The OLD driver only did layer 1 -> bare HUD with no panel open.
        // Here we: raise the bus event (activate + enable root), find the now-active MonoBehaviour, push
        // SYNTHETIC statuses via its public Set*Status methods, then invoke the private OnToggleButtonClicked
        // to expand the panel. Local UIToolkit only; sends nothing to the network -> no noise.
        private static IEnumerator AtlasCapture_connectionstatus(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_connectionstatus", ok = true };
            report.actions.Add(m);

            string err = null;
            object controller = null;
            MethodInfo toggleMethod = null;
            try
            {
                // --- Step 1: raise ChatCommandsBus.ConnectionStatusPanelVisibilityChanged(true). The plugin
                //     handler EnablePanel(true) runs InitializePanel() first time (GameObject.SetActive(true)
                //     + subscribes status feeds) then SetPanelEnabled(true) (root container visible). ---
                Type busType = FindType("DCL.Chat.Commands.ChatCommandsBus");
                if (busType == null) { err = "connectionstatus: ChatCommandsBus type not found"; }

                if (err == null)
                {
                    PropertyInfo instanceProp = busType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                    object busInstance = instanceProp != null ? instanceProp.GetValue(null) : null;
                    if (busInstance == null) { err = "connectionstatus: ChatCommandsBus.Instance is null"; }
                    else
                    {
                        MethodInfo notify = busType.GetMethod("SendConnectionStatusPanelChangedNotification",
                            BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                        if (notify == null) { err = "connectionstatus: SendConnectionStatusPanelChangedNotification(bool) not found"; }
                        else notify.Invoke(busInstance, new object[] { true });
                    }
                }

                // --- Step 2: locate the ConnectionStatusPanelController MonoBehaviour (now active after the
                //     bus event, but include inactive defensively). ---
                if (err == null)
                {
                    Type ctlType = FindType("DCL.UI.ConnectionStatusPanel.ConnectionStatusPanelController");
                    if (ctlType == null) { err = "connectionstatus: ConnectionStatusPanelController type not found"; }
                    else
                    {
                        var found = UnityEngine.Object.FindObjectsByType(ctlType, FindObjectsInactive.Include);
                        controller = (found != null && found.Length > 0) ? found[0] : null;
                        if (controller == null) { err = "connectionstatus: ConnectionStatusPanelController instance not found (plugin not loaded?)"; }
                    }
                }

                // --- Step 3: ensure the GameObject is active (so OnEnable built the view) and the root is
                //     enabled, then push SYNTHETIC statuses via the controller's public Set*Status methods. ---
                if (err == null)
                {
                    Type ctlType = controller.GetType();

                    // Make sure the GameObject is active (InitializePanel should have done this via the bus,
                    // but be robust if the plugin wasn't subscribed yet).
                    object go = ctlType.GetProperty("gameObject", BindingFlags.Public | BindingFlags.Instance)?.GetValue(controller);
                    if (go != null)
                    {
                        bool active = (bool)(go.GetType().GetProperty("activeSelf").GetValue(go));
                        if (!active)
                            go.GetType().GetMethod("SetActive", new[] { typeof(bool) }).Invoke(go, new object[] { true });
                    }

                    // SetPanelEnabled(true): removes "connection-panel--hidden" from the root container.
                    MethodInfo setEnabled = ctlType.GetMethod("SetPanelEnabled", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                    if (setEnabled != null) setEnabled.Invoke(controller, new object[] { true });

                    // Synthetic ConnectionStatus values (None/Lost/Poor/Good/Excellent).
                    Type csEnum = FindType("DCL.UI.ConnectionStatusPanel.ConnectionStatus");
                    Type abEnum = FindType("DCL.Ipfs.AssetBundleRegistryEnum"); // complete/fallback/pending
                    if (csEnum != null)
                    {
                        object good = Enum.Parse(csEnum, "Good");
                        object excellent = Enum.Parse(csEnum, "Excellent");
                        ctlType.GetMethod("SetSceneStatus", new[] { csEnum })?.Invoke(controller, new[] { good });
                        ctlType.GetMethod("SetSceneRoomStatus", new[] { csEnum })?.Invoke(controller, new[] { excellent });
                        ctlType.GetMethod("SetGlobalRoomStatus", new[] { csEnum })?.Invoke(controller, new[] { good });
                    }
                    if (abEnum != null)
                    {
                        // SetAssetBundleSceneStatus takes AssetBundleRegistryEnum? (nullable). Pass a boxed
                        // non-null value so the AssetBundle row is shown with a "complete" status.
                        MethodInfo setAb = null;
                        foreach (MethodInfo mi in ctlType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                            if (mi.Name == "SetAssetBundleSceneStatus" && mi.GetParameters().Length == 1) { setAb = mi; break; }
                        if (setAb != null) setAb.Invoke(controller, new[] { Enum.Parse(abEnum, "complete") });
                    }

                    // --- Step 4: expand the panel. OnToggleButtonClicked() (private) -> panelView.Toggle(),
                    //     which flips visible=true and removes "connection-panel--hidden" from the panel root. ---
                    toggleMethod = ctlType.GetMethod("OnToggleButtonClicked", BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (toggleMethod != null) toggleMethod.Invoke(controller, null);
                    else
                    {
                        // Fallback: reach the panelView field and call its public Toggle() directly.
                        object panelView = GetPrivateField(controller, "panelView");
                        MethodInfo toggle = panelView?.GetType().GetMethod("Toggle", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                        if (toggle != null) toggle.Invoke(panelView, null);
                        else err = "connectionstatus: neither OnToggleButtonClicked nor panelView.Toggle found";
                    }
                }
            }
            catch (System.Exception e) { err = "connectionstatus: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            for (int i = 0; i < 18; i++) yield return null;  // settle: UIToolkit redraw + CSS layout
            yield return CaptureShot("connectionstatus");

            // Collapse + hide again (non-gating; best-effort, no yield inside the try).
            try
            {
                if (toggleMethod != null && controller != null)
                    toggleMethod.Invoke(controller, null);
                MethodInfo setEnabled = controller?.GetType().GetMethod("SetPanelEnabled", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
                if (setEnabled != null) setEnabled.Invoke(controller, new object[] { false });
            }
            catch { }

            m.error = "shown";
        }

        // Show the chat panel, type "@test" into the LOCAL input field (SetTextWithoutNotify -> no message sent),
        // drive the autocomplete logic, and screenshot the @mention input-suggestion popup. NO NOISE: nothing is
        // ever submitted/sent; we only locally populate the suggestion UI.
        private static IEnumerator AtlasCapture_inputsuggestions(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_inputsuggestions", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            // wrong-grid isolation: close the leftover persistent Communities/Explore grid so the chat panel + @mention
            // suggestion overlay (which render UNDER the full-screen ExplorePanel) are actually visible in the shot.
            yield return HideExplorePanel(mvcManager);

            // Show AND focus the chat: only FocusedChatState drives the chat-input FSM into
            // TypingEnabledChatInputState, which is the ONLY state that subscribes inputField.onValueChanged
            // (TypingEnabledChatInputState.cs:84). Without it the manual onValueChanged.Invoke below fires no
            // listener and the @mention suggestion panel never appears (round-1 bare-world regression).
            yield return ShowAndFocusChat(mvcManager);

            string err = null;
            object inputSuggestionPanelView = null;

            // Step 2: Walk to the input field + suggestion panel:
            //   ChatMainSharedAreaController.viewInstance(ChatMainSharedAreaView).ChatPanelView.InputView(ChatInputView)
            //     .inputField(CustomInputField : TMP_InputField)  and  .suggestionPanel(InputSuggestionPanelView)
            object customInputField = null;
            try
            {
                object chatCtl = FindControllerByTypeName(mvcManager, "ChatMainSharedAreaController");
                if (chatCtl == null) err = "inputsuggestions: ChatMainSharedAreaController instance not found";

                object view = chatCtl != null ? GetMember(chatCtl, "viewInstance") : null;
                if (err == null && view == null) err = "inputsuggestions: viewInstance null";

                object chatPanelView = view != null ? GetMember(view, "ChatPanelView") : null;
                if (err == null && chatPanelView == null) err = "inputsuggestions: ChatPanelView not found";

                object inputView = chatPanelView != null ? GetMember(chatPanelView, "InputView") : null;
                if (err == null && inputView == null) err = "inputsuggestions: InputView not found";

                if (err == null)
                {
                    inputSuggestionPanelView = GetMember(inputView, "suggestionPanel");
                    if (inputSuggestionPanelView == null) err = "inputsuggestions: suggestionPanel not found";

                    customInputField = GetMember(inputView, "inputField");
                    if (err == null && customInputField == null) err = "inputsuggestions: inputField not found";
                }
            }
            catch (System.Exception e) { err = "inputsuggestions: suggestion-panel lookup failed: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield return CaptureShot("inputsuggestions"); yield break; }

            // Step 3: LOCAL-ONLY trigger of the @mention suggestions. SetTextWithoutNotify writes the text without
            // sending or firing duplicate events; we then manually invoke onValueChanged so the autocomplete logic
            // runs. Nothing is submitted — no message ever leaves the client (NO NOISE).
            try
            {
                MethodInfo setTextMethod = customInputField.GetType().GetMethod("SetTextWithoutNotify", BindingFlags.Public | BindingFlags.Instance);
                if (setTextMethod != null)
                    setTextMethod.Invoke(customInputField, new object[] { "@test" });

                object onValueChanged = GetMember(customInputField, "onValueChanged");
                if (onValueChanged != null)
                {
                    MethodInfo invokeMethod = onValueChanged.GetType().GetMethod("Invoke", BindingFlags.Public | BindingFlags.Instance);
                    if (invokeMethod != null)
                        invokeMethod.Invoke(onValueChanged, new object[] { "@test" });
                }
            }
            catch (System.Exception e) { err = "inputsuggestions: input-trigger failed: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield return CaptureShot("inputsuggestions"); yield break; }

            // Settle: let the suggestion panel render/animate in.
            for (int i = 0; i < 10; i++) yield return null;

            // Step 4: Record whether the panel reports active (non-gating note), then capture.
            string note = "shown";
            try
            {
                object isActive = GetMember(inputSuggestionPanelView, "IsActive");
                if (!(isActive is bool active) || !active)
                    note = "shown (suggestion-panel reported inactive; data-gated @mention candidates may be empty)";
            }
            catch (System.Exception e) { note = "shown (verify-active failed: " + (e.InnerException?.Message ?? e.Message) + ")"; }

            yield return CaptureShot("inputsuggestions");
            m.error = note;  // sentinel: "shown" prefix = success
        }

        // Expand+focus the chat window via ChatOpener.Instance.CloseAllViewsAndFocusChat() (UI-only; sends no message) and screenshot it
        private static IEnumerator AtlasCapture_chatwindow(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_chatwindow", ok = true };  // NON-GATING: ok stays true
            report.actions.Add(m);

            // wrong-grid isolation: drop the leftover persistent Communities/Explore grid so the focused/expanded
            // chat window (a persistent SHARED_AREA view that sits UNDER the full-screen ExplorePanel) is visible.
            yield return HideExplorePanel(mvcManager);

            // Show AND focus the chat. ChatOpener.CloseAllViewsAndFocusChat() raises FocusRequestedEvent, but
            // that only expands the panel when the chat FSM is in DefaultChatState. The FSM starts in
            // InitChatState and only advances to DefaultChatState when the chat MVC view is SHOWN
            // (ChatStateMachine.cs:73-78). The round-1 driver called focus WITHOUT showing first, so
            // OnFocusRequested no-opped in InitChatState -> bare world. ShowAndFocusChat does both in order.
            yield return ShowAndFocusChat(mvcManager);

            yield return CaptureShot("chatwindow");
            m.error = "shown";   // sentinel meaning success
        }

        // Opens the chat reaction selector bar (situational mode) by clicking the chat ChatReactionButton, then
        // screenshots the populated emoji-shortcut picker. NO NOISE: opening the selector is local-only UI; the
        // network ToggleReaction / situational particle only fire when an emoji is actually picked, which we never do.
        private static IEnumerator AtlasCapture_reactions(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_reactions", ok = true };  // NON-GATING
            report.actions.Add(m);

            // wrong-grid isolation: drop the leftover persistent Communities/Explore grid so the chat reaction
            // selector bar (chat HUD overlay under the full-screen ExplorePanel) is the surface that renders.
            yield return HideExplorePanel(mvcManager);

            // Show AND focus the chat first: the ChatReactionButtonView is only Show()n by reactionsPresenter.Show()
            // inside ChatUIMediator.SetupForFocusedState() (ChatUIMediator.cs:76), which runs on entering
            // FocusedChatState. Without focus the button is inactive (or its parent chat panel is hidden), so the
            // click below opens nothing. ShowAndFocusChat puts the chat into FocusedChatState and reveals it.
            yield return ShowAndFocusChat(mvcManager);

            string err = null;
            object reactionButtonView = null;   // DCL.Chat.ChatReactions.Views.ChatReactionButtonView (MonoBehaviour)
            object reactionButton = null;       // UnityEngine.UI.Button

            try
            {
                // The ChatReactionsPresenter / ChatPanelPresenter are constructed as locals inside ChatPlugin and
                // are NOT reachable from MvcManager (chat's MVC entry is ChatMainSharedAreaController, which exposes
                // no presenter/view handle) and ChatPlugin has no singleton. So we reach the situational reaction
                // bar the same way the user does: find the ChatReactionButtonView scene component and click its
                // Button. onClick -> ChatReactionsPresenter.OnButtonClicked -> shows the situational selector bar.
                Type viewType = FindType("DCL.Chat.ChatReactions.Views.ChatReactionButtonView");
                if (viewType == null)
                {
                    err = "reactions: ChatReactionButtonView type not found";
                }
                else
                {
                    var found = UnityEngine.Object.FindObjectsByType(viewType, FindObjectsInactive.Include);
                    // Prefer an active-in-hierarchy instance (the button is SetActive(false) when the
                    // CHAT_REACTIONS_ENABLED feature flag is off).
                    foreach (var o in found)
                    {
                        if (o is Behaviour beh && beh != null && beh.isActiveAndEnabled) { reactionButtonView = o; break; }
                        if (o is Component comp && comp != null && comp.gameObject.activeInHierarchy) { reactionButtonView = o; break; }
                    }
                    if (reactionButtonView == null && found != null && found.Length > 0)
                        reactionButtonView = found[0];   // fall back to any instance (may be inactive)

                    if (reactionButtonView == null)
                        err = "reactions: no ChatReactionButtonView instance in scene (chat not initialized?)";
                    else
                        reactionButton = GetPublicProperty(reactionButtonView, "ReactionButton");
                }
            }
            catch (System.Exception e) { err = "reactions: " + (e.InnerException?.Message ?? e.Message); }

            // Degrade gracefully: if we cannot reach the button, still capture whatever renders (chat panel / world)
            // so the run produces an artifact, and record the gap.
            if (err != null)
            {
                for (int i = 0; i < 18; i++) yield return null;
                yield return CaptureShot("reactions");
                m.error = err + " (captured fallback)";
                yield break;
            }

            // Invoke the button's onClick to open the situational selector bar (local-only, no network).
            try
            {
                // Reflection-only (the client is UITK; uGUI's UnityEngine.UI.Button isn't a compile-time
                // type here): invoke ReactionButton.onClick.Invoke() via reflection.
                object onClick = reactionButton != null ? GetMember(reactionButton, "onClick") : null;
                var ocInvoke = onClick?.GetType().GetMethod("Invoke", System.Type.EmptyTypes);
                if (ocInvoke != null) ocInvoke.Invoke(onClick, null);
                else err = "reactions: ReactionButton.onClick.Invoke() not reachable";
            }
            catch (System.Exception e) { err = "reactions: onClick invoke failed: " + (e.InnerException?.Message ?? e.Message); }

            // Let the selector bar open/animate, then capture regardless of whether the click resolved cleanly.
            for (int i = 0; i < 18; i++) yield return null;
            yield return CaptureShot("reactions");
            m.error = err == null ? "shown" : err + " (captured fallback)";
        }

        // Open the user mini-profile / passport CARD for a CHAT message author (read-only).
        // FLOW (verified in source): clicking a chat message author normally routes through
        // ChatContextMenuService.ShowUserProfileMenuAsync -> IMVCManagerMenusAccessFacade
        // .ShowUserProfileContextMenuFromWalletIdAsync(...) (the small context menu) and, from
        // there, the full passport is opened via MVCManagerMenusAccessFacade.OpenPassportAsync ->
        // mvcManager.ShowAsync(PassportController.IssueCommand(new PassportParams(userId))).
        // The context-menu path needs a live screen-space click Position + an IProfileRepository
        // compact fetch and is hard to drive headlessly, so we open the SAME read-only passport
        // card the menu's "View Profile" leads to, directly via PassportController.IssueCommand.
        // TARGET USER: we read the chat panel's RENDERED message list. ChatMessageFeedView
        // (MonoBehaviour) holds a private IReadOnlyList<ChatMessageViewModel> 'viewModels'; each
        // ChatMessageViewModel.Message is a ChatMessage struct exposing SenderWalletAddress /
        // SenderWalletId / IsSentByOwnUser / IsSystemMessage. We take the FIRST message from
        // ANOTHER user (not own, not system) and open ITS author's passport (isOwnProfile:false).
        // DEGRADE: if no other-user message is rendered (empty/quiet chat), open the OWN passport
        // card (isOwnProfile:true) via selfProfile, so the route still captures a profile card.
        // CAPTURE-ONLY: PassportController shows a read-only OVERVIEW view; we never send chat,
        // never open the friend/block actions, never invoke any context-menu action -> zero noise.
        private static IEnumerator AtlasCapture_chatprofile(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_chatprofile", ok = true };  // NON-GATING: ok ALWAYS stays true
            report.actions.Add(m);

            // --- Phase 1: find a chat message author wallet from the rendered feed. No yields here. ---
            string err = null;
            string targetUserId = null;       // other user's wallet/userId
            bool isOwnProfile = false;
            try
            {
                Type feedType = FindType("DCL.Chat.ChatMessages.ChatMessageFeedView");
                if (feedType == null)
                    err = "chatprofile: ChatMessageFeedView type not found";
                else
                {
                    var feeds = UnityEngine.Object.FindObjectsByType(feedType, FindObjectsInactive.Include);
                    if (feeds == null || feeds.Length == 0)
                        err = "chatprofile: no ChatMessageFeedView instance in scene";
                    else
                    {
                        FieldInfo vmField = feedType.GetField("viewModels", BindingFlags.NonPublic | BindingFlags.Instance);
                        foreach (var feed in feeds)
                        {
                            object listObj = vmField != null ? vmField.GetValue(feed) : null;
                            var list = listObj as System.Collections.IEnumerable;
                            if (list == null) continue;
                            foreach (var vm in list)
                            {
                                if (vm == null) continue;
                                // ChatMessageViewModel.Message (ChatMessage struct, public getter)
                                object msg = GetPublicProperty(vm, "Message");
                                if (msg == null) continue;
                                bool isOwn = false, isSystem = false;
                                object isOwnObj = GetMember(msg, "IsSentByOwnUser");
                                object isSysObj = GetMember(msg, "IsSystemMessage");
                                if (isOwnObj is bool b1) isOwn = b1;
                                if (isSysObj is bool b2) isSystem = b2;
                                if (isOwn || isSystem) continue;
                                string wallet = GetMember(msg, "SenderWalletAddress") as string;
                                if (string.IsNullOrEmpty(wallet))
                                    wallet = GetMember(msg, "SenderWalletId") as string;
                                if (!string.IsNullOrEmpty(wallet))
                                {
                                    targetUserId = wallet;
                                    break;
                                }
                            }
                            if (!string.IsNullOrEmpty(targetUserId)) break;
                        }
                        if (string.IsNullOrEmpty(targetUserId))
                            err = "chatprofile: no other-user message rendered; degrading to own profile";
                    }
                }
            }
            catch (System.Exception e) { err = "chatprofile: scan: " + (e.InnerException?.Message ?? e.Message); }
            // Non-fatal: if no other-user wallet, we degrade to own profile below.

            // --- Phase 2 (degrade): resolve OWN userId if no chat author was found. No yields here. ---
            object profileTask = null;
            if (string.IsNullOrEmpty(targetUserId))
            {
                isOwnProfile = true;
                try
                {
                    object selfProfile = ReachSelfProfile(dynamicContainer);
                    if (selfProfile != null)
                    {
                        object ownProfile = GetPublicProperty(selfProfile, "OwnProfile");
                        if (ownProfile != null)
                            targetUserId = GetPublicProperty(ownProfile, "UserId") as string;

                        if (string.IsNullOrEmpty(targetUserId))
                        {
                            MethodInfo profileAsync = selfProfile.GetType()
                                .GetMethod("ProfileAsync", BindingFlags.Public | BindingFlags.Instance);
                            if (profileAsync != null)
                                profileTask = profileAsync.Invoke(selfProfile, new object[] { System.Threading.CancellationToken.None });
                        }
                    }
                }
                catch (System.Exception e) { err = (err == null ? "" : err + "; ") + "chatprofile: self-id: " + (e.InnerException?.Message ?? e.Message); }
            }

            // --- Phase 3: await the own-profile fetch if needed (OUTSIDE any try). ---
            if (string.IsNullOrEmpty(targetUserId) && profileTask != null)
            {
                yield return AwaitUniTask(profileTask);
                if (awaitedError == null && awaitedResult != null)
                    targetUserId = GetPublicProperty(awaitedResult, "UserId") as string;
            }

            // --- Phase 4: show the COMPACT user-profile context hovercard (NOT the full Passport panel). ---
            // Correct H11 target: IMVCManagerMenusAccessFacade.ShowUserProfileContextMenuFromWalletIdAsync(...),
            // reached via the source-generated static accessor ViewDependencies.GlobalUIViews (same call SimpleProfileView
            // and FriendSectionController make). It internally does profileRepository.GetCompactAsync(walletId) and renders
            // the GenericContextMenuUserProfileView hovercard. closeMenuTask MUST stay pending or the menu closes instantly,
            // so we pass a never-completing UniTask built from a UniTaskCompletionSource<AsyncUnit>.Task. We invoke through
            // the public optional-arg overload supplying only the 5 required positionals (Type.Missing for the rest).
            // NO pointer/hover clicks. NO-NOISE: read-only profile fetch + local hovercard. Build is all reflection; the
            // single yield (settle) is OUTSIDE the try, mirroring the other drivers.
            string openErr = null;
            try
            {
                if (string.IsNullOrEmpty(targetUserId))
                    openErr = "no target userId (no chat author and own id unavailable)";
                else
                {
                    Type viewDepsT = FindType("MVC.ViewDependencies");
                    Type web3AddrT = FindType("DCL.Web3.Web3Address");
                    if (viewDepsT == null) openErr = "ViewDependencies type not found";
                    else if (web3AddrT == null) openErr = "Web3Address type not found";
                    else
                    {
                        // Generated static accessor: public static IMVCManagerMenusAccessFacade GlobalUIViews { get; }
                        object facade = viewDepsT.GetProperty("GlobalUIViews", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                        if (facade == null) openErr = "ViewDependencies.GlobalUIViews unavailable (not initialized)";
                        else
                        {
                            // Web3Address(string?) ctor.
                            object walletId = System.Activator.CreateInstance(web3AddrT, targetUserId);

                            // A UniTask that never completes -> the hovercard stays open until we capture.
                            // UniTaskCompletionSource<AsyncUnit>().Task is a UniTask<AsyncUnit>; the method wants a
                            // non-generic UniTask, so call .Task on a non-generic UniTaskCompletionSource.
                            Type tcsT = FindType("Cysharp.Threading.Tasks.UniTaskCompletionSource");
                            object closeMenuTask = null;
                            if (tcsT != null)
                            {
                                object tcs = System.Activator.CreateInstance(tcsT);
                                closeMenuTask = tcsT.GetProperty("Task", BindingFlags.Public | BindingFlags.Instance)?.GetValue(tcs);
                            }
                            if (closeMenuTask == null)
                                openErr = "could not build never-completing UniTask (UniTaskCompletionSource.Task)";
                            else
                            {
                                MethodInfo showM = facade.GetType().GetMethod("ShowUserProfileContextMenuFromWalletIdAsync",
                                    BindingFlags.Public | BindingFlags.Instance);
                                if (showM == null) openErr = "ShowUserProfileContextMenuFromWalletIdAsync not found";
                                else
                                {
                                    ParameterInfo[] ps = showM.GetParameters();
                                    object[] args = new object[ps.Length];
                                    // Center-screen anchor so the hovercard renders fully on-screen.
                                    var pos = new UnityEngine.Vector3(UnityEngine.Screen.width * 0.5f, UnityEngine.Screen.height * 0.5f, 0f);
                                    for (int i = 0; i < ps.Length; i++)
                                    {
                                        switch (ps[i].Name)
                                        {
                                            case "walletId":     args[i] = walletId; break;
                                            case "position":     args[i] = pos; break;
                                            case "offset":       args[i] = UnityEngine.Vector2.zero; break;
                                            case "ct":           args[i] = System.Threading.CancellationToken.None; break;
                                            case "closeMenuTask":args[i] = closeMenuTask; break;
                                            default:             args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : System.Type.Missing; break;
                                        }
                                    }
                                    showM.Invoke(facade, args);   // fire-and-forget UniTask
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { openErr = e.InnerException?.Message ?? e.Message; }
            if (openErr != null) err = (err == null ? "chatprofile: open: " + openErr : err + "; open: " + openErr);

            // Let GetCompactAsync resolve and the hovercard instantiate + render.
            for (int i = 0; i < 60; i++) yield return null;

            yield return CaptureShot("chatprofile");

            // Non-gating: capture whatever rendered; report residual notes but keep ok=true.
            m.error = "shown";
        }

        // Opens the Donations "Send a Tip" panel (read-only: shows the picker; never clicks Donate/Confirm).
        private static IEnumerator AtlasCapture_donations(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_donations", ok = true };  // NON-GATING: ok ALWAYS stays true
            report.actions.Add(m);

            // NO NOISE: we only OPEN and visually populate the panel. The actual tip/donation requires a
            // subsequent button click which this driver never performs, so opening it is non-destructive.

            string err = null;
            object placesApi = null;
            object searchTask = null;

            // --- Phase A: synchronous reflection to reach the Places API and fire the search ---
            try
            {
                // Same access path the harness EnumerateContent uses: ExplorePanelController.PlacesController
                // -> PlacesResultsController -> private field placesAPIService.
                object explore = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                object placesController = explore != null ? GetMember(explore, "PlacesController") : null;
                object placesResults = placesController != null ? GetMember(placesController, "PlacesResultsController") : null;
                placesApi = placesResults != null ? GetPrivateField(placesResults, "placesAPIService") : null;

                if (placesApi == null) err = "donations: placesAPIService not found";

                if (err == null)
                {
                    MethodInfo searchMethod = null;
                    foreach (MethodInfo mi in placesApi.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        if (mi.Name == "SearchDestinationsAsync") { searchMethod = mi; break; }

                    if (searchMethod == null) err = "donations: SearchDestinationsAsync not found";
                    else
                    {
                        ParameterInfo[] searchParams = searchMethod.GetParameters();
                        object[] searchArgs = new object[searchParams.Length];
                        for (int i = 0; i < searchParams.Length; i++)
                        {
                            if (searchParams[i].ParameterType == typeof(int)) searchArgs[i] = (i == 0) ? 0 : 20;
                            else if (searchParams[i].ParameterType == typeof(System.Threading.CancellationToken)) searchArgs[i] = System.Threading.CancellationToken.None;
                            else searchArgs[i] = searchParams[i].HasDefaultValue ? Type.Missing : null;
                        }
                        searchTask = searchMethod.Invoke(placesApi, searchArgs);
                        if (searchTask == null) err = "donations: SearchDestinationsAsync returned null";
                    }
                }
            }
            catch (System.Exception e) { err = "donations: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield return CaptureShot("donations"); yield break; }

            // --- Await the live search OUTSIDE any try/catch ---
            yield return AwaitUniTask(searchTask);
            if (awaitedError != null) { m.error = "donations: search await failed: " + awaitedError; yield return CaptureShot("donations"); yield break; }

            // --- Phase B: synchronous extraction + param build + open ---
            object param = null;
            try
            {
                // Find a place that has a creator_address so the panel has a real recipient to render.
                string creatorAddress = null;
                Vector2Int baseParcel = Vector2Int.zero;
                object data = GetMember(awaitedResult, "data") ?? GetMember(awaitedResult, "Data");
                if (data is System.Collections.IEnumerable en)
                {
                    foreach (object place in en)
                    {
                        string addr = GetMember(place, "creator_address") as string;
                        if (!string.IsNullOrEmpty(addr))
                        {
                            creatorAddress = addr;
                            object bp = GetMember(place, "base_position_processed");
                            if (bp is Vector2Int v2) baseParcel = v2;
                            break;
                        }
                    }
                }

                if (string.IsNullOrEmpty(creatorAddress))
                {
                    err = "donations: no place with creator_address found (data-gated)";
                }
                else
                {
                    Type paramType = FindType("DCL.Donations.UI.DonationsPanelParameter");
                    if (paramType == null) err = "donations: DonationsPanelParameter type not found";
                    else
                    {
                        MethodInfo createMethod = null;
                        foreach (MethodInfo mi in paramType.GetMethods(BindingFlags.Public | BindingFlags.Static))
                            if (mi.Name == "Create" && mi.GetParameters().Length == 2) { createMethod = mi; break; }

                        if (createMethod == null) err = "donations: DonationsPanelParameter.Create not found";
                        else
                        {
                            param = createMethod.Invoke(null, new object[] { creatorAddress, baseParcel });
                            if (param == null) err = "donations: Create returned null";
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "donations: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield return CaptureShot("donations"); yield break; }

            // --- Open the donations panel (boxed param, 1-arg IssueCommand path) ---
            bool opened = TryShowPanelByName(mvcManager, "DCL.Donations.UI.DonationsPanelController", param, out err);
            if (!opened) { m.error = "donations: open failed: " + err; yield return CaptureShot("donations"); yield break; }

            // --- Content-load POLL (OUTSIDE try) ---
            // LoadDataAsync fires SetDefaultLoadingState(true) -> donationDefaultView.loadingView (SkeletonLoadingView)
            // shows its skeleton with loadingCanvasGroup.alpha = 1f. When the WhenAll (profile + balance +
            // mana price + scene name) completes, the finally calls SetDefaultLoadingState(false) ->
            // HideLoading() drives loadingCanvasGroup.alpha -> 0f and fades loadedCanvasGroup in. We poll the
            // skeleton's loadingCanvasGroup.alpha and consider the form populated once it has dropped to ~0.
            // Up to ~6s (360 frames); each read is guarded in a tiny no-yield try.
            bool loaded = false;
            for (int i = 0; i < 360 && !loaded; i++)
            {
                try
                {
                    object ctl = FindControllerByTypeName(mvcManager, "DonationsPanelController");
                    object view = ctl != null ? GetMember(ctl, "viewInstance") : null;
                    object defView = view != null ? GetMember(view, "donationDefaultView") : null;
                    object skeleton = defView != null ? GetMember(defView, "loadingView") : null;
                    object loadingCg = skeleton != null ? GetMember(skeleton, "loadingCanvasGroup") : null;
                    if (loadingCg != null)
                    {
                        object alphaObj = GetMember(loadingCg, "alpha");
                        if (alphaObj is float a && a < 0.05f) loaded = true;
                    }
                    // POSITIVE done-signal fallback: HideLoading() fades loadedCanvasGroup in to alpha 1.
                    // The prior driver keyed ONLY on the skeleton's loadingCanvasGroup.alpha dropping to 0;
                    // if that member chain ever fails to resolve, the silent catch left 'loaded' false until
                    // timeout and we captured all-skeleton. Also accept the loaded form fading IN.
                    if (!loaded)
                    {
                        object loadedCg = skeleton != null ? GetMember(skeleton, "loadedCanvasGroup") : null;  // loadedCanvasGroup is on SkeletonLoadingView, not DonationDefaultView
                        object loadedAlpha = loadedCg != null ? GetMember(loadedCg, "alpha") : null;
                        if (loadedAlpha is float la && la > 0.9f) loaded = true;
                    }
                }
                catch { /* keep polling */ }
                yield return null;
            }

            // Allow the loadedCanvasGroup fade-in (fadeDuration 0.3s) + final layout to settle. Generous
            // fallback (was 48) so that when the alpha poll can't be read and times out, the live WhenAll
            // (profile + balance + mana price + scene name) still has time to finish before capture.
            for (int i = 0; i < (loaded ? 48 : 180); i++) yield return null;
            yield return CaptureShot("donations");                      // always capture, even if degraded
            m.error = "shown";                                          // sentinel meaning success
        }

        // In-world camera HUD capture: enables local photo-mode (ToggleInWorldCameraRequest on the
        // camera entity) so the InWorldCamera HUD renders, then screenshots the GameView. Purely
        // LOCAL — entering camera mode toggles only this client's camera + HUD; it does NOT take a
        // screenshot for sharing (that is a separate TakeScreenshotRequest, never issued here) and
        // sends nothing to other users. No noise.
        private static IEnumerator AtlasCapture_camera(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_camera", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            string err = null;
            try
            {
                // --- Resolve the Arch ECS World: dynamicContainer.RealmController (public prop)
                //     -> IGlobalRealmController.GlobalWorld (prop) -> GlobalWorld.EcsWorld (public FIELD).
                //     EcsWorld is a FIELD, so use GetMember (property-THEN-field), not GetPublicProperty.
                object ecsWorld = null;
                object realmController = GetPublicProperty(dynamicContainer, "RealmController");
                if (realmController == null) err = "camera: RealmController not found on dynamicContainer";
                else
                {
                    object globalWorld = GetMember(realmController, "GlobalWorld");
                    if (globalWorld == null) err = "camera: GlobalWorld not found on RealmController";
                    else
                    {
                        ecsWorld = GetMember(globalWorld, "EcsWorld");
                        if (ecsWorld == null) err = "camera: EcsWorld not found on GlobalWorld";
                    }
                }

                // Fallback: the editor-only GlobalWorld.ECSWorldInstance static holds the same World.
                if (ecsWorld == null && err != null)
                {
                    Type gwType = FindType("Global.Dynamic.GlobalWorld");
                    var sp = gwType?.GetProperty("ECSWorldInstance", BindingFlags.Public | BindingFlags.Static);
                    object viaStatic = sp?.GetValue(null);
                    if (viaStatic != null) { ecsWorld = viaStatic; err = null; }
                }

                if (err == null && ecsWorld != null)
                {
                    Type worldType = ecsWorld.GetType();

                    // --- Cache the camera entity. CacheCamera is an EXTENSION method on
                    //     DCL.CharacterCamera.WorldExtensions (static, (this World)), NOT an instance
                    //     method on World — call it statically.
                    Type weType = FindType("DCL.CharacterCamera.WorldExtensions");
                    MethodInfo cacheMethod = weType?.GetMethod("CacheCamera", BindingFlags.Public | BindingFlags.Static);
                    if (cacheMethod == null) err = "camera: WorldExtensions.CacheCamera not found";
                    else
                    {
                        object singleInstanceCamera = cacheMethod.Invoke(null, new[] { ecsWorld }); // SingleInstanceEntity
                        if (singleInstanceCamera == null) err = "camera: CacheCamera returned null";
                        else
                        {
                            // SingleInstanceEntity -> Arch Entity via its implicit operator (op_Implicit).
                            object cameraEntity = singleInstanceCamera;
                            foreach (MethodInfo op in singleInstanceCamera.GetType()
                                         .GetMethods(BindingFlags.Public | BindingFlags.Static))
                                if (op.Name == "op_Implicit" && op.ReturnType.Name == "Entity")
                                { cameraEntity = op.Invoke(null, new[] { singleInstanceCamera }); break; }

                            // --- Build ToggleInWorldCameraRequest { IsEnable = true, Source = "Harness" }.
                            //     Fields verified: bool IsEnable, string Source, CameraMode? TargetCameraMode.
                            Type requestType = FindType("DCL.InWorldCamera.ToggleInWorldCameraRequest");
                            if (requestType == null) err = "camera: ToggleInWorldCameraRequest type not found";
                            else
                            {
                                object request = Activator.CreateInstance(requestType);
                                requestType.GetField("IsEnable")?.SetValue(request, true);
                                requestType.GetField("Source")?.SetValue(request, "Harness");
                                // TargetCameraMode left at default null.

                                // --- world.Add<ToggleInWorldCameraRequest>(in Entity, in T) is a GENERIC
                                //     instance method on the Arch World. Find the single-generic-arg, 2-param
                                //     overload and close it over the request type.
                                // Arch's World exposes TWO Add<T>(_, in T) overloads with the SAME
                                // arity (1 generic arg, 2 params): Add<T>(in Entity, in T) and the bulk
                                // Add<T>(in QueryDescription, in T). The old "first match" grabbed the
                                // QueryDescription one and threw "Entity cannot be converted to
                                // QueryDescription&" (M03 camera failed, no PNG). Require the single-entity
                                // overload by checking the first param is Entity (passed `in` -> "Entity&").
                                MethodInfo addGeneric = null;
                                foreach (MethodInfo mi in worldType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                {
                                    if (mi.Name != "Add" || !mi.IsGenericMethodDefinition
                                        || mi.GetGenericArguments().Length != 1
                                        || mi.GetParameters().Length != 2) continue;
                                    if (mi.GetParameters()[0].ParameterType.Name.StartsWith("Entity")) { addGeneric = mi; break; }
                                }
                                if (addGeneric == null) err = "camera: generic World.Add<T>(in Entity,component) not found";
                                else
                                {
                                    addGeneric.MakeGenericMethod(requestType)
                                              .Invoke(ecsWorld, new object[] { cameraEntity, request });
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "camera: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }           // yield break OUTSIDE the try

            // ToggleInWorldCameraActivitySystem.Update consumes the request next frame and calls
            // EnableCamera -> hudController.Show() (MVC ShowAsync). Let the HUD show + camera settle.
            for (int i = 0; i < 30; i++) yield return null;
            yield return CaptureShot("camera");
            m.error = "shown";                                         // sentinel: success
        }

        // Camera Reel gallery (registry M04): opens the ExplorePanel to ExploreSections.CameraReel via the
        // existing TryOpenExplorePanel helper (IssueCommand -> MvcManager.ShowAsync). Selecting that section runs
        // CameraReelController.Activate() -> ShowAsync, which does READ-ONLY GETs only: GetUserGalleryStorageInfoAsync
        // (storage status) + CameraReelGalleryController.ShowWalletGalleryAsync (paginated thumbnail listing). When the
        // account has zero photos, view.emptyState is shown -> still a valid capture. NO upload/delete/share/publish
        // or any mutation is performed -> no noise. Belt-and-suspenders: after the panel shows we also call the public
        // ISection CameraReelController.Activate() directly (read-only) in case the section-selector skipped
        // re-activation, then we settle for the network GETs and capture whatever rendered.
        private static IEnumerator AtlasCapture_reel(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_reel", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            // ---- Phase 1: open the ExplorePanel on the CameraReel section (reuse the harness helper) ----
            string err = null;
            try
            {
                if (mvcManager == null) err = "reel: mvcManager null";
                else if (!TryOpenExplorePanel(mvcManager, "CameraReel", null, out string openErr))
                    err = "reel: open CameraReel section: " + openErr;
                // TryOpenExplorePanel sets lastPanelKey to the ExplorePanel controller key on success.
            }
            catch (System.Exception e) { err = "reel: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }   // yield break OUTSIDE the try

            // Let the ExplorePanel view instantiate and OnViewShow toggle the CameraReel section on.
            for (int i = 0; i < 18; i++) yield return null;

            // ---- Phase 2 (belt-and-suspenders): directly Activate() the CameraReel ISection (public, read-only) ----
            // so its ShowAsync re-runs even if the section-selector skipped re-activation. Non-gating.
            try
            {
                object explorePanelCtl = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                if (explorePanelCtl != null)
                {
                    object cameraReelController = GetMember(explorePanelCtl, "CameraReelController"); // public prop, ISection
                    if (cameraReelController != null)
                    {
                        MethodInfo activate = cameraReelController.GetType()
                            .GetMethod("Activate", BindingFlags.Public | BindingFlags.Instance);
                        activate?.Invoke(cameraReelController, null);
                    }
                }
            }
            catch (System.Exception e)
            {
                string aErr = e.InnerException?.Message ?? e.Message;
                err = (err == null ? "reel: activate: " + aErr : err + "; activate: " + aErr);
            }

            // Wait for the storage-info GET + gallery thumbnail listing GET to resolve and the empty/populated state to render.
            for (int i = 0; i < 45; i++) yield return null;

            if (lastPanelKey != null) VerifyShown(mvcManager, lastPanelKey, out _);  // best-effort render note (non-gating)
            yield return CaptureShot("reel");

            // Always "shown": the empty-gallery state is a valid capture for an account with no photos. Append any non-fatal note.
            m.error = err == null ? "shown" : ("shown; " + err);
        }

        // M05 photo — force-show the shared full-screen photo-detail VIEWER (PhotoDetailController, the
        // camera-reel / passport / community-card lightbox) instead of the empty Gallery grid. The signed-in
        // test account's reel gallery is empty, so opening the ExplorePanel->CameraReel section only renders the
        // "There are no photos yet" grid. Here we bypass the gallery and directly show PhotoDetailController with
        // a SYNTHETIC CameraReelResponseCompact so the lightbox chrome (image area, info side panel, nav arrows)
        // renders regardless of account data. This IS the photo-detail LIGHTBOX (mainImage + mainImageLoadingSpinner
        // + setAsPublicToggle + share/copy/download chrome + side info panel), NOT the gallery grid.
        // PhotoDetailController : ControllerBase<PhotoDetailView, PhotoDetailParameter>; OnViewShow -> ShowReelAsync
        // does: spinner ON, then `cameraReelScreenshotsStorage.GetScreenshotImageAsync(reel.url,false,ct)` (a plain
        // UnityWebRequestTexture GET on reel.url), then sets mainImage.texture + fades in + spinner OFF.
        // FIX (was a perpetual spinner): the old PLACEHOLDER_URL (decentraland.org/images/ui/dark-atlas-v3.png) did
        // not decode into a texture, so the spinner never cleared. Point reel.url at a REAL, resolvable
        // decentraland CONTENT-SERVER image hash (a Genesis-Plaza scene-thumbnail.png, verified HTTP 200 + valid
        // 1140x800 PNG) so an actual picture renders in the lightbox. READ-ONLY / NO-NOISE: GetScreenshotImageAsync
        // is a single public GET; with OpenedFromPublicBoard=true the delete + set-as-public controls are hidden, so
        // no destructive UI surfaces and no buttons are ever clicked.
        private static IEnumerator AtlasCapture_photo(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_photo", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            // A real, public, read-only image on the Decentraland content server (Genesis Plaza scene thumbnail,
            // content hash -> /content/contents/<hash>). Verified to return a valid PNG that decodes into a texture,
            // so the lightbox's mainImage renders an actual picture instead of an endless spinner. No world state is
            // touched: this is a single GET on a CDN-cached, immutable content hash.
            const string PLACEHOLDER_URL = "https://peer.decentraland.org/content/contents/bafybeietrfx6arffgapt65jkawued7mcsu75uuloodf3drxbvq2pfpggei";

            string err = null;
            Type controllerT = null;
            Type paramT = null;
            Type callerCtxT = null;
            Type compactT = null;
            Type galleryEventBusT = null;
            object photoParam = null;

            // ---- Build the synthetic reel + PhotoDetailParameter (all reflection, no yields) ----
            try
            {
                controllerT      = FindType("DCL.InWorldCamera.PhotoDetail.PhotoDetailController");
                paramT           = FindType("DCL.InWorldCamera.PhotoDetail.PhotoDetailParameter");
                callerCtxT       = FindType("DCL.InWorldCamera.PhotoDetail.PhotoDetailParameter+CallerContext");
                compactT         = FindType("DCL.InWorldCamera.CameraReelStorageService.Schemas.CameraReelResponseCompact");
                galleryEventBusT = FindType("DCL.InWorldCamera.GalleryEventBus");

                if (controllerT == null || paramT == null || callerCtxT == null || compactT == null)
                    err = "photo: PhotoDetail types not found (skipped:no-types)";
                else
                {
                    // CameraReelResponseCompact { string id; string url; string thumbnailUrl; bool isPublic; string dateTime; }
                    object reel = System.Activator.CreateInstance(compactT);
                    compactT.GetField("id").SetValue(reel, "atlas-placeholder-0001");
                    compactT.GetField("url").SetValue(reel, PLACEHOLDER_URL);           // the lightbox downloads THIS into mainImage
                    compactT.GetField("thumbnailUrl").SetValue(reel, PLACEHOLDER_URL);
                    compactT.GetField("isPublic").SetValue(reel, true);
                    // dateTime is parsed downstream for bucketing; a valid ISO string keeps any parse happy.
                    compactT.GetField("dateTime").SetValue(reel, "2026-01-01T00:00:00.000Z");

                    Type listT = typeof(System.Collections.Generic.List<>).MakeGenericType(compactT);
                    object allReels = System.Activator.CreateInstance(listT);
                    listT.GetMethod("Add").Invoke(allReels, new[] { reel });

                    // A fresh GalleryEventBus (the viewer only subscribes/raises on it; nothing leaves the client).
                    object galleryEventBus = galleryEventBusT != null ? System.Activator.CreateInstance(galleryEventBusT) : null;

                    // PhotoDetailParameter(List<CameraReelResponseCompact> allReels, int currentReelIndex,
                    //   bool openedFromPublicBoard, CallerContext openedFrom,
                    //   Action<CameraReelResponseCompact> reelDeleteAction,
                    //   Action<CameraReelResponseCompact> hideReelFromListIntention, GalleryEventBus galleryEventBus)
                    object cameraReelCtx = System.Enum.Parse(callerCtxT, "CameraReel");
                    ConstructorInfo ctor = paramT.GetConstructor(new[]
                    {
                        listT, typeof(int), typeof(bool), callerCtxT,
                        typeof(System.Action<>).MakeGenericType(compactT),
                        typeof(System.Action<>).MakeGenericType(compactT),
                        galleryEventBusT
                    });
                    if (ctor == null) err = "photo: PhotoDetailParameter ctor not found (skipped:no-ctor)";
                    else
                        // openedFromPublicBoard=true -> delete + set-public controls hidden (no destructive UI).
                        photoParam = ctor.Invoke(new object[] { allReels, 0, true, cameraReelCtx, null, null, galleryEventBus });
                }
            }
            catch (System.Exception e) { err = "photo: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // ---- Show the full-screen viewer (TryShowPanel = IssueCommand -> ShowAsync, fire-and-forget) ----
            if (!TryShowPanel(mvcManager, controllerT, photoParam, out string showErr))
            { m.error = "photo: TryShowPanel failed: " + showErr + " (skipped:show-error)"; yield break; }

            // Generous settle OUTSIDE any try: view instantiate/show + the GetScreenshotImageAsync image
            // download (UnityWebRequestTexture GET on a CDN content hash) + texture upload + DOFade fade-in.
            // ~300 frames gives the network fetch plenty of time so the spinner clears and the picture renders.
            for (int i = 0; i < 300; i++) yield return null;
            VerifyShown(mvcManager, lastPanelKey, out _);   // best-effort render note (non-gating)
            yield return CaptureShot("photo");
            m.error = "shown";
        }

// Opens the GiftSelection panel (recipient header + the user's own wearable/emote grid to choose a gift) and screenshots it once the grid has actually loaded. NO-NOISE: only opens/populates the picker; the actual web3 gift transfer is gated behind the footer "Send" button which we never click. LOADWAIT: the previous capture fired after a fixed 18-frame settle and caught the spinner over an empty grid. The view shows GiftingView.ProgressContainer while the active grid presenter has CurrentItemCount==0; we now poll the live GiftSelectionController (tabsManager.ActivePresenter.CurrentItemCount > 0 / ProgressContainer inactive) for up to ~6s so REAL wearables render before the shot.
private static IEnumerator AtlasCapture_gifting(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
{
    var m = new PhaseMarker { label = "atlas_gifting", ok = true };
    report.actions.Add(m);

    string err = null;
    object giftParams = null;
    try
    {
        // Build GiftSelectionParams(userAddress, userName) with a placeholder recipient.
        // The picker shows OUR OWN inventory grid regardless of recipient; no profile/transfer
        // side-effect is visible to other users. (Real friend lookup is intentionally skipped:
        // it adds an awaited service call for zero visual gain and risks gating the capture.)
        Type paramType = FindType("DCL.Backpack.Gifting.Views.GiftSelectionParams");
        if (paramType == null) { err = "gifting: GiftSelectionParams type not found"; }
        else
        {
            ConstructorInfo paramCtor = paramType.GetConstructor(new[] { typeof(string), typeof(string) });
            if (paramCtor == null) { err = "gifting: GiftSelectionParams(string,string) ctor not found"; }
            else
                giftParams = paramCtor.Invoke(new object[]
                {
                    "0x0000000000000000000000000000000000000000",
                    "Recipient",
                });
        }
    }
    catch (System.Exception e) { err = "gifting: " + (e.InnerException?.Message ?? e.Message); }
    if (err != null) { m.error = err; yield break; }

    // Open via the shared IssueCommand -> ShowAsync helper (sets lastPanelKey). Its own try/catch.
    if (!TryShowPanelByName(mvcManager, "DCL.Backpack.Gifting.Presenters.GiftSelectionController", giftParams, out string showErr))
    { m.error = "gifting: " + showErr; yield break; }

    for (int i = 0; i < 18; i++) yield return null;             // open anim + let the controller register

    // CONTENT-LOAD POLL (up to ~6s): the grid loads asynchronously (equippedStatusProvider.InitializeAsync
    // -> grid presenter populates). While it is loading the controller turns on GiftingView.ProgressContainer
    // (the spinner). We consider the grid ready when the active presenter reports CurrentItemCount > 0, or
    // (fallback for the genuinely empty-inventory case) when the spinner has been switched OFF. Each read is a
    // tiny try with NO yield; the 'yield return null' pacing lives outside any try.
    bool loaded = false;
    for (int i = 0; i < 360 && !loaded; i++)
    {
        try
        {
            object ctl = FindControllerByTypeName(mvcManager, "GiftSelectionController");
            if (ctl != null)
            {
                // tabsManager.ActivePresenter.CurrentItemCount  (private field -> public prop -> public prop)
                object tabsManager = GetPrivateField(ctl, "tabsManager");
                object active = tabsManager != null ? GetMember(tabsManager, "ActivePresenter") : null;
                object countObj = active != null ? GetMember(active, "CurrentItemCount") : null;
                if (countObj is int cnt && cnt > 0) loaded = true;

                // Fallback: spinner has been hidden again (empty inventory finished loading).
                if (!loaded)
                {
                    object viewInstance = GetMember(ctl, "viewInstance");
                    object progress = viewInstance != null ? GetMember(viewInstance, "ProgressContainer") : null; // GameObject
                    object spinnerActive = progress != null ? GetMember(progress, "activeInHierarchy") : null;
                    // Only trust the OFF state after the spinner has had a chance to come up (>~30 frames in).
                    if (i > 30 && spinnerActive is bool sa && sa == false) loaded = true;
                }
            }
        }
        catch { /* controller/view not ready yet; keep polling */ }

        if (!loaded) yield return null;
    }

    for (int i = 0; i < 12; i++) yield return null;             // brief settle so grid cells finish layout
    yield return CaptureShot("gifting");                         // always capture, even if grid stayed empty
    m.error = "shown";
}

        // Force-show the 'Credits Unlocked' weekly-goal-claimed celebration MODAL
        // (CreditsUnlockedController, a POPUP-layer reward panel) with SYNTHETIC display data
        // (claimed credits = 250) so it renders without any real claim state.
        //
        // NO NOISE: this only opens the LOCAL, already-registered modal view via the standard
        // inherited-static IssueCommand(Params) -> mvcManager.ShowAsync path. OnBeforeViewShow just
        // formats the synthetic number into a label; nothing is claimed, written, or networked.
        //
        // WHY THE OLD CAPTURE WAS BLANK: the controller blocks in WaitForCloseIntentAsync, so we
        // fire-and-forget ShowAsync (TryShowPanelByName never awaits it). The previous driver then
        // captured after only ~18 frames (~0.3s) -- but the show animation is a 0.4s DOFade(alpha 0->1)
        // FOLLOWED BY a 0.5s DOScale(0->1) of the panel content (RewardBackgroundRaysAnimation),
        // and the view starts at alpha 0. At 18 frames the panel is still mid-fade with its content
        // scaled near zero, so the capture showed only the bare HUD. We now settle ~75 frames
        // (~1.25s) so the full ShowAnimationAsync (fade + scale, ~0.9s) completes before CaptureShot.
        private static IEnumerator AtlasCapture_creditsunlocked(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_creditsunlocked", ok = true };  // non-gating: ok always true
            report.actions.Add(m);

            string err = null;
            object param = null;
            try
            {
                // CreditsUnlockedController.Params is a readonly struct with a single (float claimedCredits) ctor.
                Type paramsType = FindType("DCL.MarketplaceCredits.CreditsUnlockedController+Params");
                if (paramsType == null) { err = "creditsunlocked: Params type not found"; }
                else
                {
                    ConstructorInfo paramsCtor = paramsType.GetConstructor(new[] { typeof(float) });
                    if (paramsCtor == null) { err = "creditsunlocked: Params(float) constructor not found"; }
                    else param = paramsCtor.Invoke(new object[] { 250f });  // synthetic claimed-credits amount
                }
            }
            catch (System.Exception e) { err = "creditsunlocked: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Pre-close + poll until the target controller is ViewHidden so the ShowAsync below can't be swallowed
            // by a prior view still mid-hide (the M07 ViewHidden race). yield is OUTSIDE any try.
            yield return PreShowSettle(mvcManager, "CreditsUnlockedController");

            // FIRE-AND-FORGET: TryShowPanelByName drives the inherited static IssueCommand(param) -> ShowAsync
            // dance internally and never awaits ShowAsync (the controller would otherwise block in
            // WaitForCloseIntentAsync until CloseButton or a 5000ms delay). It also sets lastPanelKey.
            if (!TryShowPanelByName(mvcManager, "DCL.MarketplaceCredits.CreditsUnlockedController", param, out err))
            { m.error = "creditsunlocked: " + err; yield break; }

            // Settle long enough for the full show animation to play: RewardBackgroundRaysAnimation
            // does DOFade(alpha -> 1, 0.4s) then DOScale(content -> 1, 0.5s) ~= 0.9s. ~75 frames (~1.25s)
            // gives a comfortable margin, well inside the 5000ms auto-close window.
            for (int i = 0; i < 75; i++) yield return null;
            yield return CaptureShot("creditsunlocked");

            // Verify the view actually rendered (State != ViewHidden/ViewHiding). Non-gating note only.
            if (!VerifyShown(mvcManager, lastPanelKey, out string rerr)) m.error = "not-shown: " + rerr;
            else m.error = "shown";
        }

        // Open the Marketplace Credits menu panel and force its "Goals of the Week" sub-state,
        // populated with a SYNTHETIC CreditsProgramProgressResponse so the distinct goal states are
        // shown deterministically regardless of the live account's real program status:
        //   - an in-progress goal (completedSteps < totalSteps)  -> progress bar / "locked" look
        //   - a claimed goal       (isClaimed = true)            -> claimed badge
        // SomethingToClaim() is kept FALSE on the synthetic data (no completed-but-unclaimed goal),
        // so Setup() does NOT call ReloadCaptcha() and no captcha is generated. The whole flow is
        // read-only UI population: ShowAsync opens the panel, OpenSection switches the local section,
        // Setup() fills row views from in-memory structs. No claim, no purchase, no email, no email
        // verification, nothing visible to other users -> NO NOISE.
        // (Note: showing the panel normally also fires the welcome section's GetProgramProgressAsync,
        //  a read-only GET of the user's own progress; we then override the rendered section.)
        private static IEnumerator AtlasCapture_creditsstates(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_creditsstates", ok = true };  // non-gating
            report.actions.Add(m);

            // --- show the Marketplace Credits menu panel (IssueCommand(Params)->ShowAsync) ---
            string err = null;
            object param = null;
            try
            {
                // MarketplaceCreditsMenuController.Params is a readonly struct: Params(bool isOpenedFromNotification)
                Type paramsType = FindType("DCL.MarketplaceCredits.MarketplaceCreditsMenuController+Params");
                if (paramsType == null) err = "creditsstates: Params type not found";
                else
                {
                    ConstructorInfo paramsCtor = paramsType.GetConstructor(new[] { typeof(bool) });
                    if (paramsCtor == null) err = "creditsstates: Params(bool) constructor not found";
                    else param = paramsCtor.Invoke(new object[] { false });
                }

                if (err == null && !TryShowPanelByName(mvcManager, "DCL.MarketplaceCredits.MarketplaceCreditsMenuController", param, out string showErr))
                    err = "creditsstates: open panel failed: " + showErr;
            }
            catch (System.Exception e) { err = "creditsstates: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // --- let the panel instantiate + the welcome async load settle ---
            for (int i = 0; i < 24; i++) yield return null;

            // --- force the Goals-of-the-Week section + populate it with synthetic goal data ---
            // Non-fatal: if any reflection step fails we still capture whatever the panel rendered.
            try
            {
                object ctl = FindControllerByTypeName(mvcManager, "MarketplaceCreditsMenuController");
                Type sectionEnum = FindType("DCL.MarketplaceCredits.MarketplaceCreditsSection");
                Type respType = FindType("DCL.MarketplaceCredits.CreditsProgramProgressResponse");
                Type goalType = FindType("DCL.MarketplaceCredits.GoalData");
                Type goalProgType = FindType("DCL.MarketplaceCredits.GoalProgressData");
                Type weekType = FindType("DCL.MarketplaceCredits.Week");
                Type creditsType = FindType("DCL.MarketplaceCredits.CreditsData");
                Type userType = FindType("DCL.MarketplaceCredits.UserData");

                if (ctl != null && sectionEnum != null && respType != null && goalType != null
                    && goalProgType != null && weekType != null && creditsType != null && userType != null)
                {
                    // Build two synthetic goals: one in-progress, one already claimed.
                    object MakeGoal(string title, string desc, uint completed, uint total, float reward, bool claimed)
                    {
                        object prog = System.Activator.CreateInstance(goalProgType);
                        goalProgType.GetField("totalSteps").SetValue(prog, total);
                        goalProgType.GetField("completedSteps").SetValue(prog, completed);

                        object goal = System.Activator.CreateInstance(goalType);
                        goalType.GetField("title").SetValue(goal, title);
                        goalType.GetField("description").SetValue(goal, desc);
                        goalType.GetField("thumbnail").SetValue(goal, "");      // empty -> no remote image fetch
                        goalType.GetField("progress").SetValue(goal, prog);
                        goalType.GetField("reward").SetValue(goal, reward);
                        goalType.GetField("isClaimed").SetValue(goal, claimed);
                        return goal;
                    }

                    Type listType = typeof(System.Collections.Generic.List<>).MakeGenericType(goalType);
                    object goals = System.Activator.CreateInstance(listType);
                    MethodInfo add = listType.GetMethod("Add");
                    add.Invoke(goals, new[] { MakeGoal("Walk around Genesis City", "Explore the world", 2u, 5u, 100f, false) });
                    add.Invoke(goals, new[] { MakeGoal("Customize your avatar", "Change your look", 1u, 1u, 50f, true) });

                    // currentWeek with a time-left value (timeLeft is uint on Week)
                    object week = System.Activator.CreateInstance(weekType);
                    weekType.GetField("weekNumber").SetValue(week, 1);
                    weekType.GetField("timeLeft").SetValue(week, (uint)259200); // ~3 days
                    weekType.GetField("startDate").SetValue(week, "");
                    weekType.GetField("endDate").SetValue(week, "");
                    weekType.GetField("secondsRemaining").SetValue(week, (uint)259200);

                    object credits = System.Activator.CreateInstance(creditsType);
                    creditsType.GetField("available").SetValue(credits, 150f);
                    creditsType.GetField("expiresIn").SetValue(credits, (uint)30);
                    creditsType.GetField("isBlockedForClaiming").SetValue(credits, false);

                    object user = System.Activator.CreateInstance(userType);
                    userType.GetField("email").SetValue(user, "user@example.com");
                    userType.GetField("isEmailConfirmed").SetValue(user, true);
                    userType.GetField("hasStartedProgram").SetValue(user, true);

                    object resp = System.Activator.CreateInstance(respType);
                    respType.GetField("currentWeek").SetValue(resp, week);
                    respType.GetField("credits").SetValue(resp, credits);
                    respType.GetField("user").SetValue(resp, user);
                    respType.GetField("goals").SetValue(resp, goals);

                    // Switch the rendered section locally, then populate the goal rows.
                    object goalsSection = System.Enum.Parse(sectionEnum, "GOALS_OF_THE_WEEK");
                    MethodInfo openSection = ctl.GetType().GetMethod("OpenSection",
                        BindingFlags.Public | BindingFlags.Instance, null, new[] { sectionEnum }, null);
                    if (openSection != null)
                        openSection.Invoke(ctl, new[] { goalsSection });

                    // private field on the controller: the goals-of-the-week sub-controller instance
                    object goalsSub = GetPrivateField(ctl, "marketplaceCreditsGoalsOfTheWeekSubController");
                    if (goalsSub != null)
                        TryInvoke(goalsSub, "Setup", new[] { resp }, out string _);
                }
            }
            catch (System.Exception) { /* non-fatal: capture whatever rendered */ }

            // --- let the section swap + goal rows lay out / animate in ---
            for (int i = 0; i < 24; i++) yield return null;

            HideChat(mvcManager);   // hide the persistent chat HUD bleeding into this isolated shot
            yield return CaptureShot("creditsstates");

            // Verify the panel rendered (non-gating note only).
            if (!VerifyShown(mvcManager, lastPanelKey, out string rerr)) m.error = "not-shown: " + rerr;
            else m.error = "shown";
        }

        // Opens the local chat panel, then reveals the in-world emoji picker (DCL.Emoji.EmojiPanelView,
        // a scene MonoBehaviour) by calling SetVisible(true) directly. Purely local UI: no chat/message
        // is sent, so it is noise-free. The emoji row content is populated by EmojiPanelPresenter, whose
        // instance is a plain (non-MVC, non-MonoBehaviour) object held inside the chat-input FSM and is
        // not cleanly reachable by reflection; this driver captures whatever the view itself renders.
        private static IEnumerator AtlasCapture_emoji(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_emoji", ok = true };
            report.actions.Add(m);

            // wrong-grid isolation: drop the leftover persistent Communities/Explore grid so the EmojiPanelView
            // overlay (chat input emoji picker, under the full-screen ExplorePanel) is the surface that renders.
            yield return HideExplorePanel(mvcManager);

            // STEP 1: Show AND focus the chat. Merely showing the persistent controller leaves the chat-input row
            // collapsed/unfocused, so the emoji panel (parented under the chat input) renders over nothing
            // meaningful. FocusedChatState expands the input row (ChatUIMediator.SetupForFocusedState ->
            // chatInputPresenter.ShowFocusedAsync, ChatUIMediator.cs:70) so the emoji picker reveals in place.
            yield return ShowAndFocusChat(mvcManager);

            // STEP 2: Locate the EmojiPanelView MonoBehaviour in the scene and reveal it.
            string err = null;
            object emojiView = null;
            try
            {
                Type viewT = FindType("DCL.Emoji.EmojiPanelView");
                if (viewT == null)
                {
                    err = "emoji: type DCL.Emoji.EmojiPanelView not found";
                }
                else
                {
                    foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsByType(viewT, FindObjectsInactive.Include))
                    {
                        emojiView = o;
                        break;
                    }

                    if (emojiView == null)
                    {
                        err = "emoji: no EmojiPanelView instance in scene";
                    }
                    else
                    {
                        // EmojiPanelView.SetVisible(bool) internally calls EnsureInitialized() and
                        // toggles its CanvasGroup. This is a local visual toggle only (no noise).
                        MethodInfo setVisible = emojiView.GetType().GetMethod("SetVisible",
                            BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);

                        if (setVisible == null)
                            err = "emoji: SetVisible(bool) not found on EmojiPanelView";
                        else
                            setVisible.Invoke(emojiView, new object[] { true });
                    }
                }
            }
            catch (System.Exception e) { err = "emoji: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Settle emoji panel reveal animation / layout.
            for (int i = 0; i < 18; i++) yield return null;

            // STEP 3: Best-effort visibility verification (non-gating).
            string visNote = null;
            try
            {
                object isVisible = GetPublicProperty(emojiView, "IsVisible");
                if (isVisible is bool visible && !visible)
                    visNote = "shown but EmojiPanelView.IsVisible == false";
            }
            catch (System.Exception e) { visNote = "verify: " + (e.InnerException?.Message ?? e.Message); }

            yield return CaptureShot("emoji");
            m.error = visNote ?? "shown";
        }

        // Opens the teleport CONFIRMATION dialog for a target parcel (Genesis Plaza) and screenshots it.
        // NO-NOISE: showing the prompt only opens the popup + triggers a read-only IPlacesAPIService.GetPlaceAsync
        // fetch to populate the place card. The actual teleport (chat /goto) fires only in Approve() when the
        // user clicks Continue - we never invoke that, so nothing visible to other users happens.
        private static IEnumerator AtlasCapture_teleportprompt(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_teleportprompt", ok = true };  // NON-GATING
            report.actions.Add(m);

            string err = null;
            bool opened = false;
            try
            {
                if (mvcManager == null) { err = "teleportprompt: mvcManager is null"; }
                else
                {
                    // Build TeleportPromptController.Params(Vector2Int coords). Params is a nested struct
                    // whose single ctor takes the target parcel; pass a populated Genesis Plaza parcel.
                    Type paramT = FindType("DCL.TeleportPrompt.TeleportPromptController+Params");
                    if (paramT == null) { err = "teleportprompt: Params type not found"; }
                    else
                    {
                        object param = Activator.CreateInstance(paramT, new Vector2Int(74, -9));
                        // Reuse the shared IssueCommand->ShowAsync helper (1-arg path with our Params).
                        opened = TryShowPanelByName(mvcManager, "DCL.TeleportPrompt.TeleportPromptController", param, out err);
                    }
                }
            }
            catch (System.Exception e) { err = "teleportprompt: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = "not-shown: " + err; yield break; }

            // Settle: GetPlaceInfoAsync delays 300ms then fetches the place card; give it time to populate.
            for (int i = 0; i < 36; i++) yield return null;
            yield return CaptureShot("teleportprompt");

            string rerr = null;
            try { if (!VerifyShown(mvcManager, lastPanelKey, out rerr)) rerr = "not-shown: " + rerr; else rerr = null; }
            catch (System.Exception e) { rerr = "verify-failed: " + (e.InnerException?.Message ?? e.Message); }
            m.error = rerr ?? "shown";
        }

        // Opens the NFT info prompt (NftPromptController) for a known test NFT and captures the FULLY-LOADED view.
        // FIX (loadwait): the old 18-frame settle captured a blank white modal + spinner because the NFT info is
        // a LIVE web fetch (OpenSeaAPIClient -> /api/v2/chain/ethereum/contract/.../nfts/...) followed by an image
        // download. We now POLL the view: NftPromptController.SetNftInfo() activates viewInstance.NftContent when the
        // data resolves (or MainErrorFeedbackContent on failure). We wait for either, then add image-render frames.
        // NFT: CryptoKitties #1540722 (contract 0x06012c8...266d) — a well-known, reliably-resolving ethereum NFT.
        // NON-NOISE: ShowAsync only displays the panel; OnViewShow just fetches NFT info + unlocks cursor.
        // The browser only opens on a user button click (ViewOnMarket), which the harness never triggers.
        private static IEnumerator AtlasCapture_nftprompt(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_nftprompt", ok = true };
            report.actions.Add(m);

            string err = null;
            Type panelKey = null;
            try
            {
                // Params is a nested struct: NftPromptController.Params(chain, contractAddress, tokenId).
                Type paramT = FindType("DCL.NftPrompt.NftPromptController+Params");
                if (paramT == null) { err = "nftprompt: Params type not found"; }

                Type controllerT = err == null ? FindType("DCL.NftPrompt.NftPromptController") : null;
                if (err == null && controllerT == null) { err = "nftprompt: NftPromptController type not found"; }

                if (err == null)
                {
                    object param = Activator.CreateInstance(paramT,
                        "ethereum",                                        // chain
                        "0x06012c8cf97bead5deae237070f9587f8e7a266d",     // contractAddress (CryptoKitties)
                        "1540722");                                        // tokenId

                    // IssueCommand is inherited static from ControllerBase; there are two overloads
                    // (0-arg / 1-arg) so we must pick the 1-arg one by hand (GetMethod would be ambiguous).
                    MethodInfo issue = null;
                    foreach (MethodInfo mi in controllerT.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                        if (mi.Name == "IssueCommand" && mi.GetParameters().Length == 1) { issue = mi; break; }
                    if (issue == null) { err = "nftprompt: IssueCommand(1-arg) not found"; }

                    object command = issue != null ? issue.Invoke(null, new[] { param }) : null;
                    if (err == null && command == null) { err = "nftprompt: IssueCommand returned null"; }

                    if (err == null)
                    {
                        // mvcManager.ShowAsync<TView,TInput>(command, CancellationToken.None) — generic.
                        MethodInfo showAsync = null;
                        foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                            if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                        if (showAsync == null) { err = "nftprompt: ShowAsync not found"; }

                        if (err == null)
                        {
                            Type[] genArgs = command.GetType().GetGenericArguments();
                            // Fire-and-forget; the async flow continues on the player loop.
                            showAsync.MakeGenericMethod(genArgs)
                                     .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });

                            Type ifaceOpen = FindType("MVC.IController`2");
                            panelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(genArgs) : null;
                        }
                    }
                }
            }
            catch (Exception e) { err = "nftprompt: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Brief settle so the controller is registered + view instantiated before we start polling.
            for (int i = 0; i < 30; i++) yield return null;

            // Content-load POLL (up to ~300 frames): wait for the live OpenSea fetch to resolve.
            // SetNftInfo() activates viewInstance.NftContent; failure path activates MainErrorFeedbackContent.
            // We read activeSelf each frame inside a tiny try (no yields); 'yield return null' stays OUTSIDE.
            bool loaded = false;
            bool errored = false;
            for (int i = 0; i < 300 && !loaded && !errored; i++)
            {
                try
                {
                    object ctl = FindControllerByTypeName(mvcManager, "NftPromptController");
                    object view = ctl != null ? GetMember(ctl, "viewInstance") : null;
                    if (view != null)
                    {
                        object nftContent = GetMember(view, "NftContent");
                        var contentGo = nftContent as UnityEngine.GameObject;
                        if (contentGo != null && contentGo.activeSelf) loaded = true;

                        object errFeedback = GetMember(view, "MainErrorFeedbackContent");
                        var errGo = errFeedback as UnityEngine.GameObject;
                        if (errGo != null && errGo.activeSelf) errored = true;
                    }
                }
                catch { /* keep polling */ }

                yield return null;
            }

            // Extra frames for the NFT image (placeImageController.RequestImage) to download + render
            // once the body is active. Generous on the load path; small on error path.
            int settleFrames = loaded ? 180 : 60;
            for (int i = 0; i < settleFrames; i++) yield return null;

            // Verify the view is actually shown (State != ViewHidden/ViewHiding). Non-gating.
            if (panelKey != null && !VerifyShown(mvcManager, panelKey, out string verifyErr))
                m.error = "nftprompt: " + verifyErr;

            yield return CaptureShot("nftprompt");

            if (m.error == null)
                m.error = loaded ? "shown" : (errored ? "shown: nft-fetch-failed (error feedback)" : "shown: nft-fetch-timeout");
        }

        // Force-shows the big Quest Reward OVERLAY (RewardPanelController) directly via the MVC
        // ShowAsync path with a SYNTHETIC RewardPanelParameter, instead of dispatching a
        // REWARD_IN_PROGRESS notification (which only updates the HUD and does NOT render the overlay).
        // NO-NOISE: this is a purely local display panel. OnBeforeViewShow just requests a thumbnail
        // image (read-only web request, same as nftprompt) and sets text/colors from the synthetic
        // param. No reward is claimed/created/activated; the panel blocks on the Continue button
        // (WaitForCloseIntentAsync), so we fire-and-forget the ShowAsync.
        private static IEnumerator AtlasCapture_reward(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_reward", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            string err = null;
            Type panelKey = null;
            try
            {
                // RewardPanelParameter is a readonly struct:
                //   RewardPanelParameter(string imageUrl, string wearableName, string rarity, string category)
                Type paramT = FindType("DCL.RewardPanel.RewardPanelParameter");
                if (paramT == null) { err = "reward: RewardPanelParameter type not found"; }

                Type controllerT = err == null ? FindType("DCL.RewardPanel.RewardPanelController") : null;
                if (err == null && controllerT == null) { err = "reward: RewardPanelController type not found"; }

                if (err == null)
                {
                    // Synthetic display data only (no real reward). Unknown rarity/category fall back
                    // to default sprites/colors safely; "epic" exists in the rarity mappings.
                    object param = Activator.CreateInstance(paramT,
                        "https://peer.decentraland.org/content/contents/bafybeihfypqzqr7l2v3fvhx25gw32qxc4rkmmulccm2ij5p7quhzqxypy", // imageUrl
                        "Test Reward Wearable",  // wearableName
                        "epic",                  // rarity
                        "eyewear");              // category

                    // IssueCommand is inherited static from ControllerBase; there are 0-arg and 1-arg
                    // overloads, so pick the 1-arg one by param count (GetMethod by name is ambiguous).
                    MethodInfo issue = null;
                    foreach (MethodInfo mi in controllerT.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                        if (mi.Name == "IssueCommand" && mi.GetParameters().Length == 1) { issue = mi; break; }
                    if (issue == null) { err = "reward: IssueCommand(1-arg) not found"; }

                    object command = issue != null ? issue.Invoke(null, new[] { param }) : null;
                    if (err == null && command == null) { err = "reward: IssueCommand returned null"; }

                    if (err == null)
                    {
                        // mvcManager.ShowAsync<TView,TInput>(command, CancellationToken.None) — generic.
                        MethodInfo showAsync = null;
                        foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                            if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                        if (showAsync == null) { err = "reward: ShowAsync not found"; }

                        if (err == null)
                        {
                            Type[] genArgs = command.GetType().GetGenericArguments();
                            // Fire-and-forget: the overlay blocks on the Continue-button close intent.
                            showAsync.MakeGenericMethod(genArgs)
                                     .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });

                            Type ifaceOpen = FindType("MVC.IController`2");
                            panelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(genArgs) : null;
                        }
                    }
                }
            }
            catch (Exception e) { err = "reward: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }   // yield break is OUTSIDE the try

            // Settle: allow the thumbnail fetch + RewardPanelView show animation.
            for (int i = 0; i < 90; i++) yield return null;   // long settle: RewardBackgroundRays animation (~0.9s) like creditsunlocked

            // Verify the view is actually shown (State != ViewHidden/ViewHiding). Non-gating.
            if (panelKey != null && !VerifyShown(mvcManager, panelKey, out string verifyErr))
                m.error = "reward: " + verifyErr;

            // stray-modal / HUD-bleed isolation before capture: deactivate any transient notification toast
            // (NewNotificationPanel, the stray corner modal) and the persistent chat HUD that bleed past this
            // overlay's un-dimmed edges. Same proven helpers used by other isolated shots (DclPlaytestHarness.cs
            // :573 / :605); synchronous voids, safe outside any try. (Minimap/sidebar HUD have no hide helper and
            // intentionally remain.)
            HideRewardsPopup();
            HideChat(mvcManager);

            yield return CaptureShot("reward");

            if (m.error == null)
                m.error = "shown";
        }

        // Directly SHOW the PrivateWorldPopupController in PasswordRequired mode via MVC (no realm change,
        // no network access attempt) so the password-gate UI renders, then screenshot. NOISE-FREE: this only
        // opens a local popup; it does NOT navigate the realm or attempt to enter any world.
        private static IEnumerator AtlasCapture_privateworlds(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_privateworlds", ok = true };
            report.actions.Add(m);

            string err = null;
            object popupParams = null;
            try
            {
                // Build PrivateWorldPopupParams(worldName, PrivateWorldPopupMode.PasswordRequired, ownerAddress)
                // exactly as PrivateWorldAccessHandler does. All synchronous reflection — no yields in here.
                Type paramsT = FindType("DCL.PrivateWorlds.UI.PrivateWorldPopupParams");
                Type modeT = FindType("DCL.PrivateWorlds.UI.PrivateWorldPopupMode");
                if (paramsT == null) err = "privateworlds: PrivateWorldPopupParams type not found";
                else if (modeT == null) err = "privateworlds: PrivateWorldPopupMode enum not found";
                else
                {
                    object passwordRequired = System.Enum.Parse(modeT, "PasswordRequired");
                    ConstructorInfo ctor = paramsT.GetConstructor(new[] { typeof(string), modeT, typeof(string) });
                    if (ctor != null)
                        popupParams = ctor.Invoke(new object[] { "private-world", passwordRequired, string.Empty });
                    else
                        popupParams = System.Activator.CreateInstance(paramsT); // graceful fallback to empty params
                }
            }
            catch (System.Exception e) { err = "privateworlds: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Show the popup via the shared helper (IssueCommand + MVCManager.ShowAsync). Local UI only.
            string showErr;
            bool opened = TryShowPanelByName(mvcManager, "DCL.PrivateWorlds.UI.PrivateWorldPopupController", popupParams, out showErr);
            if (!opened)
            {
                m.error = "privateworlds: show-failed (" + showErr + ")";
                yield break;
            }

            for (int i = 0; i < 18; i++) yield return null; // settle

            // Confirm the popup actually rendered (non-gating; capture regardless).
            string verifyErr = null;
            if (lastPanelKey != null && !VerifyShown(mvcManager, lastPanelKey, out verifyErr))
            {
                m.error = "privateworlds: not-shown (" + verifyErr + ")";
                yield return CaptureShot("privateworlds");
                yield break;
            }

            yield return CaptureShot("privateworlds");
            m.error = "shown";
        }

        // Smart-wearable PEX activation dialog (Runtime.Wearables.SmartWearableAuthorizationPopupController).
        // Force-shows the client-local "authorize this smart wearable" popup. NO-NOISE: opening it is a
        // local confirmation modal (no chat/social/economic/destructive action); we never click Authorize/Deny,
        // so CompletionSource is never set and nothing is granted.
        // DATA-GATED in prod: OnViewShow dereferences a REAL resolved smart IWearable -- wearable.GetCategory()
        // is Wearable.Model.Asset!.metadata.data.category, and GetRarity()/GetName() read DTO.Metadata.* . An
        // empty-parcel session has no resolved smart wearable, so a bare IWearable.NewEmpty() NPEs in OnViewShow.
        // We synthesize a minimally-valid Wearable by building a WearableDTO (metadata: name/rarity/category/
        // thumbnail/id, empty representations), wrapping it in StreamableLoadingResult<WearableDTO>, and resolving
        // it through the Wearable(result) ctor so GetCategory/GetRarity/GetName/thumbnail all return clean values.
        // The scene-permission list is fetched via SmartWearableCache.GetCachedSceneInfoAsync(...).Forget() (async,
        // off the harness path); it may resolve to no permissions, but the dialog body + wearable card still render.
        private static IEnumerator AtlasCapture_smartwearables(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_smartwearables", ok = true };  // NON-GATING
            report.actions.Add(m);

            string err = null;
            object param = null;
            string controllerFull = "Runtime.Wearables.SmartWearableAuthorizationPopupController";
            try
            {
                Type wearableConcreteT = FindType("DCL.AvatarRendering.Wearables.Components.Wearable");
                Type wearableIfaceT    = FindType("DCL.AvatarRendering.Wearables.Components.IWearable");
                Type dtoT              = FindType("DCL.AvatarRendering.Wearables.Helpers.WearableDTO");
                Type metaT             = FindType("DCL.AvatarRendering.Wearables.Helpers.WearableDTO+WearableMetadataDto");
                Type slrOpenT          = FindType("ECS.StreamableLoading.Common.Components.StreamableLoadingResult`1");
                Type reprT             = FindType("DCL.AvatarRendering.Loading.DTO.AvatarAttachmentDTO+Representation");
                Type paramT            = FindType(controllerFull + "+Params");
                Type csOpenT           = FindType("Cysharp.Threading.Tasks.UniTaskCompletionSource`1");

                if (wearableConcreteT == null) err = "smartwearables: Wearable concrete type not found";
                else if (wearableIfaceT == null) err = "smartwearables: IWearable type not found";
                else if (dtoT == null) err = "smartwearables: WearableDTO type not found";
                else if (metaT == null) err = "smartwearables: WearableMetadataDto type not found";
                else if (slrOpenT == null) err = "smartwearables: StreamableLoadingResult`1 not found";
                else if (reprT == null) err = "smartwearables: Representation type not found";
                else if (paramT == null) err = "smartwearables: Params type not found";
                else if (csOpenT == null) err = "smartwearables: UniTaskCompletionSource`1 not found";
                else
                {
                    // --- Build a minimally-valid WearableDTO --------------------------------------
                    // metadata (WearableMetadataDto): TrimmedMetadataBase has id/rarity/name; MetadataBase
                    // adds name/i18n/thumbnail/description; nested .data (DataDto) auto-initialized in ctor.
                    object metadata = Activator.CreateInstance(metaT);
                    metaT.GetField("id").SetValue(metadata, "urn:decentraland:matic:collections-v2:smart:atlas-preview");
                    metaT.GetField("rarity").SetValue(metadata, "epic");
                    // MetadataBase.name shadows TrimmedMetadataBase.name; GetName() reads MetadataBase.name.
                    foreach (System.Reflection.FieldInfo fi in metaT.GetFields(BindingFlags.Public | BindingFlags.Instance))
                        if (fi.Name == "name" && fi.DeclaringType.Name == "MetadataBase") fi.SetValue(metadata, "Smart Wearable");
                    metaT.GetField("name").SetValue(metadata, "Smart Wearable");        // also set the shadowed one
                    metaT.GetField("thumbnail").SetValue(metadata, "preview-thumbnail"); // != "thumbnail.png" -> no content lookup
                    metaT.GetField("description").SetValue(metadata, "Atlas preview smart wearable");

                    // metadata.data (DataDto : DataBase : TrimmedDataBase): category + representations.
                    object data = metaT.GetField("data").GetValue(metadata);
                    Type dataT = data.GetType();
                    foreach (System.Reflection.FieldInfo fi in dataT.GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (fi.Name == "category") fi.SetValue(data, "upper_body");
                        else if (fi.Name == "representations") fi.SetValue(data, System.Array.CreateInstance(reprT, 0));
                    }

                    // DTO root (WearableDTO -> AvatarAttachmentDTO<T> -> EntityDefinitionBase -> TrimmedEntityDefinitionBase):
                    // metadata/id/thumbnail can be declared on the runtime type OR a base; walk the base chain.
                    object dto = Activator.CreateInstance(dtoT);
                    for (Type t = dtoT; t != null; t = t.BaseType)
                    {
                        System.Reflection.FieldInfo fMeta = t.GetField("metadata",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        if (fMeta != null) { fMeta.SetValue(dto, metadata); break; }
                    }
                    for (Type t = dtoT; t != null; t = t.BaseType)
                    {
                        System.Reflection.FieldInfo fId = t.GetField("id",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        if (fId != null) { fId.SetValue(dto, "urn:decentraland:matic:collections-v2:smart:atlas-preview"); break; }
                    }
                    for (Type t = dtoT; t != null; t = t.BaseType)
                    {
                        System.Reflection.FieldInfo fThumb = t.GetField("thumbnail",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                        if (fThumb != null) { fThumb.SetValue(dto, "preview-thumbnail"); break; }
                    }

                    // StreamableLoadingResult<WearableDTO>(dto) -- 1-arg success ctor.
                    Type slrT = slrOpenT.MakeGenericType(dtoT);
                    object resolvedResult = Activator.CreateInstance(slrT, new object[] { dto });

                    // Wearable(StreamableLoadingResult<WearableDTO>) ctor resolves the DTO (sets Model + TrimmedModel
                    // + Type), so GetCategory()/GetRarity()/GetName() all return our synthetic values.
                    ConstructorInfo wctor = wearableConcreteT.GetConstructor(
                        BindingFlags.Public | BindingFlags.Instance, null, new[] { slrT }, null);
                    object wearable = wctor != null
                        ? wctor.Invoke(new object[] { resolvedResult })
                        : null;
                    if (wearable == null) err = "smartwearables: Wearable(StreamableLoadingResult) ctor not found";

                    if (err == null)
                    {
                        // Params(IWearable, UniTaskCompletionSource<bool>) -- struct ctor.
                        Type csBool = csOpenT.MakeGenericType(typeof(bool));
                        object completionSource = Activator.CreateInstance(csBool);
                        ConstructorInfo paramCtor = paramT.GetConstructor(
                            BindingFlags.Public | BindingFlags.Instance, null,
                            new[] { wearableIfaceT, csBool }, null);
                        if (paramCtor == null) err = "smartwearables: Params(IWearable, UniTaskCompletionSource<bool>) ctor not found";
                        else param = paramCtor.Invoke(new object[] { wearable, completionSource });
                    }
                }
            }
            catch (System.Exception e) { err = "smartwearables: " + (e.InnerException?.Message ?? e.Message); }

            // Fire-and-forget show OUTSIDE any try (TryShowPanelByName uses ShowAsync w/o awaiting; the popup
            // blocks on WaitForCloseIntentAsync which we never satisfy -> no claim/grant happens).
            bool shown = false;
            if (err == null && param != null && mvcManager != null)
            {
                shown = TryShowPanelByName(mvcManager, controllerFull, param, out string uerr);
                if (!shown) err = "smartwearables: show-failed: " + uerr;
            }
            else if (err == null) err = "smartwearables: param or mvcManager null";

            for (int i = 0; i < 18; i++) yield return null;             // settle / let popup animate in
            // HUD/chat-bleed isolation before capture: deactivate any transient notification toast
            // (NewNotificationPanel) and the persistent chat HUD bleeding past this popup's edges, via the same
            // proven helpers (DclPlaytestHarness.cs:573 / :605). Synchronous voids, safe outside any try.
            HideRewardsPopup();
            HideChat(mvcManager);
            yield return CaptureShot("smartwearables");

            m.error = shown ? "shown" : (err ?? "not shown (unknown reason)");
        }

        // Shows the generic one-off Error popup (ErrorPopupController / ErrorPopupView, POPUP layer).
        // This is the plain notification popup (icon + title + description + single OK button), distinct from
        // the connection-error retry modal. Real triggers (RealUserInAppInitializationFlow line 350) call
        // mvcManager.ShowAsync(new ShowCommand<ErrorPopupView, ErrorPopupData>(ErrorPopupData.FromDescription(msg))).
        // ErrorPopupController : ControllerBase<ErrorPopupView, ErrorPopupData>, so it inherits the static
        // IssueCommand(TInputData) -> ShowCommand<ErrorPopupView, ErrorPopupData>; we feed it an ErrorPopupData
        // built from the static factory ErrorPopupData.FromDescription(string). Local-only UI: the popup's only
        // action is the OK button (closes), which we never click, so showing it makes zero noise.
        // FIRE-AND-FORGET: TryShowPanelByName invokes ShowAsync without awaiting; the view's OnViewShow ->
        // Apply renders synchronously, then it waits forever on OkButton.OnClickAsync (a close we never send).
        private static IEnumerator AtlasCapture_errorpopup(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_errorpopup", ok = true };  // NON-GATING
            report.actions.Add(m);

            string err = null;
            object inputObj = null;
            try
            {
                Type dataT = FindType("DCL.UI.ErrorPopup.ErrorPopupData");
                if (dataT == null)
                    err = "errorpopup: type not found (DCL.UI.ErrorPopup.ErrorPopupData)";
                else
                {
                    // static ErrorPopupData FromDescription(string description) -> uses default title/icon,
                    // explicit description value. Matches the real RealUserInAppInitializationFlow path.
                    MethodInfo fromDesc = dataT.GetMethod("FromDescription",
                        BindingFlags.Public | BindingFlags.Static,
                        null, new[] { typeof(string) }, null);
                    if (fromDesc == null)
                        err = "errorpopup: ErrorPopupData.FromDescription(string) not found";
                    else
                        inputObj = fromDesc.Invoke(null, new object[]
                        {
                            "Something went wrong while loading Decentraland. Please try again."
                        });
                }
            }
            catch (System.Exception e) { err = "errorpopup: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Inherited static IssueCommand(ErrorPopupData) -> ShowCommand<ErrorPopupView, ErrorPopupData>;
            // TryShowPanelByName picks the 1-arg overload (param != null), extracts the generic args for ShowAsync.
            if (!TryShowPanelByName(mvcManager, "DCL.UI.ErrorPopup.ErrorPopupController", inputObj, out err))
            { m.error = "errorpopup: " + err; yield break; }

            for (int i = 0; i < 18; i++) yield return null;             // settle / animate in
            // HUD-bleed isolation before capture: deactivate any transient notification toast (NewNotificationPanel)
            // and the persistent chat HUD bleeding past this popup. HideRewardsPopup matches by name on
            // NewNotificationPanel/RewardsHUD only, so it does NOT touch this POPUP-layer ErrorPopupView (the
            // intended target). Same proven helpers (DclPlaytestHarness.cs:573 / :605), synchronous, outside any try.
            HideRewardsPopup();
            HideChat(mvcManager);
            yield return CaptureShot("errorpopup");
            m.error = "shown";                                          // sentinel meaning success
        }

        // P01 login — force-show the LoginSelectionAuthView ("Log in or Sign up"). Pre-world auth-FSM view.
        // With a cached identity the FSM auto-skips the login screen, so the natural observation never captures
        // it debug-free. We force-show it directly via the auth view (the dispatch hides the debug panel +
        // isolates sub-views first). Show(int animHash, bool moreOptionsExpanded) is local UI only — no real login.
        private static IEnumerator AtlasCapture_login(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_login", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            string err = null;
            object loginView = null;
            try
            {
                object authCtl = FindControllerByTypeName(mvcManager, "AuthenticationScreenController");
                object view = authCtl != null ? GetMember(authCtl, "viewInstance") : null;
                if (view == null) { err = "login: skipped:auth-view-null (only reachable in auth mode pre-world)"; }
                else
                {
                    loginView = GetMember(view, "LoginSelectionAuthView");
                    if (loginView == null) err = "login: LoginSelectionAuthView not found";
                    else
                    {
                        var showM = loginView.GetType().GetMethod("Show", BindingFlags.Public | BindingFlags.Instance,
                            null, new[] { typeof(int), typeof(bool) }, null);
                        if (showM == null) err = "login: Show(int,bool) not found";
                        // StringToHash("In") == production's UIAnimationHashes.IN; animHash 0 made SetTrigger a no-op and the
                        // show-await WaitUntil(==0) hang forever, so the In clip never played. Stays in UnityEngine (no client ref).
                        else showM.Invoke(loginView, new object[] { UnityEngine.Animator.StringToHash("In"), false });   // moreOptionsExpanded false
                    }
                }
            }
            catch (System.Exception e) { err = "login: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            for (int i = 0; i < 24; i++) yield return null;
            // The existing-account lobby (LoggedInCached FSM state) re-asserts during the settle and overlaps the
            // login view; re-hide the non-login siblings IMMEDIATELY before capture (CaptureShot grabs frame 0,
            // before the FSM can re-show them). No yields in this try.
            try
            {
                object av = GetMember(FindControllerByTypeName(mvcManager, "AuthenticationScreenController"), "viewInstance");
                foreach (string sib in new[] { "LobbyForExistingAccountAuthView", "LobbyForNewAccountAuthView", "ProfileFetchingAuthView", "VerificationDappAuthView", "VerificationOTPAuthView" })
                {
                    object sv = av != null ? GetMember(av, sib) : null;
                    if (sv == null) continue;
                    var hideM = sv.GetType().GetMethod("Hide", BindingFlags.Public | BindingFlags.Instance, null, System.Type.EmptyTypes, null);
                    hideM?.Invoke(sv, null);
                    object go = GetMember(sv, "gameObject");
                    var sa = go?.GetType().GetMethod("SetActive", new[] { typeof(bool) });
                    sa?.Invoke(go, new object[] { false });
                }
            }
            catch { }
            yield return CaptureShot("login");
            m.error = "shown";
        }

        // Loading tip carousel: shows SceneLoadingScreenController (OVERLAY) with a fresh, never-completing
        // AsyncLoadProcessReport so the loading screen + tip carousel renders for capture. Local overlay only — no noise.
        private static IEnumerator AtlasCapture_loading(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_loading", ok = true };  // non-gating: ok stays true
            report.actions.Add(m);

            string err = null;
            object command = null;
            Type viewType = null;
            Type paramsType = null;

            try
            {
                // 1. Fresh AsyncLoadProcessReport (DCL.Utilities) via static Create(CancellationToken).
                Type reportType = FindType("DCL.Utilities.AsyncLoadProcessReport");
                if (reportType == null) { err = "loading: AsyncLoadProcessReport type not found"; }
                else
                {
                    System.Reflection.MethodInfo createMethod = reportType.GetMethod("Create",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static,
                        null, new[] { typeof(System.Threading.CancellationToken) }, null);
                    if (createMethod == null) { err = "loading: AsyncLoadProcessReport.Create(CancellationToken) not found"; }
                    else
                    {
                        object loadReport = createMethod.Invoke(null, new object[] { System.Threading.CancellationToken.None });
                        if (loadReport == null) { err = "loading: AsyncLoadProcessReport.Create returned null"; }
                        else
                        {
                            // 2. SceneLoadingScreenController + its nested Params struct.
                            Type controllerType = FindType("DCL.SceneLoadingScreens.SceneLoadingScreenController");
                            if (controllerType == null) { err = "loading: SceneLoadingScreenController type not found"; }
                            else
                            {
                                paramsType = controllerType.GetNestedType("Params", System.Reflection.BindingFlags.Public);
                                if (paramsType == null) { err = "loading: SceneLoadingScreenController+Params type not found"; }
                                else
                                {
                                    // 3. new Params(AsyncLoadProcessReport)
                                    System.Reflection.ConstructorInfo paramsCtor = paramsType.GetConstructor(new[] { reportType });
                                    if (paramsCtor == null) { err = "loading: Params(AsyncLoadProcessReport) constructor not found"; }
                                    else
                                    {
                                        object paramsInstance = paramsCtor.Invoke(new[] { loadReport });
                                        // 4. Static IssueCommand(Params) inherited from ControllerBase<TView,TInputData>
                                        //    (FlattenHierarchy: it lives on the generic base, not the derived type).
                                        System.Reflection.MethodInfo issueCommand = controllerType.GetMethod("IssueCommand",
                                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.FlattenHierarchy,
                                            null, new[] { paramsType }, null);
                                        if (issueCommand == null) { err = "loading: IssueCommand(Params) not found"; }
                                        else
                                        {
                                            command = issueCommand.Invoke(null, new[] { paramsInstance });
                                            if (command == null) { err = "loading: IssueCommand returned null"; }
                                            else
                                            {
                                                // 5. SceneLoadingScreenView (the TView generic arg).
                                                viewType = FindType("DCL.SceneLoadingScreens.SceneLoadingScreenView");
                                                if (viewType == null) { err = "loading: SceneLoadingScreenView type not found"; command = null; }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // 6. mvcManager.ShowAsync<SceneLoadingScreenView, Params>(command, ct) — fire-and-forget.
                if (err == null && command != null)
                {
                    System.Reflection.MethodInfo showAsync = null;
                    foreach (System.Reflection.MethodInfo mi in mvcManager.GetType().GetMethods(
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                        if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }

                    if (showAsync == null) { err = "loading: ShowAsync not found on MvcManager"; }
                    else
                    {
                        showAsync.MakeGenericMethod(viewType, paramsType)
                                 .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });

                        // Record the panel key (controllers[typeof(IController<TView,Params>)]) for VerifyShown.
                        Type ifaceOpen = FindType("MVC.IController`2");
                        lastPanelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(viewType, paramsType) : null;
                    }
                }
            }
            catch (System.Exception e) { err = "loading: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }   // yield break is OUTSIDE the try

            // Settle: let the view instantiate, tips load + first tip render.
            for (int i = 0; i < 30; i++) yield return null;

            if (lastPanelKey != null && !VerifyShown(mvcManager, lastPanelKey, out string verifyErr))
            {
                // Non-gating: still capture whatever rendered.
                m.error = "not-shown: " + verifyErr;
                yield return CaptureShot("loading");
                yield break;
            }

            yield return CaptureShot("loading");
            m.error = "shown";
        }

        // New-account onboarding lobby (AUTH mode). NO-PUBLISH render path: instead of entering
        // LobbyForNewAccountAuthState (whose Enter flips controller.IsCurrentlyNewAccount=true, mutates
        // currentState.Value=LoggedIn, reparents the live character preview, and arms FinalizeNewUser ->
        // PublishNewProfileAsync -> selfProfile.UpdateProfileAsync, a real network profile publish), we
        // render ONLY the view. We reach AuthenticationScreenController.viewInstance (protected field on
        // ControllerBase), read its serialized child LobbyForNewAccountAuthView, and call the view's public
        // 0-arg Show() (LobbyForNewAccountAuthView.Show() => ShowAsync().Forget(): it only SetActive(true)s
        // the GameObject and plays the IN animation). The view itself touches NO profile/network/session
        // state -- the publish lives exclusively in FinalizeNewUser, which only fires on a button click we
        // never wire up. Fire-and-forget show (the view renders synchronously), settle, screenshot.
        // DATA-GATED: the parent AuthenticationScreenView is only active during the pre-world auth phase
        // (auth-capture mode). In an in-world atlas session the auth screen is hidden, so the controller/
        // viewInstance is unreachable and we degrade gracefully to not-found. The character-preview avatar
        // is set up by the (skipped) state, so it will be absent -- we capture the onboarding UI chrome
        // (name field, randomize, body-type selector, terms toggles, finalize button, lobby background).
        private static IEnumerator AtlasCapture_lobbynew(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_lobbynew", ok = true };  // NON-GATING: ok ALWAYS stays true
            report.actions.Add(m);

            string err = null;
            object lobbyView = null;

            // ALL synchronous reflection setup here; never yield in this try.
            try
            {
                object authCtl = FindControllerByTypeName(mvcManager, "AuthenticationScreenController");
                if (authCtl == null) { err = "lobbynew: not-found:authCtl (auth screen unreachable in-world; auth-capture mode only)"; }
                else
                {
                    // protected TView viewInstance on ControllerBase; GetMember walks base types incl. NonPublic.
                    object viewInstance = GetMember(authCtl, "viewInstance");
                    if (viewInstance == null) err = "lobbynew: not-shown:viewInstance-null (auth view not yet instantiated)";
                    else
                    {
                        // public LobbyForNewAccountAuthView LobbyForNewAccountAuthView { get; }
                        lobbyView = GetMember(viewInstance, "LobbyForNewAccountAuthView");
                        if (lobbyView == null) err = "lobbynew: not-found:LobbyForNewAccountAuthView";
                    }
                }

                if (lobbyView != null)
                {
                    // public void Show() -> ShowAsync(CancellationToken.None).Forget(): activates GO + plays IN.
                    // NO profile/network/session mutation. Pick the 0-arg overload by param count to avoid
                    // matching any string-arg overload that might exist on sibling view types.
                    System.Reflection.MethodInfo showM = null;
                    foreach (System.Reflection.MethodInfo mi in lobbyView.GetType().GetMethods(
                                 System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        if (mi.Name == "Show" && mi.GetParameters().Length == 0) { showM = mi; break; }
                    }
                    if (showM == null) err = "lobbynew: not-found:Show()-method";
                    else
                    {
                        showM.Invoke(lobbyView, null);   // fire-and-forget; view renders synchronously

                        // STATE FIX (P03 lobby / P04 lobbynew): this driver calls Show() directly and
                        // never runs LobbyForNewAccountAuthState.Enter(), so the body-type dropdown stays
                        // in its prefab-default OPEN state and the BODY TYPE label/icons are unset. The
                        // state normally closes it via view.SetBodyTypeDropdownOpen(false) +
                        // view.UpdateBodyTypeUI(true) (LobbyForNewAccountAuthState.cs:117-118). Replicate
                        // those two PUBLIC view calls here: both are client-local UI only
                        // (SetActive(false) + chevron rotate; label text + male/female icon + checkmark),
                        // no profile/network/FSM mutation. (The 3D avatar preview is NOT reachable from
                        // here — it needs the state's AuthenticationScreenCharacterPreviewController +
                        // a constructed Avatar — so it stays missing in this capture.)
                        try
                        {
                            System.Reflection.MethodInfo closeDropdownM = lobbyView.GetType().GetMethod(
                                "SetBodyTypeDropdownOpen",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                                null, new[] { typeof(bool) }, null);
                            if (closeDropdownM != null) closeDropdownM.Invoke(lobbyView, new object[] { false });

                            System.Reflection.MethodInfo bodyTypeUIM = lobbyView.GetType().GetMethod(
                                "UpdateBodyTypeUI",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                                null, new[] { typeof(bool) }, null);
                            if (bodyTypeUIM != null) bodyTypeUIM.Invoke(lobbyView, new object[] { true });   // BODY TYPE A (male)
                        }
                        catch { /* dropdown-close is best-effort; still capture the lobby */ }
                    }
                }
            }
            catch (System.Exception e) { err = "lobbynew: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            for (int i = 0; i < 18; i++) yield return null;   // settle (ShowAsync IN animation)
            yield return CaptureShot("lobbynew");
            m.error = "shown";                                // sentinel meaning success
        }

        // OTP / verification-code entry screen (registry P05), AUTH-MODE (pre-world).
        // Reached in the real flow when AuthenticationScreenController's FSM enters
        // IdentityVerificationOTPAuthState (CurrentState == AuthStatus.VerificationRequested) and the
        // provider raises OTPSendSucceeded -> VerificationOTPAuthView.Show(email). With a cached identity
        // the auth flow auto-logins and NEVER walks the email->OTP path, so the passive observer never
        // fires. This driver FORCE-SHOWS the OTP view directly for a screenshot:
        //   authCtl.viewInstance (private prop, AuthenticationScreenView)
        //     -> LoginSelectionAuthView.Hide()            (clear the current screen)
        //     -> VerificationOTPAuthView.Show("your@email.com")  (client-local: InputField.Clear() +
        //        fire-and-forget ShowAsync animation + a local description text Replace). The placeholder
        //        email matches the prefab's own placeholder, so no account data appears.
        // NO-NOISE: we do NOT drive the FSM via Enter<IdentityVerificationOTPAuthState> (that would run
        // AuthenticateAsync -> compositeWeb3Provider.LoginAsync(ForOtpFlow) and send a REAL OTP email).
        // We never call SubmitOtp/ResendOtp, never raise OTPVerified, never touch the network. Capture-only.
        // Dispatched from the auth-capture flow (mode "auth"): the AuthenticationScreenController is only
        // reachable via the MVCManager during the pre-in-world auth phase.
        private static IEnumerator AtlasCapture_otp(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_otp", ok = true };  // NON-GATING: ok ALWAYS stays true
            report.actions.Add(m);

            string err = null;
            object otpView = null;

            // ALL synchronous reflection setup here; never yield in this try.
            try
            {
                object authCtl = FindControllerByTypeName(mvcManager, "AuthenticationScreenController");
                if (authCtl == null) { err = "otp: skipped:authCtl-not-found (pre-in-world auth controller not registered)"; }
                else
                {
                    // protected TView? viewInstance { get; private set; }  -> AuthenticationScreenView
                    object view = GetMember(authCtl, "viewInstance");
                    if (view == null) err = "otp: skipped:view-instance-null (auth view not instantiated yet)";
                    else
                    {
                        // Clear whatever screen is currently up so only the OTP screen renders.
                        try
                        {
                            object loginView = GetMember(view, "LoginSelectionAuthView");
                            if (loginView != null)
                            {
                                var hideM = loginView.GetType().GetMethod("Hide",
                                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                                    null, System.Type.EmptyTypes, null);
                                hideM?.Invoke(loginView, null);
                            }
                        }
                        catch { /* non-fatal: still try to show the OTP view */ }

                        otpView = GetMember(view, "VerificationOTPAuthView");
                        if (otpView == null) err = "otp: skipped:VerificationOTPAuthView-null";
                        else
                        {
                            // public void Show(string email) — client-local: InputField.Clear() +
                            // fire-and-forget ShowAsync (UI animation) + local description text Replace.
                            // Pass the prefab's own placeholder so no real email appears in the shot.
                            var showM = otpView.GetType().GetMethod("Show",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                                null, new[] { typeof(string) }, null);
                            if (showM == null) err = "otp: skipped:Show(string)-not-found";
                            else showM.Invoke(otpView, new object[] { "your@email.com" });
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "otp: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            for (int i = 0; i < 24; i++) yield return null;   // settle (ShowAsync IN animation) — OUTSIDE any try
            yield return CaptureShot("otp");
            m.error = "shown";                                // sentinel meaning success
        }

        // Dapp wallet "verify / check your wallet" WAITING view (registry P06). This is the pre-world auth
        // FSM screen rendered by IdentityVerificationDappAuthState while waiting for the user to confirm a
        // signature. We force-show the WAITING UI directly via the view, with a synthetic code + future
        // expiration, so NO real wallet/signature/network call happens.
        //
        // Why this is noise-free: the real wallet RPC lives in compositeWeb3Provider.LoginAsync (called only
        // from IdentityVerificationDappAuthState.AuthenticateAsync), which we never invoke. The view method
        // VerificationDappAuthView.Show(int code, DateTime expiration) is pure local UI: it sets the code
        // TMP_Text, starts a local countdown coroutine, and plays the show animation. Nothing is signed,
        // published, or sent to the network or other users.
        //
        // AUTH mode: the AuthenticationScreenController FSM only exists pre-world. We read the controller's
        // protected viewInstance (ControllerBase<TView>.viewInstance) -> VerificationDappAuthView. If the view
        // is not instantiated (already past auth in an in-world session) we degrade gracefully.
        private static IEnumerator AtlasCapture_verify(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_verify", ok = true };  // NON-GATING: ok ALWAYS stays true
            report.actions.Add(m);

            string err = null;
            object dappView = null;
            System.Reflection.MethodInfo showMethod = null;
            object[] showArgs = null;

            // ALL synchronous reflection setup here; never yield in this try.
            try
            {
                object authCtl = FindControllerByTypeName(mvcManager, "AuthenticationScreenController");
                if (authCtl == null) { err = "verify: skipped:authCtl-not-found (auth FSM only exists pre-world)"; }
                else
                {
                    // ControllerBase<TView>.viewInstance is a protected property; GetMember walks bases.
                    object viewInstance = GetMember(authCtl, "viewInstance");
                    if (viewInstance == null) { err = "verify: skipped:viewInstance-null (auth view not instantiated)"; }
                    else
                    {
                        // AuthenticationScreenView.VerificationDappAuthView { get; }
                        dappView = GetMember(viewInstance, "VerificationDappAuthView");
                        if (dappView == null) { err = "verify: skipped:VerificationDappAuthView-not-found"; }
                        else
                        {
                            // public void Show(int dataCode, DateTime expiration) -- pure local UI, no signature.
                            showMethod = dappView.GetType().GetMethod(
                                "Show",
                                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                                null,
                                new Type[] { typeof(int), typeof(System.DateTime) },
                                null);
                            if (showMethod == null) { err = "verify: skipped:Show(int,DateTime)-not-found"; }
                            else
                            {
                                // Synthetic, display-only: a code and a future UTC expiry so the countdown renders positive.
                                showArgs = new object[] { 123456, System.DateTime.UtcNow.AddMinutes(5) };
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "verify: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Render the waiting UI (no wallet contact). Failure to invoke degrades gracefully; we still capture.
            try { showMethod.Invoke(dappView, showArgs); }
            catch (System.Exception e) { m.error = "verify: show-invoke-failed: " + (e.InnerException?.Message ?? e.Message); }

            for (int i = 0; i < 18; i++) yield return null;             // settle (ShowAsync animation in)
            yield return CaptureShot("verify");
            if (m.error == null) m.error = "shown";                     // sentinel meaning success
        }

        // Web3 wallet confirmation prompt (registry P07): the "confirm in your browser wallet" popup. Real target is
        // Web3ConfirmationPopupView.ShowAsync (fully documented in-body below). AUTH-MODE only — degrades to "skipped"
        // in-world (the auth view is disposed past login). NO NOISE: building the request + ShowAsync toggles local UI only.
        private static IEnumerator AtlasCapture_web3confirm(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_web3confirm", ok = true };  // NON-GATING: ok ALWAYS stays true
            report.actions.Add(m);

            string err = null;
            object popupView = null;   // DCL.AuthenticationScreenFlow.Web3ConfirmationPopupView (ViewBase MonoBehaviour)
            object request = null;     // DCL.Web3.Authenticators.TransactionConfirmationRequest (plain class)

            // P07 web3confirm = the Web3 CONFIRMATION POPUP, NOT the pre-login verification-code screen.
            // Correct target: Web3ConfirmationPopupView.ShowAsync(TransactionConfirmationRequest). The view is
            // Object.Instantiate'd unconditionally + SetActive(false) by Web3AuthenticationPlugin during init
            // (InitializeTransactionConfirmationPopup), so FindObjectsByType(..., Include) finds it even while
            // inactive. ShowAsync sets gameObject.SetActive(true) and returns a UniTask<bool> that only completes
            // on Cancel/Continue button clicks (we never click) -> fire-and-forget, capture the rendered popup.
            // NO-NOISE: building a synthetic TransactionConfirmationRequest and calling ShowAsync only toggles
            // local UI; no wallet/network/signing happens (that lives in ThirdWebEthereumApi, not the view).
            // ALL synchronous reflection setup here; never yield in this try.
            try
            {
                Type viewType = FindType("DCL.AuthenticationScreenFlow.Web3ConfirmationPopupView");
                if (viewType == null) { err = "web3confirm: Web3ConfirmationPopupView type not found"; }
                else
                {
                    var found = UnityEngine.Object.FindObjectsByType(viewType, FindObjectsInactive.Include);
                    if (found != null && found.Length > 0) popupView = found[0];
                    if (popupView == null)
                        err = "web3confirm: skipped:no-Web3ConfirmationPopupView-instance (auth plugin not initialized?)";
                    else
                    {
                        // TransactionConfirmationRequest: plain class, parameterless ctor + public settable props.
                        // Method="eth_sendTransaction" -> IsTransaction=true -> shows the transaction details panel
                        // (balance / gas fee / cost) so the popup renders its richest chrome. Synthetic ETH values.
                        Type reqType = FindType("DCL.Web3.Authenticators.TransactionConfirmationRequest");
                        if (reqType == null) { err = "web3confirm: TransactionConfirmationRequest type not found"; }
                        else
                        {
                            request = System.Activator.CreateInstance(reqType);
                            reqType.GetProperty("Method")?.SetValue(request, "eth_sendTransaction");
                            reqType.GetProperty("EstimatedGasFeeEth")?.SetValue(request, "0.0012");
                            reqType.GetProperty("BalanceEth")?.SetValue(request, "1.2345");
                            // HideDescription / HideDetailsPanel default false -> full popup shown.
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "web3confirm: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Force-show the popup: ShowAsync(TransactionConfirmationRequest) -> SetActive(true), render, returns
            // a UniTask<bool> we deliberately drop (it only resolves on user button clicks, which we never do).
            try
            {
                System.Reflection.MethodInfo showM = popupView.GetType().GetMethod(
                    "ShowAsync",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null,
                    new Type[] { request.GetType() },
                    null);
                if (showM == null) { err = "web3confirm: skipped:ShowAsync(TransactionConfirmationRequest)-not-found"; }
                else
                    showM.Invoke(popupView, new object[] { request });   // fire-and-forget UniTask<bool>
            }
            catch (System.Exception e) { err = "web3confirm: show-failed: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            for (int i = 0; i < 24; i++) yield return null;   // settle (popup SetActive + layout)
            yield return CaptureShot("web3confirm");
            m.error = "shown";                                 // sentinel meaning success
        }

        // Shows the SceneLoadingScreenView (progress bar, carousel tips, breadcrumbs) by issuing a
        // SceneLoadingScreenController.Params built from a fresh AsyncLoadProcessReport, then reusing
        // the shared TryShowPanel helper (IssueCommand -> mvcManager.ShowAsync + lastPanelKey).
        // Local OVERLAY UI only; produces no chat/social/destructive action -> no noise.
        private static IEnumerator AtlasCapture_sceneloading(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_sceneloading", ok = true };
            report.actions.Add(m);

            string err = null;
            object paramInstance = null;
            Type controllerType = null;

            try
            {
                // AsyncLoadProcessReport.Create(CancellationToken) -> the report the loading screen binds to.
                Type reportType = FindType("DCL.Utilities.AsyncLoadProcessReport");
                if (reportType == null) { err = "sceneloading: AsyncLoadProcessReport type not found"; }
                else
                {
                    MethodInfo createMethod = reportType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static);
                    if (createMethod == null) { err = "sceneloading: AsyncLoadProcessReport.Create not found"; }
                    else
                    {
                        object asyncReport = createMethod.Invoke(null, new object[] { System.Threading.CancellationToken.None });
                        if (asyncReport == null) { err = "sceneloading: AsyncLoadProcessReport.Create returned null"; }
                        else
                        {
                            // SceneLoadingScreenController.Params(AsyncLoadProcessReport) — value-type input data.
                            Type paramsType = FindType("DCL.SceneLoadingScreens.SceneLoadingScreenController+Params");
                            if (paramsType == null) { err = "sceneloading: SceneLoadingScreenController+Params type not found"; }
                            else
                            {
                                ConstructorInfo ctor = paramsType.GetConstructor(new[] { reportType });
                                if (ctor == null) { err = "sceneloading: Params(AsyncLoadProcessReport) ctor not found"; }
                                else
                                {
                                    paramInstance = ctor.Invoke(new[] { asyncReport });
                                    if (paramInstance == null) { err = "sceneloading: Params ctor returned null"; }
                                    else
                                    {
                                        controllerType = FindType("DCL.SceneLoadingScreens.SceneLoadingScreenController");
                                        if (controllerType == null) { err = "sceneloading: SceneLoadingScreenController type not found"; }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "sceneloading: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // TryShowPanel issues the command, fire-and-forget ShowAsync's it, and records lastPanelKey.
            // It internally try/catches; no yield crosses it.
            string showErr = null;
            bool shown = false;
            try
            {
                shown = TryShowPanel(mvcManager, controllerType, paramInstance, out showErr);
            }
            catch (System.Exception e) { showErr = (e.InnerException?.Message ?? e.Message); }
            if (!shown) { m.error = "sceneloading: " + (showErr ?? "TryShowPanel failed"); yield break; }

            // Settle: let the tips carousel + RootCanvasGroup/ContentCanvasGroup fade in (~3.6s @ 60fps).
            for (int i = 0; i < 216; i++) yield return null;

            string verifyErr = null;
            if (!VerifyShown(mvcManager, lastPanelKey, out verifyErr)) { m.error = "sceneloading: " + verifyErr; yield break; }

            yield return CaptureShot("sceneloading");
            m.error = "shown";
        }

// Force-show the minimum-hardware-specs warning OVERLAY (DCL.ApplicationMinimumSpecsGuard.MinimumSpecsScreenController).
// WHY FORCE: the controller is NOT a pre-registered MVC panel. MainSceneLoader.VerifyMinimumHardwareRequirementMetAsync
// only constructs + RegisterController + ShowAsync it when shouldShowScreen==true (device FAILS specs OR the
// -forceMinimumSpecsScreen flag is set). On our in-spec capture VM with no flag it is never registered, so the old
// observe-only minspecs.cs always degraded to "controller-not-registered". Here we replicate the production path
// exactly, by reflection: read bootstrapContainer/dynamicSettings off the live MainSceneLoader, provision the
// MinimumSpecsScreenPrefab via AssetsProvisioner (read-only async), build the view factory via CreateLazily,
// construct the controller with the real IWebBrowser + IAnalyticsController + a synthetic IReadOnlyList<SpecResult>
// table, RegisterController, then ShowAsync(IssueCommand()) FIRE-AND-FORGET (no await -- the OVERLAY's
// WaitForCloseIntentAsync is HoldingTask.Task which never completes on its own; the view renders synchronously on
// show). NO-NOISE: this only opens a local UI overlay + reads hardware info + provisions a local addressable; the
// only side effect is an internal analytics.Track(MEETS_MINIMUM_REQUIREMENTS) telemetry ping that production fires
// on every boot anyway -- nothing visible to other users, no chat/profile/account mutation.
private static IEnumerator AtlasCapture_minspecs(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
{
    var m = new PhaseMarker { label = "atlas_minspecs", ok = true };  // NON-GATING: ok stays true
    report.actions.Add(m);

    // ---------- Phase A: locate dependencies + kick off the (async) prefab provisioning. No yield. ----------
    string err = null;
    object provideUniTask = null;            // UniTask<ProvidedAsset<GameObject>>
    object webBrowser = null, analytics = null;
    try
    {
        if (mvcManager == null) { err = "minspecs: mvcManager is null"; }
        else if (FindControllerByTypeName(mvcManager, "MinimumSpecsScreenController") != null)
        {
            // Already registered (real spec failure / -forceMinimumSpecsScreen). Skip rebuild; just show below.
            err = null; webBrowser = "ALREADY"; // sentinel handled in Phase C
        }
        else
        {
            object loader = FindMainSceneLoader();
            if (loader == null) { err = "minspecs: skipped:no-main-scene-loader"; }
            else
            {
                object bootstrap = GetMember(loader, "bootstrapContainer");
                object dynamicSettings = GetMember(loader, "dynamicSettings");
                if (bootstrap == null) err = "minspecs: bootstrapContainer null (not booted yet)";
                else if (dynamicSettings == null) err = "minspecs: dynamicSettings null";
                else
                {
                    webBrowser = GetMember(bootstrap, "WebBrowser");
                    object analyticsContainer = GetMember(bootstrap, "Analytics");
                    analytics = analyticsContainer != null ? GetMember(analyticsContainer, "Controller") : null;
                    object assetsProvisioner = GetMember(bootstrap, "AssetsProvisioner");
                    object prefabRef = GetMember(dynamicSettings, "MinimumSpecsScreenPrefab");
                    if (webBrowser == null) err = "minspecs: WebBrowser null";
                    else if (analytics == null) err = "minspecs: Analytics.Controller null";
                    else if (assetsProvisioner == null) err = "minspecs: AssetsProvisioner null";
                    else if (prefabRef == null) err = "minspecs: MinimumSpecsScreenPrefab ref null";
                    else
                    {
                        // Find the generic ProvideMainAssetAsync<T>(AssetReferenceT<T>, CancellationToken)
                        // overload (NOT the ComponentReference one) and bind T=GameObject.
                        System.Reflection.MethodInfo provide = null;
                        foreach (var mi in assetsProvisioner.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        {
                            if (mi.Name != "ProvideMainAssetAsync" || !mi.IsGenericMethodDefinition) continue;
                            var ps = mi.GetParameters();
                            if (ps.Length != 2) continue;
                            string pn = ps[0].ParameterType.Name;
                            if (pn.StartsWith("AssetReferenceT")) { provide = mi; break; }
                        }
                        if (provide == null) err = "minspecs: ProvideMainAssetAsync<T>(AssetReferenceT) overload not found";
                        else
                        {
                            var bound = provide.MakeGenericMethod(typeof(GameObject));
                            provideUniTask = bound.Invoke(assetsProvisioner, new object[] { prefabRef, System.Threading.CancellationToken.None });
                            if (provideUniTask == null) err = "minspecs: ProvideMainAssetAsync returned null";
                        }
                    }
                }
            }
        }
    }
    catch (System.Exception e) { err = "minspecs: " + (e.InnerException?.Message ?? e.Message); }
    if (err != null) { m.error = "not-shown: " + err; yield break; }

    // ---------- Phase B: await the prefab provision (yields; OUTSIDE any try). Skipped if already registered. ----------
    object providedAsset = null;
    bool alreadyRegistered = ReferenceEquals(webBrowser, "ALREADY");
    if (!alreadyRegistered && provideUniTask != null)
    {
        yield return AwaitUniTask(provideUniTask);
        if (awaitedError != null) { m.error = "not-shown: minspecs: provide-prefab: " + awaitedError; yield break; }
        providedAsset = awaitedResult;   // ProvidedAsset<GameObject>
    }

    // ---------- Phase C: build view factory + controller, register, ShowAsync FIRE-AND-FORGET. No yield. ----------
    try
    {
        if (!alreadyRegistered)
        {
            object prefabGo = providedAsset != null ? GetMember(providedAsset, "Value") : null;  // GameObject
            if (prefabGo == null) { err = "minspecs: provided prefab Value null"; }
            else
            {
                Type viewType = FindType("DCL.ApplicationMinimumSpecsGuard.MinimumSpecsScreenView");
                Type ctlType = FindType("DCL.ApplicationMinimumSpecsGuard.MinimumSpecsScreenController");
                Type specResultType = FindType("DCL.ApplicationMinimumSpecsGuard.SpecResult");
                Type specCategoryType = FindType("DCL.ApplicationMinimumSpecsGuard.SpecCategory");
                if (viewType == null || ctlType == null || specResultType == null || specCategoryType == null)
                    err = "minspecs: type lookup failed (view/ctl/SpecResult/SpecCategory)";
                else
                {
                    // GetComponent(viewType) off the prefab GameObject (reflection: GameObject.GetComponent(Type)).
                    var getComp = typeof(GameObject).GetMethod("GetComponent", new[] { typeof(Type) });
                    object viewComp = getComp.Invoke(prefabGo, new object[] { viewType });
                    if (viewComp == null) err = "minspecs: prefab has no MinimumSpecsScreenView component";
                    else
                    {
                        // Build a synthetic IReadOnlyList<SpecResult> table (read-only; the screen renders these rows).
                        // SpecResult(SpecCategory, bool isMet, string required, string actual).
                        var srCtor = specResultType.GetConstructor(new[] { specCategoryType, typeof(bool), typeof(string), typeof(string) });
                        if (srCtor == null) err = "minspecs: SpecResult ctor not found";
                        else
                        {
                            var listType = typeof(System.Collections.Generic.List<>).MakeGenericType(specResultType);
                            var list = (System.Collections.IList)Activator.CreateInstance(listType);
                            // CRITICAL: MinimumSpecsTablePresenter.Populate FILTERS to results where !IsMet
                            // (it renders ONLY the UNMET specs and returns early if every spec is met). The previous
                            // driver passed IsMet=true for every row, so unmetResults.Count==0 -> ZERO rows populated
                            // and the table showed only the prefab template's placeholder cells ("Id/Minimum/Current").
                            // Fix: pass IsMet=FALSE so each row renders. SetTitle=Category, SetRequiredText=Required
                            // (the "Minimum Recommended" column), SetActualText=Actual (the "Your System" column).
                            // Realistic below-spec values so the warning ("Performance Adjusted to Your Device") reads true.
                            string[][] rows = {
                                new[]{"CPU", "Intel Core i5-8400 / Ryzen 5 2600", "Intel Core i3-7100"},
                                new[]{"RAM", "16 GB",                             "8 GB"},
                                new[]{"GPU", "NVIDIA GTX 1060 / AMD RX 580",     "Intel UHD Graphics 630"},
                            };
                            foreach (var r in rows)
                            {
                                object cat = Enum.Parse(specCategoryType, r[0]);
                                list.Add(srCtor.Invoke(new object[] { cat, false, r[1], r[2] }));
                            }

                            // ViewFactoryMethod = ControllerBase<TView,ControllerNoData>.CreateLazily<TViewMono>(prefab, root).
                            // Find the generic CreateLazily on the controller type (inherited static), bind TViewMono=viewType.
                            System.Reflection.MethodInfo createLazily = null;
                            foreach (var mi in ctlType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                                if (mi.Name == "CreateLazily" && mi.IsGenericMethodDefinition) { createLazily = mi; break; }
                            if (createLazily == null) err = "minspecs: CreateLazily not found";
                            else
                            {
                                object viewFactory = createLazily.MakeGenericMethod(viewType)
                                    .Invoke(null, new object[] { viewComp, null });

                                // new MinimumSpecsScreenController(viewFactory, webBrowser, analytics, IReadOnlyList<SpecResult>)
                                var ctlCtor = ctlType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                                System.Reflection.ConstructorInfo chosen = null;
                                foreach (var c in ctlCtor) if (c.GetParameters().Length == 4) { chosen = c; break; }
                                if (chosen == null) err = "minspecs: MinimumSpecsScreenController(4-arg) ctor not found";
                                else
                                {
                                    object controller = chosen.Invoke(new object[] { viewFactory, webBrowser, analytics, list });

                                    // RegisterController<TView,ControllerNoData>(controller). Bind generics from the
                                    // 0-arg IssueCommand()'s ShowCommand<TView,ControllerNoData> generic args.
                                    System.Reflection.MethodInfo issue0 = null;
                                    foreach (var mi in ctlType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                                        if (mi.Name == "IssueCommand" && mi.GetParameters().Length == 0) { issue0 = mi; break; }
                                    if (issue0 == null) err = "minspecs: 0-arg IssueCommand not found";
                                    else
                                    {
                                        object command = issue0.Invoke(null, null);   // ShowCommand<View,ControllerNoData>
                                        Type[] cmdArgs = command.GetType().GetGenericArguments();  // [View, ControllerNoData]

                                        System.Reflection.MethodInfo regGen = null;
                                        foreach (var mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                            if (mi.Name == "RegisterController" && mi.IsGenericMethodDefinition) { regGen = mi; break; }
                                        if (regGen == null) err = "minspecs: RegisterController not found";
                                        else
                                        {
                                            regGen.MakeGenericMethod(cmdArgs).Invoke(mvcManager, new object[] { controller });

                                            // ShowAsync<TView,TInputData>(command, ct) -- FIRE-AND-FORGET (Invoke only, no await).
                                            System.Reflection.MethodInfo showAsync = null;
                                            foreach (var mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                                if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                                            if (showAsync == null) err = "minspecs: ShowAsync not found";
                                            else
                                                showAsync.MakeGenericMethod(cmdArgs)
                                                    .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        else
        {
            // Already-registered path: just fire ShowAsync via the controller's own type.
            Type ctlType = FindType("DCL.ApplicationMinimumSpecsGuard.MinimumSpecsScreenController");
            System.Reflection.MethodInfo issue0 = null;
            if (ctlType != null)
                foreach (var mi in ctlType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                    if (mi.Name == "IssueCommand" && mi.GetParameters().Length == 0) { issue0 = mi; break; }
            if (issue0 == null) err = "minspecs: 0-arg IssueCommand not found (already-registered path)";
            else
            {
                object command = issue0.Invoke(null, null);
                Type[] cmdArgs = command.GetType().GetGenericArguments();
                System.Reflection.MethodInfo showAsync = null;
                foreach (var mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                if (showAsync == null) err = "minspecs: ShowAsync not found (already-registered path)";
                else showAsync.MakeGenericMethod(cmdArgs).Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });
            }
        }
    }
    catch (System.Exception e) { err = "minspecs: " + (e.InnerException?.Message ?? e.Message); }
    if (err != null) { m.error = "not-shown: " + err; yield break; }

    // ---------- Phase D: settle + capture (OUTSIDE any try). The OVERLAY renders synchronously on show. ----------
    for (int i = 0; i < 18; i++) yield return null;   // settle (view instantiate + table populate)
    yield return CaptureShot("minspecs");

    // Verify the controller actually rendered (non-gating note).
    string rerr = null;
    try
    {
        object ctl = FindControllerByTypeName(mvcManager, "MinimumSpecsScreenController");
        string st = ctl != null ? (GetPublicProperty(ctl, "State")?.ToString() ?? "?") : null;
        if (ctl == null) rerr = "controller-not-found-after-show";
        else if (st == "ViewHidden" || st == "ViewHiding") rerr = "State=" + st;
    }
    catch (System.Exception e) { rerr = "verify-failed: " + (e.InnerException?.Message ?? e.Message); }
    m.error = rerr != null ? ("not-shown: " + rerr) : "shown";   // "shown" sentinel on success
}

        // P11 updaterequired — the "update required" / launcher-redirection OVERLAY
        // (LauncherRedirectionScreenController, namespace DCL.AuthenticationScreenFlow). MainSceneLoader only
        // RegisterControllers it on a REAL version mismatch (DoesApplicationRequireVersionUpdateAsync, gated on
        // currentVersion.IsOlderThan(latestVersion)), so in a normal up-to-date session it is never registered
        // and a plain ShowAsync no-ops / force-show fails. We BUILD + REGISTER it ourselves (the duplicateidentity
        // pattern): load VersionUpdateScreen.prefab via AssetDatabase (the harness is an Editor script), CreateLazily
        // a view factory off the controller, new the controller, RegisterController, then fire-and-forget ShowAsync.
        // CTOR (verified in source) takes FOUR args: (ApplicationVersionGuard versionGuard, ViewFactoryMethod
        // viewFactory, string current, string latest) — NOT just the view factory. We pass versionGuard=null:
        // OnViewInstantiated only calls view.SetVersions(current,latest) + wires CloseButton->ExitUtils.Exit and
        // CloseWithLauncherButton->HandleVersionUpdate (which is the only place versionGuard is dereferenced). Both
        // buttons fire only on a click we never perform, so a null guard is safe for a capture-only run and avoids
        // needing the live web-request/web-browser containers. NO-NOISE: nothing exits or downloads on its own.
        // The overlay never auto-closes (WaitForCloseIntentAsync => UniTask.Never) — a persistent full-screen surface
        // that would obscure later shots — so keep this among the later captures. Capture 'updaterequired'.
        private static IEnumerator AtlasCapture_updaterequired(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_updaterequired", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            string err = null;
            object command = null;
            Type[] cmdArgs = null;
            MethodInfo showAsync = null;
            Type panelKey = null;

            // ---- All reflection / synchronous setup (NO yield in here) ----
            try
            {
                if (mvcManager == null) { err = "updaterequired: mvcManager null"; }
                else
                {
                    Type ctlType  = FindType("DCL.AuthenticationScreenFlow.LauncherRedirectionScreenController");
                    Type viewType = FindType("DCL.AuthenticationScreenFlow.LauncherRedirectionScreenView");
                    if (ctlType == null || viewType == null) { err = "updaterequired: controller/view type not found"; }
                    else
                    {
                        // 0-arg IssueCommand() -> ShowCommand<View, ControllerNoData>; its generic args drive Register/Show.
                        MethodInfo issue0 = null;
                        foreach (var mi in ctlType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                            if (mi.Name == "IssueCommand" && mi.GetParameters().Length == 0) { issue0 = mi; break; }
                        if (issue0 == null) { err = "updaterequired: 0-arg IssueCommand not found"; }
                        else
                        {
                            command = issue0.Invoke(null, null);
                            cmdArgs = command.GetType().GetGenericArguments();   // [LauncherRedirectionScreenView, ControllerNoData]

                            // Build + register only if it hasn't been (it normally hasn't on an up-to-date build).
                            object existing = FindControllerByTypeName(mvcManager, "LauncherRedirectionScreenController");
                            if (existing == null)
                            {
                                var prefabGo = UnityEditor.AssetDatabase.LoadAssetAtPath(
                                    "Assets/DCL/ApplicationsGuards/ApplicationVersionGuard/VersionUpdateScreen.prefab",
                                    typeof(UnityEngine.GameObject)) as UnityEngine.GameObject;
                                if (prefabGo == null) { err = "updaterequired: prefab not found via AssetDatabase"; }
                                else
                                {
                                    object viewComp = prefabGo.GetComponent(viewType);
                                    if (viewComp == null) viewComp = prefabGo.GetComponentInChildren(viewType, true);
                                    if (viewComp == null) { err = "updaterequired: prefab missing LauncherRedirectionScreenView"; }
                                    else
                                    {
                                        // ViewFactoryMethod CreateLazily<TViewMono>(TViewMono prefab, Transform? root) — inherited static generic.
                                        MethodInfo createLazily = null;
                                        foreach (var mi in ctlType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                                            if (mi.Name == "CreateLazily" && mi.IsGenericMethodDefinition) { createLazily = mi; break; }
                                        if (createLazily == null) { err = "updaterequired: CreateLazily not found"; }
                                        else
                                        {
                                            object viewFactory = createLazily.MakeGenericMethod(viewType).Invoke(null, new object[] { viewComp, null });

                                            // new LauncherRedirectionScreenController(ApplicationVersionGuard versionGuard,
                                            //     ViewFactoryMethod viewFactory, string current, string latest)
                                            // Select the 4-arg ctor explicitly (do NOT rely on ctor ordering).
                                            ConstructorInfo ctor = null;
                                            foreach (var ci in ctlType.GetConstructors())
                                                if (ci.GetParameters().Length == 4) { ctor = ci; break; }
                                            if (ctor == null) { err = "updaterequired: 4-arg controller ctor not found"; }
                                            else
                                            {
                                                // versionGuard=null (only dereferenced by the download button we never click),
                                                // current/latest are display-only strings for the description text.
                                                object controller = ctor.Invoke(new object[] { null, viewFactory, "1.0.0-capture", "9.9.9-latest" });

                                                // mvcManager.RegisterController<View, ControllerNoData>(controller)
                                                MethodInfo regGen = null;
                                                foreach (var mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                                    if (mi.Name == "RegisterController" && mi.IsGenericMethodDefinition) { regGen = mi; break; }
                                                if (regGen == null) { err = "updaterequired: RegisterController not found"; }
                                                else regGen.MakeGenericMethod(cmdArgs).Invoke(mvcManager, new object[] { controller });
                                            }
                                        }
                                    }
                                }
                            }

                            if (err == null)
                            {
                                foreach (var mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                    if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                                if (showAsync == null) err = "updaterequired: ShowAsync not found";
                                else { Type iface = FindType("MVC.IController`2"); panelKey = iface != null ? iface.MakeGenericType(cmdArgs) : null; }
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "updaterequired: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Fire-and-forget show (overlay blocks on a close intent that never comes) — OUTSIDE any try.
            try { showAsync.MakeGenericMethod(cmdArgs).Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None }); }
            catch (System.Exception e) { err = "updaterequired: show: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            for (int i = 0; i < 24; i++) yield return null;
            yield return CaptureShot("updaterequired");

            string vErr = "no panel key";
            bool shown = panelKey != null && VerifyShown(mvcManager, panelKey, out vErr);
            m.error = shown ? "shown" : ("shown; verify: " + (vErr ?? "?"));
        }

        // Shows the local-session Connection Error modal (ErrorPopupWithRetryController, CONNECTION_LOST icon).
        // Local-only UI: no chat/social/network action, nothing other users can see. Exit/Restart fire only on click,
        // which we never do, so merely showing the popup is noise-free.
        private static IEnumerator AtlasCapture_connectionerror(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_connectionerror", ok = true };  // NON-GATING
            report.actions.Add(m);

            string err = null;
            object inputObj = null;
            try
            {
                Type controllerT = FindType("DCL.UI.ErrorPopup.ErrorPopupWithRetryController");
                Type inputT      = FindType("DCL.UI.ErrorPopup.ErrorPopupWithRetryController+Input");
                Type iconTypeT   = FindType("DCL.UI.ErrorPopup.ErrorPopupWithRetryController+IconType");

                if (controllerT == null || inputT == null || iconTypeT == null)
                    err = "connectionerror: types not found (ErrorPopupWithRetryController / Input / IconType)";
                else
                {
                    // Input(string title, string description, string retryText, string exitText, IconType iconType)
                    object iconValue = Enum.Parse(iconTypeT, "CONNECTION_LOST");
                    inputObj = Activator.CreateInstance(inputT, new object[]
                    {
                        "Connection Error",
                        "We were unable to connect to Decentraland. Please verify your connection and retry.",
                        "Continue",
                        "Exit Application",
                        iconValue
                    });
                }
            }
            catch (System.Exception e) { err = "connectionerror: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // TryShowPanelByName handles the inherited static IssueCommand(input) -> mvcManager.ShowAsync dance
            // (FlattenHierarchy lookup, generic-arg extraction, lastPanelKey for VerifyShown).
            if (!TryShowPanelByName(mvcManager, "DCL.UI.ErrorPopup.ErrorPopupWithRetryController", inputObj, out err))
            { m.error = "connectionerror: " + err; yield break; }

            for (int i = 0; i < 18; i++) yield return null;             // settle / animate in
            yield return CaptureShot("connectionerror");
            m.error = "shown";                                          // sentinel meaning success
        }

        // P13 duplicateidentity — the "Session Ended" / duplicate-identity OVERLAY. DuplicateIdentityPlugin only
        // RegisterControllers it after an async prefab load that never completes in a normal (no-duplicate)
        // session, so the controller is never registered and a plain ShowAsync no-ops. We BUILD + REGISTER it
        // ourselves (the minspecs pattern): load the prefab via AssetDatabase (the harness is an Editor script),
        // CreateLazily a view factory, new the controller, RegisterController, then fire-and-forget ShowAsync.
        // NO-NOISE: OnBeforeViewShow only disables input + wires the Exit button (fires on a click we never do);
        // nothing disconnects the session. The overlay disables input and never auto-closes, so this MUST be the
        // ABSOLUTE LAST capture in a run (dispatch pins it last).
        private static IEnumerator AtlasCapture_duplicateidentity(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_duplicateidentity", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            string err = null;
            object command = null;
            Type[] cmdArgs = null;
            MethodInfo showAsync = null;
            Type panelKey = null;

            // ---- All reflection / synchronous setup (NO yield in here) ----
            try
            {
                if (mvcManager == null) { err = "duplicateidentity: mvcManager null"; }
                else
                {
                    Type ctlType  = FindType("DCL.UI.DuplicateIdentityPopup.DuplicateIdentityWindowController");
                    Type viewType = FindType("DCL.UI.DuplicateIdentityPopup.DuplicateIdentityWindowView");
                    if (ctlType == null || viewType == null) { err = "duplicateidentity: controller/view type not found"; }
                    else
                    {
                        // 0-arg IssueCommand() -> ShowCommand<View, ControllerNoData>; its generic args drive Register/Show.
                        MethodInfo issue0 = null;
                        foreach (var mi in ctlType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                            if (mi.Name == "IssueCommand" && mi.GetParameters().Length == 0) { issue0 = mi; break; }
                        if (issue0 == null) { err = "duplicateidentity: 0-arg IssueCommand not found"; }
                        else
                        {
                            command = issue0.Invoke(null, null);
                            cmdArgs = command.GetType().GetGenericArguments();   // [DuplicateIdentityWindowView, ControllerNoData]

                            // Build + register only if the plugin hasn't (it normally hasn't).
                            object existing = FindControllerByTypeName(mvcManager, "DuplicateIdentityWindowController");
                            if (existing == null)
                            {
                                var prefabGo = UnityEditor.AssetDatabase.LoadAssetAtPath(
                                    "Assets/DCL/UI/DuplicateIdentityPopup/DuplicateIdentityWindow.prefab",
                                    typeof(UnityEngine.GameObject)) as UnityEngine.GameObject;
                                if (prefabGo == null) { err = "duplicateidentity: prefab not found via AssetDatabase"; }
                                else
                                {
                                    object viewComp = prefabGo.GetComponent(viewType);
                                    if (viewComp == null) viewComp = prefabGo.GetComponentInChildren(viewType, true);
                                    if (viewComp == null) { err = "duplicateidentity: prefab missing DuplicateIdentityWindowView"; }
                                    else
                                    {
                                        // ViewFactoryMethod CreateLazily<TViewMono>(TViewMono prefab, Transform? root) — inherited static generic.
                                        MethodInfo createLazily = null;
                                        foreach (var mi in ctlType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                                            if (mi.Name == "CreateLazily" && mi.IsGenericMethodDefinition) { createLazily = mi; break; }
                                        if (createLazily == null) { err = "duplicateidentity: CreateLazily not found"; }
                                        else
                                        {
                                            object viewFactory = createLazily.MakeGenericMethod(viewType).Invoke(null, new object[] { viewComp, null });
                                            // new DuplicateIdentityWindowController(ViewFactoryMethod)
                                            object controller = ctlType.GetConstructors()[0].Invoke(new object[] { viewFactory });
                                            // mvcManager.RegisterController<View, ControllerNoData>(controller)
                                            MethodInfo regGen = null;
                                            foreach (var mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                                if (mi.Name == "RegisterController" && mi.IsGenericMethodDefinition) { regGen = mi; break; }
                                            if (regGen == null) { err = "duplicateidentity: RegisterController not found"; }
                                            else regGen.MakeGenericMethod(cmdArgs).Invoke(mvcManager, new object[] { controller });
                                        }
                                    }
                                }
                            }

                            if (err == null)
                            {
                                foreach (var mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                    if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                                if (showAsync == null) err = "duplicateidentity: ShowAsync not found";
                                else { Type iface = FindType("MVC.IController`2"); panelKey = iface != null ? iface.MakeGenericType(cmdArgs) : null; }
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "duplicateidentity: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Fire-and-forget show (overlay blocks on a close intent that never comes) — OUTSIDE any try.
            try { showAsync.MakeGenericMethod(cmdArgs).Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None }); }
            catch (System.Exception e) { err = "duplicateidentity: show: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            for (int i = 0; i < 24; i++) yield return null;
            yield return CaptureShot("duplicateidentity");

            string vErr = "no panel key";
            bool shown = panelKey != null && VerifyShown(mvcManager, panelKey, out vErr);
            m.error = shown ? "shown" : ("shown; verify: " + (vErr ?? "?"));
        }

        // Open the Explore panel on the Communities section (the CommunitiesBrowserController panel) and capture it.
        // Read-only: we only open/show the browser via TryOpenExplorePanel (ExploreSections.Communities ->
        // ExplorePanelController.IssueCommand -> MvcManager.ShowAsync). No join/create/leave or any other noise.
        // LOADWAIT FIX: the previous capture caught skeleton bars (mid-load). We now POLL the live
        // "Browse Communities" grid (DCL.Communities.CommunitiesBrowser.FilteredCommunitiesView.CurrentResultsCount)
        // until real community cards have streamed in (or up to ~6s), then settle for thumbnails before capture.
        private static IEnumerator AtlasCapture_communities(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_communities", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            // ---- Phase 1: fire the Explore->Communities show (sync; helper handles IssueCommand/ShowAsync + lastPanelKey) ----
            string err = null;
            bool opened = false;
            try
            {
                if (mvcManager == null) err = "communities: mvcManager null";
                else
                {
                    opened = TryOpenExplorePanel(mvcManager, "Communities", null, out string openErr);
                    if (!opened) err = "communities: TryOpenExplorePanel failed: " + openErr;
                }
            }
            catch (System.Exception e) { err = "communities: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // ---- Phase 2: let the explore panel + communities browser instantiate ----
            for (int i = 0; i < 24; i++) yield return null;

            // ---- Phase 3: POLL the FilteredCommunitiesView grid until live cards load (up to ~6s) ----
            // The "Browse Communities" grid is FilteredCommunitiesView; CurrentResultsCount > 0 means real
            // community cards have rendered (skeletons are gone). Find it via Resources.FindObjectsOfTypeAll so
            // we don't depend on the controller graph. Reads are in tiny try blocks WITHOUT yields; the loop
            // (with 'yield return null') lives OUTSIDE any try.
            System.Type filteredViewT = null;
            try { filteredViewT = FindType("DCL.Communities.CommunitiesBrowser.FilteredCommunitiesView"); }
            catch { filteredViewT = null; }

            int resultsCount = 0;
            for (int i = 0; i < 360; i++)   // ~6s at 60fps
            {
                int polled = 0;
                try
                {
                    if (filteredViewT != null)
                    {
                        UnityEngine.Object[] views = UnityEngine.Resources.FindObjectsOfTypeAll(filteredViewT);
                        if (views != null)
                        {
                            for (int v = 0; v < views.Length; v++)
                            {
                                object cntObj = GetPublicProperty(views[v], "CurrentResultsCount");
                                if (cntObj is int c && c > polled) polled = c;
                            }
                        }
                    }
                }
                catch { /* keep polling */ }

                resultsCount = polled;
                if (resultsCount > 0) break;
                yield return null;
            }

            // ---- Phase 4: settle so card thumbnails finish decoding/painting, then capture ----
            // (Generous fixed settle even if the poll never saw cards — degraded/empty data still gets captured.)
            int settle = resultsCount > 0 ? 60 : 240;
            for (int i = 0; i < settle; i++) yield return null;
            yield return CaptureShot("communities");

            // ---- Verify the panel actually rendered (verify outside any try) ----
            string verifyErr = "no panel key";
            bool shown = lastPanelKey != null && VerifyShown(mvcManager, lastPanelKey, out verifyErr);
            m.error = shown ? "shown" : ("not-shown: " + (verifyErr ?? "no panel key"));
        }

        // Opens the local user's OWN passport profile (read-only, no noise) and screenshots the rendered panel.
        // Uses ISelfProfile (profileContainer.SelfProfile post-#8996; ReachSelfProfile chains old->new) for a guaranteed userId so the panel renders
        // even on an empty-parcel session; falls back to capturing whatever shows if the profile is unavailable.
        private static IEnumerator AtlasCapture_passport(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_passport", ok = true };
            report.actions.Add(m);

            // --- Phase 1: synchronous reflection to obtain the self-profile UniTask (NO yield in here) ---
            string err = null;
            object profileTask = null;
            try
            {
                object selfProfile = ReachSelfProfile(dynamicContainer);
                if (selfProfile == null) err = "passport: selfProfile not found on dynamicContainer";
                else
                {
                    MethodInfo profileAsync = null;
                    foreach (MethodInfo mi in selfProfile.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        if (mi.Name == "ProfileAsync") { profileAsync = mi; break; }
                    if (profileAsync == null) err = "passport: ProfileAsync not found";
                    else
                    {
                        // ProfileAsync(CancellationToken ct) -> UniTask<Profile?>
                        object[] args = new object[profileAsync.GetParameters().Length];
                        for (int i = 0; i < args.Length; i++) args[i] = System.Threading.CancellationToken.None;
                        profileTask = profileAsync.Invoke(selfProfile, args);
                    }
                }
            }
            catch (System.Exception e) { err = "passport: " + (e.InnerException?.Message ?? e.Message); }

            // If profile reflection failed, still capture whatever is on screen so the gap is visible.
            if (err != null) { m.error = err; for (int i = 0; i < 18; i++) yield return null; yield return CaptureShot("passport"); yield break; }

            // --- Await the profile fetch OUTSIDE any try/catch ---
            yield return AwaitUniTask(profileTask);
            object profileResult = awaitedResult;
            if (awaitedError != null) { m.error = "passport: profile-fetch: " + awaitedError; for (int i = 0; i < 18; i++) yield return null; yield return CaptureShot("passport"); yield break; }

            // --- Phase 2: build PassportParams + show the panel (synchronous; NO yield in here) ---
            err = null;
            Type panelKey = null;
            try
            {
                string userId = profileResult != null ? GetMember(profileResult, "UserId")?.ToString() : null;
                if (string.IsNullOrEmpty(userId)) { err = "passport: own UserId unavailable"; }
                else
                {
                    Type passportControllerT = FindType("DCL.Passport.PassportController");
                    Type passportParamsT = FindType("DCL.Passport.PassportParams");
                    if (passportControllerT == null || passportParamsT == null) err = "passport: passport types not found";
                    else
                    {
                        // PassportParams(string userId, string badgeIdSelected = null, bool isOwnProfile = false)
                        object passportParams = System.Activator.CreateInstance(passportParamsT, new object[] { userId, null, true });

                        MethodInfo issueCommand = null;
                        foreach (MethodInfo mi in passportControllerT.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                            if (mi.Name == "IssueCommand") { issueCommand = mi; break; }
                        if (issueCommand == null) err = "passport: IssueCommand not found";
                        else
                        {
                            object command = issueCommand.Invoke(null, new object[] { passportParams });
                            if (command == null) err = "passport: IssueCommand returned null";
                            else
                            {
                                MethodInfo showAsync = null;
                                foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                    if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                                if (showAsync == null) err = "passport: ShowAsync not found";
                                else
                                {
                                    Type[] genArgs = command.GetType().GetGenericArguments();
                                    showAsync.MakeGenericMethod(genArgs)
                                             .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });
                                    Type ifaceOpen = FindType("MVC.IController`2");
                                    panelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(genArgs) : null;
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "passport: " + (e.InnerException?.Message ?? e.Message); }

            if (err != null) { m.error = err; for (int i = 0; i < 18; i++) yield return null; yield return CaptureShot("passport"); yield break; }

            // --- Settle frames for the passport to render ---
            for (int i = 0; i < 18; i++) yield return null;

            // --- Verify + capture (verify is non-gating; we always capture) ---
            string verifyErr = null;
            bool shown = panelKey != null && VerifyShown(mvcManager, panelKey, out verifyErr);
            yield return CaptureShot("passport");
            m.error = shown ? "shown" : ("not-shown: " + (verifyErr ?? "unknown"));
        }

        // Force-show the Create-Community FORM modal (CommunityCreationEditionController) directly,
        // bypassing the Communities browser (CommunitiesBrowserController was unavailable). The controller
        // is ControllerBase<CommunityCreationEditionView, CommunityCreationEditionParameter> with the
        // inherited static IssueCommand(param) -> ShowCommand. We build a SYNTHETIC parameter for a NEW
        // community: canCreateCommunities=true (so the creation form shows, not the buy-a-NAME splash),
        // communityId="" (NEW, not edition), thumbnailSpriteCache=null (view uses its own cache).
        // NON-NOISE: ShowAsync only renders the form. The destructive POST happens solely on the in-form
        // "Create" button click (CreateCommunityButtonClicked), which the harness never triggers.
        // FIRE-AND-FORGET: the controller blocks on a close-intent (backgroundCloseButton), so we do NOT
        // AwaitUniTask the ShowAsync; we settle frames and capture.
        private static IEnumerator AtlasCapture_communitycreate(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_communitycreate", ok = true };  // NON-GATING: ok ALWAYS stays true
            report.actions.Add(m);

            string err = null;
            object command = null;
            MethodInfo showAsync = null;
            Type[] genArgs = null;
            Type panelKey = null;
            try
            {
                if (mvcManager == null) { err = "communitycreate: mvcManager null"; }
                else
                {
                    Type controllerT = FindType("DCL.Communities.CommunityCreation.CommunityCreationEditionController");
                    Type paramT = FindType("DCL.Communities.CommunityCreation.CommunityCreationEditionParameter");

                    if (controllerT == null) err = "communitycreate: CommunityCreationEditionController type not found";
                    else if (paramT == null) err = "communitycreate: CommunityCreationEditionParameter type not found";
                    else
                    {
                        // CommunityCreationEditionParameter(bool canCreateCommunities, string communityId, ISpriteCache thumbnailSpriteCache)
                        // Synthetic NEW-community form: allow creation, no id, no shared sprite cache.
                        object param = paramT.GetConstructors()[0].Invoke(new object[] { true, string.Empty, null });

                        // IssueCommand is inherited static from ControllerBase<TView,TInputData>; there are
                        // 0-arg and 1-arg overloads, so pick the 1-arg one by param count (GetMethod by name
                        // alone would throw AmbiguousMatchException).
                        MethodInfo issue = null;
                        foreach (MethodInfo mi in controllerT.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                            if (mi.Name == "IssueCommand" && mi.GetParameters().Length == 1) { issue = mi; break; }
                        if (issue == null) err = "communitycreate: IssueCommand(1-arg) not found";
                        else
                        {
                            command = issue.Invoke(null, new[] { param });
                            if (command == null) err = "communitycreate: IssueCommand returned null";
                            else
                            {
                                // mvcManager.ShowAsync<TView,TInput>(command, CancellationToken) — generic instance method.
                                foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                    if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                                if (showAsync == null) err = "communitycreate: ShowAsync not found";
                                else
                                {
                                    genArgs = command.GetType().GetGenericArguments();
                                    Type ifaceOpen = FindType("MVC.IController`2");
                                    panelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(genArgs) : null;
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "communitycreate: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Fire the create-form ShowAsync (fire-and-forget) OUTSIDE the try; it blocks on a close-intent.
            try
            {
                showAsync.MakeGenericMethod(genArgs)
                         .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });
            }
            catch (System.Exception e) { err = "communitycreate: ShowAsync failed: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Settle: let the creation form view instantiate + show (LoadPanelAsync renders form in loading state).
            for (int i = 0; i < 90; i++) yield return null;   // long settle: create-form content load

            // Verify the form is actually shown (State != ViewHidden/ViewHiding). Non-gating.
            string note = "shown";
            if (panelKey != null && !VerifyShown(mvcManager, panelKey, out string verifyErr))
                note = "not-shown: " + (verifyErr ?? "?");

            yield return CaptureShot("communitycreate");
            m.error = note;
        }

        // S04 createcommunity — REAL MUTATION (user explicitly authorized creating a community).
        // Opens the Create-Community FORM (CommunityCreationEditionController, the same controller that
        // CreateCommunityCommand.Execute -> mvcManager.ShowAsync(CommunityCreationEditionController.IssueCommand(...))
        // surfaces), then INVOKES THE ACTUAL CREATE through the controller's own data provider — the exact
        // call its in-form Create button handler makes.
        //
        // SUBMIT PATH (verified in source):
        //   View.CreateButtonClicked -> CreateCommunityButtonClicked(name,desc,lands,worlds,privacy,visibility)
        //   -> CommunityCreationEditionController.CreateCommunity(...) -> CreateCommunityAsync(...)
        //   -> dataProvider.CreateOrUpdateCommunityAsync(communityId:null, name, description, thumbnail:null,
        //                                                 lands, worlds, privacy, visibility, ct)   <-- the POST.
        // We cannot await the controller's CreateCommunityAsync (it is UniTaskVoid, fire-and-forget, and routes
        // the success card through mvcManager). To both REALLY MUTATE and CAPTURE the resulting id/name, we reach
        // the registered controller's private 'dataProvider' field and call CreateOrUpdateCommunityAsync directly
        // with communityId=null (the new-community branch), name='Evaristo Test Community', a description
        // (the form requires name+description to enable its button; the moderation API also expects both),
        // empty lands/worlds, privacy=@public (membership default), visibility=all (discoverable default),
        // thumbnail=null (optional image skipped). We AwaitUniTask the create UniTask and read result.data.id/name.
        // MUTATES THE LIVE ACCOUNT — intended.
        private static IEnumerator AtlasCapture_createcommunity(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_createcommunity", ok = true };  // NON-GATING: ok ALWAYS stays true
            report.actions.Add(m);

            // ---- Phase 1: build + fire the create-form ShowAsync (so the form renders for the screenshot) ----
            string err = null;
            object command = null;
            MethodInfo showAsync = null;
            Type[] genArgs = null;
            Type panelKey = null;
            try
            {
                if (mvcManager == null) { err = "createcommunity: mvcManager null"; }
                else
                {
                    Type controllerT = FindType("DCL.Communities.CommunityCreation.CommunityCreationEditionController");
                    Type paramT = FindType("DCL.Communities.CommunityCreation.CommunityCreationEditionParameter");
                    if (controllerT == null) err = "createcommunity: CommunityCreationEditionController type not found";
                    else if (paramT == null) err = "createcommunity: CommunityCreationEditionParameter type not found";
                    else
                    {
                        // CommunityCreationEditionParameter(bool canCreateCommunities, string communityId, ISpriteCache thumbnailSpriteCache)
                        // canCreateCommunities=true -> creation form (not the buy-a-NAME splash); communityId="" -> NEW (not edition).
                        object param = paramT.GetConstructors()[0].Invoke(new object[] { true, string.Empty, null });

                        MethodInfo issue = null;
                        foreach (MethodInfo mi in controllerT.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                            if (mi.Name == "IssueCommand" && mi.GetParameters().Length == 1) { issue = mi; break; }
                        if (issue == null) err = "createcommunity: IssueCommand(1-arg) not found";
                        else
                        {
                            command = issue.Invoke(null, new[] { param });
                            if (command == null) err = "createcommunity: IssueCommand returned null";
                            else
                            {
                                foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                    if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                                if (showAsync == null) err = "createcommunity: ShowAsync not found";
                                else
                                {
                                    genArgs = command.GetType().GetGenericArguments();
                                    Type ifaceOpen = FindType("MVC.IController`2");
                                    panelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(genArgs) : null;
                                }
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "createcommunity: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Fire the form ShowAsync (fire-and-forget; it blocks on a close-intent) OUTSIDE the try.
            try
            {
                showAsync.MakeGenericMethod(genArgs)
                         .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });
            }
            catch (System.Exception e) { err = "createcommunity: ShowAsync failed: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Settle so the controller registers + the form view instantiates/loads.
            for (int i = 0; i < 90; i++) yield return null;

            // ---- Phase 2: reach the registered controller's data provider + build the REAL create UniTask ----
            object createTask = null;
            try
            {
                object creationController = FindControllerByTypeName(mvcManager, "CommunityCreationEditionController");
                if (creationController == null) err = "createcommunity: CommunityCreationEditionController not registered";
                else
                {
                    object dataProvider = GetPrivateField(creationController, "dataProvider");
                    if (dataProvider == null) err = "createcommunity: dataProvider field not found";
                    else
                    {
                        // Resolve the CommunityPrivacy/CommunityVisibility enums and pick the defaults the form uses:
                        // membership default = @public, visibility default = all (discoverable).
                        Type privacyT = FindType("DCL.Communities.CommunitiesDataProvider.DTOs.CommunityPrivacy");
                        Type visibilityT = FindType("DCL.Communities.CommunitiesDataProvider.DTOs.CommunityVisibility");
                        if (privacyT == null) err = "createcommunity: CommunityPrivacy enum not found";
                        else if (visibilityT == null) err = "createcommunity: CommunityVisibility enum not found";
                        else
                        {
                            object privacyPublic = System.Enum.Parse(privacyT, "public"); // member name '@public' -> "public"
                            object visibilityAll = System.Enum.Parse(visibilityT, "all");

                            MethodInfo createMi = dataProvider.GetType()
                                .GetMethod("CreateOrUpdateCommunityAsync", BindingFlags.Public | BindingFlags.Instance);
                            if (createMi == null) err = "createcommunity: CreateOrUpdateCommunityAsync not found";
                            else
                            {
                                var lands = new System.Collections.Generic.List<string>();
                                var worlds = new System.Collections.Generic.List<string>();
                                // communityId=null -> NEW-community POST branch; thumbnail=null -> optional image skipped.
                                object[] args = new object[]
                                {
                                    null,                       // communityId (null => create)
                                    "Evaristo Test Community",  // name (required)
                                    "Created via Atlas in-editor driver (authorized).", // description (form requires it)
                                    null,                       // thumbnail bytes (optional, skipped)
                                    lands,                      // lands
                                    worlds,                     // worlds
                                    privacyPublic,              // privacy = @public (default)
                                    visibilityAll,              // visibility = all/discoverable (default)
                                    System.Threading.CancellationToken.None
                                };
                                createTask = createMi.Invoke(dataProvider, args);
                                if (createTask == null) err = "createcommunity: create UniTask null";
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "createcommunity: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // ---- Await the REAL create POST OUTSIDE any try ----
            yield return AwaitUniTask(createTask);

            // ---- Phase 3: read the resulting community id/name (response.data is a struct CommunityData) ----
            string createdId = null, createdName = null;
            string readErr = null;
            try
            {
                if (awaitedError == null && awaitedResult != null)
                {
                    object data = GetMember(awaitedResult, "data");
                    if (data != null)
                    {
                        createdId = GetMember(data, "id")?.ToString();
                        createdName = GetMember(data, "name")?.ToString();
                    }
                }
            }
            catch (System.Exception e) { readErr = e.InnerException?.Message ?? e.Message; }

            // Let the success card (CommunityCardController, opened by the real handler on success) settle, then capture.
            for (int i = 0; i < 120; i++) yield return null;
            yield return CaptureShot("communitycreate");

            // ---- Report (m.error carries the create outcome; label stays atlas_createcommunity, ok stays true) ----
            if (awaitedError != null)
                m.error = "create-failed: " + awaitedError;
            else if (!string.IsNullOrEmpty(createdId))
                m.error = "shown; created id=" + createdId + " name=" + (createdName ?? "?");
            else if (readErr != null)
                m.error = "shown; created (id-read-error: " + readErr + ")";
            else
                m.error = "shown; created (id not in response)";
        }

        // Open the OWN-profile Passport, navigate to its Badges section and render the badge detail UI (no noise: viewing your own passport only)
        private static IEnumerator AtlasCapture_badgesdetail(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_badgesdetail", ok = true };
            report.actions.Add(m);

            string err = null;
            object selfProfile = null;
            object profileTask = null;

            // --- synchronous setup: get the self-profile service + kick off ProfileAsync ---
            try
            {
                selfProfile = ReachSelfProfile(dynamicContainer);
                if (selfProfile == null) err = "badgesdetail: selfProfile not found";
                else
                {
                    profileTask = TryInvoke(selfProfile, "ProfileAsync",
                        new object[] { System.Threading.CancellationToken.None }, out string ierr);
                    if (profileTask == null) err = "badgesdetail: ProfileAsync invoke failed: " + ierr;
                }
            }
            catch (System.Exception e) { err = "badgesdetail: " + (e.InnerException?.Message ?? e.Message); }
            // Fallback like passport: still settle + capture so S05 is never an empty deliverable on a transient miss.
            if (err != null) { m.error = err; for (int i = 0; i < 18; i++) yield return null; yield return CaptureShot("badgesdetail"); yield break; }

            // --- await the profile (OUTSIDE try) ---
            yield return AwaitUniTask(profileTask);
            if (awaitedError != null) { m.error = "badgesdetail: ProfileAsync failed: " + awaitedError; yield break; }

            object profile = awaitedResult;
            string userId = null;

            // --- build PassportParams + open the passport panel (own profile) ---
            object passportParam = null;
            try
            {
                if (profile == null) err = "badgesdetail: profile result null";
                else
                {
                    userId = GetPublicProperty(profile, "UserId") as string;
                    if (string.IsNullOrEmpty(userId)) err = "badgesdetail: userId empty";
                }

                if (err == null)
                {
                    // PassportParams is a struct in namespace DCL.Passport (NOT DCL.Passport.Bridge).
                    // ctor: PassportParams(string userId, string? badgeIdSelected = null, bool isOwnProfile = false)
                    Type paramsT = FindType("DCL.Passport.PassportParams");
                    if (paramsT == null) err = "badgesdetail: PassportParams type not found";
                    else
                    {
                        ConstructorInfo ctor = paramsT.GetConstructor(new[] { typeof(string), typeof(string), typeof(bool) });
                        if (ctor == null) err = "badgesdetail: PassportParams constructor not found";
                        else
                            passportParam = ctor.Invoke(new object[] { userId, null, true });
                    }
                }

                if (err == null)
                {
                    // TryShowPanelByName builds ShowCommand via IssueCommand(param) + ShowAsync internally.
                    if (!TryShowPanelByName(mvcManager, "DCL.Passport.PassportController", passportParam, out string showErr))
                        err = "badgesdetail: open passport failed: " + showErr;
                }
            }
            catch (System.Exception e) { err = "badgesdetail: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // --- let the passport view + character preview settle before navigating ---
            for (int i = 0; i < 18; i++) yield return null;

            // --- navigate to the Badges section via the controller's private OpenBadgesSection() ---
            try
            {
                object passportCtl = FindControllerByTypeName(mvcManager, "PassportController");
                if (passportCtl != null)
                {
                    MethodInfo openBadges = passportCtl.GetType().GetMethod("OpenBadgesSection",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (openBadges != null)
                    {
                        // single optional string param (badgeIdSelected) -> pass null to open the section
                        ParameterInfo[] ps = openBadges.GetParameters();
                        object[] callArgs = ps.Length == 1 ? new object[] { null } : new object[0];
                        openBadges.Invoke(passportCtl, callArgs);
                    }
                }
                // non-fatal: if the controller/method is missing we still capture the passport overview
            }
            catch (System.Exception) { /* non-fatal: capture whatever rendered */ }

            // --- POLL for the BadgeInfo detail subview to render (instead of a fixed 24-frame wait). ---
            // OpenBadgesSection() kicks off PassportController.LoadPassportSectionAsync (async, network
            // FetchBadgesAsync) -> badgesDetails module Setup -> SelectFirstBadge ->
            // BadgeInfo_PassportModuleSubController.Setup -> SetAsLoading(false) which does
            // MainContainer.SetActive(true) (BadgeInfo_PassportModuleSubController.cs:88,95). The old
            // 24-frame wait was shorter than the fetch, so the grid showed but the right-hand BadgeInfo
            // detail panel stayed empty. Poll passportCtl.viewInstance.BadgeInfoModuleView.MainContainer
            // .activeSelf (public chain: PassportController.viewInstance.BadgeInfoModuleView @
            // PassportView.cs:52, BadgeInfo_PassportModuleView.MainContainer @
            // BadgeInfo_PassportModuleView.cs:11) until it goes active, up to ~8s (480 frames), then a
            // short extra settle for the 3D image/tier buttons to paint. Read-only.
            object badgeMainContainer = null;
            for (int i = 0; i < 480; i++)
            {
                bool detailShown = false;
                try
                {
                    if (badgeMainContainer == null)
                    {
                        object passportCtl2 = FindControllerByTypeName(mvcManager, "PassportController");
                        object passView = passportCtl2 != null ? GetMember(passportCtl2, "viewInstance") : null;
                        object badgeInfoView = passView != null ? GetMember(passView, "BadgeInfoModuleView") : null;
                        badgeMainContainer = badgeInfoView != null ? GetMember(badgeInfoView, "MainContainer") : null;
                    }
                    object active = badgeMainContainer != null ? GetMember(badgeMainContainer, "activeSelf") : null;
                    if (active is bool ab) detailShown = ab;
                }
                catch { detailShown = false; }
                if (detailShown) break;
                yield return null;
            }
            // let the selected badge's 3D image / tier buttons finish painting after the detail activates
            for (int i = 0; i < 48; i++) yield return null;

            yield return CaptureShot("badgesdetail");
            m.error = "shown";
        }

        // Open the Community Card: reach the registered CommunityCardController, pull a live community id via
        // its CommunitiesDataProvider (read-only list), then IssueCommand->ShowAsync the card and screenshot it.
        // LOADWAIT FIX: the previous capture settled only ~18 frames, so the card rendered its skeleton bones
        // (main loadingObject + the auto-selected ANNOUNCEMENTS/MEMBERS section skeleton). The card's content is
        // gated on GetCommunityAsync + opt-out + invite checks (multiple sequential signed fetches) before
        // viewInstance.SetLoadingState(false) hides the SkeletonLoadingView. We now POLL the view's skeleton
        // CanvasGroups (main loadingObject + AnnouncementsSectionView.loadingObject): SkeletonLoadingView.HideLoading()
        // drops loadingCanvasGroup.alpha to 0 and fades loadedCanvasGroup in. We wait until both skeletons are
        // hidden (alpha ~0) up to ~6s, then a generous settle for the loaded fade-in tween, then capture.
        private static IEnumerator AtlasCapture_communitycard(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_communitycard", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);
            yield return HideExplorePanel(mvcManager);  // hide the persistent Explore/Communities grid bleeding behind this POPUP card (matches communitycontent)

            // ---- Phase 1: locate the CommunityCardController + its CommunitiesDataProvider, build the list task ----
            // The data provider is NOT a field on dynamicContainer (it is a bootstrap local), but the registered
            // CommunityCardController holds it as private field 'communitiesDataProvider'. Reaching it through the
            // controller is the reliable path.
            string err = null;
            object communitiesDataProvider = null;
            object listTask = null;
            try
            {
                if (mvcManager == null) { err = "communitycard: mvcManager null"; }
                else
                {
                    object cardController = FindControllerByTypeName(mvcManager, "CommunityCardController");
                    if (cardController == null) err = "communitycard: CommunityCardController not registered";
                    else
                    {
                        communitiesDataProvider = GetPrivateField(cardController, "communitiesDataProvider");
                        if (communitiesDataProvider == null) err = "communitycard: communitiesDataProvider field not found";
                        else
                        {
                            // GetUserCommunitiesAsync(name, onlyMemberOf, pageNumber, elementsPerPage, ct,
                            //                         includeRequestsReceivedPerCommunity=false, isStreaming=false)
                            // onlyMemberOf=false -> general discover list of REAL communities (those with content
                            // rank first), so the opened card is a populated, access-allowed community when available.
                            MethodInfo getCommunities = communitiesDataProvider.GetType()
                                .GetMethod("GetUserCommunitiesAsync", BindingFlags.Public | BindingFlags.Instance);
                            if (getCommunities == null) err = "communitycard: GetUserCommunitiesAsync not found";
                            else
                                listTask = getCommunities.Invoke(communitiesDataProvider, new object[]
                                {
                                    "", false, 1, 10, System.Threading.CancellationToken.None, false, false
                                });
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "communitycard: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }
            if (listTask == null) { m.error = "communitycard: list task null"; yield break; }

            // ---- Await the community list OUTSIDE any try ----
            yield return AwaitUniTask(listTask);
            if (awaitedError != null) { m.error = "communitycard: list fetch: " + awaitedError; yield break; }

            // ---- Phase 2: extract a community id from response.data.results[].id (DTO field is lowercase 'id') ----
            string communityId = null;
            try
            {
                object data = awaitedResult != null ? GetMember(awaitedResult, "data") : null;
                object results = data != null ? GetMember(data, "results") : null;
                if (results is System.Collections.IEnumerable enumerable)
                {
                    foreach (object community in enumerable)
                    {
                        object id = GetMember(community, "id");
                        string ids = id?.ToString();
                        if (!string.IsNullOrEmpty(ids)) { communityId = ids; break; }
                    }
                }
            }
            catch (System.Exception e) { err = "communitycard: id extract: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }
            if (string.IsNullOrEmpty(communityId))
            {
                // Data-gated: this account is in no communities, so there is no card to open. Capture whatever
                // renders so the run is still visually reviewable, then record the gap.
                for (int i = 0; i < 18; i++) yield return null;
                yield return CaptureShot("communitycard");
                m.error = "communitycard: skipped:no-community-id (account in no communities)";
                yield break;
            }

            // ---- Phase 3: IssueCommand(new CommunityCardParameter(communityId)) -> mvcManager.ShowAsync<TView,TInput> ----
            // Read-only display of an existing community's card; no join/leave/post/invite is performed.
            try
            {
                Type controllerType = FindType("DCL.Communities.CommunitiesCard.CommunityCardController");
                Type paramType = FindType("DCL.Communities.CommunitiesCard.CommunityCardParameter");
                if (controllerType == null || paramType == null) err = "communitycard: card types not found";
                else
                {
                    // CommunityCardParameter(string communityId, ISpriteCache? spriteCache = null) — struct
                    object param = Activator.CreateInstance(paramType, communityId, null);

                    MethodInfo issueCommand = controllerType.GetMethod("IssueCommand",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy,
                        null, new[] { paramType }, null);
                    if (issueCommand == null) err = "communitycard: IssueCommand(param) not found";
                    else
                    {
                        object command = issueCommand.Invoke(null, new[] { param });
                        if (command == null) err = "communitycard: IssueCommand returned null";
                        else
                        {
                            MethodInfo showAsync = null;
                            foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                            if (showAsync == null) err = "communitycard: ShowAsync not found";
                            else
                            {
                                Type[] genArgs = command.GetType().GetGenericArguments(); // [CommunityCardView, CommunityCardParameter]
                                showAsync.MakeGenericMethod(genArgs)
                                    .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });
                                Type ifaceOpen = FindType("MVC.IController`2");
                                lastPanelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(genArgs) : null;
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "communitycard: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // ---- Phase 4: CONTENT-LOAD POLL (outside any try) ----------------------------------------------
            // SetDefaultsAndLoadData() shows the main skeleton (loadingObject) + the default section skeleton,
            // then a chain of signed fetches (GetCommunityAsync, opt-out, invite/request, places) precedes
            // SetLoadingState(false). We poll the view's SkeletonLoadingView.loadingCanvasGroup.alpha: it is 1
            // while loading and tweens to 0 once HideLoading() runs. Wait until BOTH the main card skeleton and
            // the announcements-section skeleton are hidden, up to ~6s (360 frames @ ~60fps). Falls through to a
            // generous settle even if a flag can't be read, so REAL content (or the empty state) is captured.
            bool loaded = false;
            for (int i = 0; i < 360 && !loaded; i++)
            {
                float mainAlpha = 1f;
                float annAlpha = 1f;
                bool readMain = false;
                bool readAnn = false;
                try
                {
                    object cardController = FindControllerByTypeName(mvcManager, "CommunityCardController");
                    object viewInstance = cardController != null ? GetMember(cardController, "viewInstance") : null;
                    if (viewInstance != null)
                    {
                        // main card skeleton: private 'loadingObject' (SkeletonLoadingView)
                        object mainSkel = GetPrivateField(viewInstance, "loadingObject");
                        if (mainSkel != null)
                        {
                            object cg = GetPrivateField(mainSkel, "loadingCanvasGroup");
                            if (cg is UnityEngine.CanvasGroup mcg) { mainAlpha = mcg.alpha; readMain = true; }
                        }

                        // default-section skeleton: AnnouncementsSectionView.loadingObject (SkeletonLoadingView)
                        object annView = GetPublicProperty(viewInstance, "AnnouncementsSectionView")
                                         ?? GetPublicField(viewInstance, "AnnouncementsSectionView");
                        if (annView != null)
                        {
                            object annSkel = GetPrivateField(annView, "loadingObject");
                            if (annSkel != null)
                            {
                                object acg = GetPrivateField(annSkel, "loadingCanvasGroup");
                                if (acg is UnityEngine.CanvasGroup accg) { annAlpha = accg.alpha; readAnn = true; }
                            }
                        }
                    }
                }
                catch { /* tolerate a transient read failure; keep polling */ }

                // Loaded once the main skeleton is hidden AND (the announcements skeleton is hidden or not present).
                if (readMain && mainAlpha <= 0.05f && (!readAnn || annAlpha <= 0.05f))
                    loaded = true;
                else
                    yield return null;
            }

            // Generous settle for the loaded fade-in tween (DOFade 0.3s) + list item layout, even if loaded early.
            for (int i = 0; i < 60; i++) yield return null;

            // ---- Capture + verify (verify outside any try) ----
            yield return CaptureShot("communitycard");

            string verifyErr = "no panel key";
            bool shown = lastPanelKey != null && VerifyShown(mvcManager, lastPanelKey, out verifyErr);
            m.error = shown ? "shown" : ("not-shown: " + (verifyErr ?? "no panel key"));
        }

        // Unfriend confirmation popup: open the Friends panel, reach IFriendsService via the FriendList
        // request manager, fetch one friend, open their passport, then click RemoveFriendButton which only
        // SHOWS the UnfriendConfirmationPopup (non-destructive: the actual unfriend needs a separate confirm
        // we never click). NON-GATING; dataGated (needs a real friend on the test account).
        private static IEnumerator AtlasCapture_friendactions(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_friendactions", ok = true };
            report.actions.Add(m);

            var ctNone = System.Threading.CancellationToken.None;

            // STEP 1: open the Friends panel (FRIENDS tab) and locate the IFriendsService through its
            // section controller's request manager (the container does NOT store the service as a field).
            string err = null;
            object friendsService = null;
            try
            {
                Type panelControllerT = FindType("DCL.Friends.UI.FriendPanel.FriendsPanelController");
                Type panelParamT = FindType("DCL.Friends.UI.FriendPanel.FriendsPanelParameter");
                Type tabT = FindType("DCL.Friends.UI.FriendPanel.FriendsPanelController+FriendsPanelTab");
                if (panelControllerT == null || panelParamT == null || tabT == null)
                {
                    err = "friendactions: Friends panel types not found";
                }
                else
                {
                    object friendsTab = System.Enum.Parse(tabT, "FRIENDS");
                    object panelParam = System.Activator.CreateInstance(panelParamT, new object[] { friendsTab });
                    if (!TryShowPanelByName(mvcManager, "DCL.Friends.UI.FriendPanel.FriendsPanelController", panelParam, out err))
                    {
                        // keep err; still try to read the service below
                    }
                }
            }
            catch (System.Exception e) { err = "friendactions: open-friends failed: " + (e.InnerException?.Message ?? e.Message); }

            // let the panel instantiate before we reach into its controller graph
            for (int i = 0; i < 18; i++) yield return null;

            // STEP 1b: walk FriendsPanelController -> (friendSectionController | friendSectionControllerConnectivity)
            // -> requestManager -> friendsService. Synchronous reflection only (no yields here).
            try
            {
                object panelController = FindControllerByTypeName(mvcManager, "FriendsPanelController");
                if (panelController != null)
                {
                    object section = GetPrivateField(panelController, "friendSectionController")
                                  ?? GetPrivateField(panelController, "friendSectionControllerConnectivity");
                    if (section != null)
                    {
                        object requestManager = GetPrivateField(section, "requestManager");
                        if (requestManager != null)
                            friendsService = GetPrivateField(requestManager, "friendsService");
                    }
                }
            }
            catch (System.Exception e) { if (err == null) err = "friendactions: service-lookup failed: " + (e.InnerException?.Message ?? e.Message); }

            // If we cannot reach the service, capture the friends panel (whatever rendered) and stop.
            if (friendsService == null)
            {
                m.error = "friendactions: IFriendsService not reachable" + (err != null ? " (" + err + ")" : "") + "; captured friends panel only";
                for (int i = 0; i < 6; i++) yield return null;
                yield return CaptureShot("friendactions");
                yield break;
            }

            // STEP 2: fetch one friend (GetFriendsAsync(pageNum, pageSize, ct)). Invoke is synchronous;
            // the returned UniTask is awaited OUTSIDE any try.
            object friendsTask = null;
            try
            {
                System.Reflection.MethodInfo getFriends = friendsService.GetType().GetMethod(
                    "GetFriendsAsync",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null,
                    new[] { typeof(int), typeof(int), typeof(System.Threading.CancellationToken) },
                    null);
                if (getFriends == null) err = "friendactions: GetFriendsAsync not found";
                else friendsTask = getFriends.Invoke(friendsService, new object[] { 0, 1, ctNone });
            }
            catch (System.Exception e) { err = "friendactions: GetFriendsAsync invoke failed: " + (e.InnerException?.Message ?? e.Message); }

            if (friendsTask == null)
            {
                m.error = err ?? "friendactions: GetFriendsAsync returned null";
                yield return CaptureShot("friendactions");
                yield break;
            }

            yield return AwaitUniTask(friendsTask);
            if (awaitedError != null)
            {
                m.error = "friendactions: GetFriendsAsync failed: " + awaitedError + " (dataGated)";
                yield return CaptureShot("friendactions");
                yield break;
            }

            // STEP 2b: extract the first friend's UserId from PaginatedFriendsResult.Friends (CompactInfo.UserId).
            string friendUserId = null;
            try
            {
                if (awaitedResult != null)
                {
                    object friendsList = GetPublicProperty(awaitedResult, "Friends");
                    if (friendsList is System.Collections.IEnumerable en)
                    {
                        var it = en.GetEnumerator();
                        if (it.MoveNext() && it.Current != null)
                        {
                            object uid = GetMember(it.Current, "UserId");
                            friendUserId = uid != null ? uid.ToString() : null;
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "friendactions: friend-extract failed: " + (e.InnerException?.Message ?? e.Message); }

            if (string.IsNullOrEmpty(friendUserId))
            {
                m.error = "friendactions: no friends available (dataGated:no-friends); captured friends panel only";
                yield return CaptureShot("friendactions");
                yield break;
            }

            // STEP 3: open the friend's passport. PassportController.IssueCommand(PassportParams) -> ShowAsync.
            try
            {
                Type passportParamsT = FindType("DCL.Passport.PassportParams");
                if (passportParamsT == null) err = "friendactions: PassportParams not found";
                else
                {
                    object passportParams = System.Activator.CreateInstance(passportParamsT, new object[] { friendUserId, null, false });
                    if (!TryShowPanelByName(mvcManager, "DCL.Passport.PassportController", passportParams, out err))
                    {
                        // keep err
                    }
                }
            }
            catch (System.Exception e) { err = "friendactions: passport-open failed: " + (e.InnerException?.Message ?? e.Message); }

            // settle so the passport view + friend-interaction buttons populate
            for (int i = 0; i < 18; i++) yield return null;

            // STEP 4: click RemoveFriendButton -> opens the UnfriendConfirmationPopup (NON-destructive;
            // the real unfriend needs a confirm we never click). Synchronous reflection only.
            bool clicked = false;
            try
            {
                object passportController = FindControllerByTypeName(mvcManager, "PassportController");
                if (passportController != null)
                {
                    // viewInstance is a protected PROPERTY on ControllerBase -> use GetMember (property-then-field).
                    object viewInstance = GetMember(passportController, "viewInstance");
                    if (viewInstance != null)
                    {
                        object removeFriendButton = GetPublicProperty(viewInstance, "RemoveFriendButton");
                        if (removeFriendButton != null)
                        {
                            object onClick = GetPublicProperty(removeFriendButton, "onClick");
                            if (onClick != null)
                            {
                                System.Reflection.MethodInfo invoke = onClick.GetType().GetMethod(
                                    "Invoke", System.Type.EmptyTypes);
                                if (invoke != null) { invoke.Invoke(onClick, null); clicked = true; }
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "friendactions: remove-friend-click failed: " + (e.InnerException?.Message ?? e.Message); }

            // settle so the confirmation popup renders
            for (int i = 0; i < 18; i++) yield return null;
            yield return CaptureShot("friendactions");
            m.error = clicked ? "shown" : ("friendactions: passport shown, unfriend-confirm not triggered" + (err != null ? " (" + err + ")" : ""));
        }

        // Open a Community Card and switch to its MEMBERS section, then screenshot a FULLY-LOADED
        // members list (not the ANNOUNCEMENTS tab the card opens on). Read-only: discovers a community
        // id via CommunitiesDataProvider.GetUserCommunitiesAsync, opens the card (IssueCommand->ShowAsync),
        // POLLS until the card's own communityData has finished loading, then WAITS for the controller's
        // post-load ResetToggle(true) to land on ANNOUNCEMENTS (the view's private currentSection turns
        // non-null) BEFORE driving the view's private ToggleSection(Sections.MEMBERS). This ordering is
        // the actual fix: ConfigureCommunity's LoadCommunityDataAsync sets communityData (line ~471)
        // several awaits BEFORE it calls ResetToggle(true) (line ~497). If we switched to MEMBERS the
        // instant communityData appeared, ResetToggle would later overwrite us back to ANNOUNCEMENTS
        // (the observed S08 bug). ToggleSection(MEMBERS) both highlights the MEMBERS tab AND raises
        // SectionChanged -> OnSectionChanged -> MembersListController.ShowMembersList. We then POLL the
        // members list's protected 'isFetching' flag (CommunityFetchingControllerBase) until the member
        // fetch completes, and RE-ASSERT MEMBERS right before the shot as a safety net. No
        // join/leave/post/invite -> no noise. Data-gated: needs at least one community for the session
        // identity; otherwise captures whatever renders (ok stays true regardless).
        private static IEnumerator AtlasCapture_communitymembers(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_communitymembers", ok = true };  // NON-GATING
            report.actions.Add(m);
            yield return HideExplorePanel(mvcManager);  // hide the persistent Explore/Communities grid bleeding behind this POPUP card (matches communitycontent)

            string err = null;
            object dataProvider = null;   // CommunitiesDataProvider instance (reflected)
            object listTask = null;       // UniTask<GetUserCommunitiesResponse>

            // PHASE 1 (sync reflection only — NO yield in here): locate a CommunitiesDataProvider on
            // any already-registered MVC controller, then kick off the user-communities query.
            try
            {
                if (mvcManager == null) { err = "communitymembers: mvcManager null"; }
                else
                {
                    object dict = GetPublicProperty(mvcManager, "Controllers");
                    if (dict == null)
                    {
                        object core = GetPrivateField(mvcManager, "core");
                        if (core != null) dict = GetPublicProperty(core, "Controllers");
                    }
                    object values = dict != null ? GetPublicProperty(dict, "Values") : null;
                    if (values is System.Collections.IEnumerable en)
                    {
                        foreach (object ctrl in en)
                        {
                            if (ctrl == null) continue;
                            foreach (FieldInfo fi in ctrl.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
                            {
                                object fv = fi.GetValue(ctrl);
                                if (fv != null && fv.GetType().Name == "CommunitiesDataProvider") { dataProvider = fv; break; }
                            }
                            if (dataProvider != null) break;
                        }
                    }

                    if (dataProvider == null)
                        err = "communitymembers: skipped:no-CommunitiesDataProvider-reachable (no registered controller holds it yet)";
                    else
                    {
                        // GetUserCommunitiesAsync(string name, bool onlyMemberOf, int pageNumber,
                        //   int elementsPerPage, CancellationToken ct, bool includeRequests=false, bool isStreaming=false)
                        var ct = System.Threading.CancellationToken.None;
                        MethodInfo mi = null;
                        foreach (MethodInfo c in dataProvider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                            if (c.Name == "GetUserCommunitiesAsync") { mi = c; break; }
                        if (mi == null) err = "communitymembers: GetUserCommunitiesAsync not found";
                        else
                        {
                            ParameterInfo[] ps = mi.GetParameters();
                            object[] args = new object[ps.Length];
                            for (int i = 0; i < ps.Length; i++)
                            {
                                if (ps[i].ParameterType == typeof(string)) args[i] = "";
                                else if (ps[i].ParameterType == typeof(bool)) args[i] = false;       // onlyMemberOf=false -> any community
                                else if (ps[i].ParameterType == typeof(int)) args[i] = (ps[i].Name == "pageNumber") ? 1 : 20;
                                else if (ps[i].ParameterType == typeof(System.Threading.CancellationToken)) args[i] = ct;
                                else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                            }
                            listTask = mi.Invoke(dataProvider, args);
                            if (listTask == null) err = "communitymembers: GetUserCommunitiesAsync returned null";
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "communitymembers: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Await the query OUTSIDE any try.
            yield return AwaitUniTask(listTask);
            if (awaitedError != null) { m.error = "communitymembers: list-failed: " + awaitedError; yield break; }

            // PHASE 2 (sync reflection only): pull the first community id out of the response and
            // build the card command. Response shape: GetUserCommunitiesResponse.data.results[].id
            // (Result<T> wrapper unwrapped if present via a 'Value'/'Success' member).
            string communityId = null;
            object command = null;
            Type[] genArgs = null;
            try
            {
                object resp = awaitedResult;
                // Result<GetUserCommunitiesResponse> may wrap the payload: unwrap a likely 'Value' member.
                object payload = resp;
                object maybeValue = resp != null ? GetMember(resp, "Value") : null;
                if (maybeValue != null && maybeValue.GetType().Name.Contains("Response")) payload = maybeValue;

                object data = payload != null ? GetMember(payload, "data") : null;
                object results = data != null ? GetMember(data, "results") : null;
                if (results is System.Collections.IEnumerable ren)
                {
                    foreach (object comm in ren)
                    {
                        communityId = GetMember(comm, "id") as string;   // 'id' is a public field
                        if (!string.IsNullOrEmpty(communityId)) break;
                    }
                }

                if (string.IsNullOrEmpty(communityId))
                    err = "communitymembers: dataGated:no-community-in-results";
                else
                {
                    Type paramType = FindType("DCL.Communities.CommunitiesCard.CommunityCardParameter");
                    Type controllerType = FindType("DCL.Communities.CommunitiesCard.CommunityCardController");
                    if (paramType == null || controllerType == null) err = "communitymembers: card types not found";
                    else
                    {
                        // CommunityCardParameter(string communityId, ISpriteCache spriteCache = null)
                        object param = System.Activator.CreateInstance(paramType, new object[] { communityId, null });

                        // Static IssueCommand(CommunityCardParameter) inherited from ControllerBase.
                        MethodInfo issue = null;
                        foreach (MethodInfo c in controllerType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                            if (c.Name == "IssueCommand" && c.GetParameters().Length == 1) { issue = c; break; }
                        if (issue == null) err = "communitymembers: IssueCommand(param) not found";
                        else
                        {
                            command = issue.Invoke(null, new[] { param });
                            if (command == null) err = "communitymembers: IssueCommand returned null";
                            else
                            {
                                genArgs = command.GetType().GetGenericArguments();
                                MethodInfo showAsync = null;
                                foreach (MethodInfo c in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                    if (c.Name == "ShowAsync" && c.IsGenericMethodDefinition) { showAsync = c; break; }
                                if (showAsync == null) err = "communitymembers: ShowAsync not found";
                                else
                                    showAsync.MakeGenericMethod(genArgs)
                                             .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "communitymembers: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // POLL 1 (OUTSIDE try): wait for the card controller to finish its own GetCommunityAsync
            // load. communityData (a private CommunityData struct field on the controller) gets a
            // non-empty 'id' once getCommunityResult.Value.data is assigned. Up to ~8s (480 frames).
            object cardController = null;
            for (int i = 0; i < 480; i++)
            {
                bool loaded = false;
                try
                {
                    if (cardController == null) cardController = FindControllerByTypeName(mvcManager, "CommunityCardController");
                    object cd = cardController != null ? GetPrivateField(cardController, "communityData") : null;
                    // communityData is a struct (CommunityData); once set it has a non-empty 'id'.
                    object cdId = cd != null ? GetMember(cd, "id") : null;
                    loaded = cardController != null && !string.IsNullOrEmpty(cdId as string);
                }
                catch { loaded = false; }
                if (loaded) break;
                yield return null;
            }

            // Verify the card is shown (non-gating: ok stays true; just annotate on failure).
            Type ifaceOpen = FindType("MVC.IController`2");
            lastPanelKey = (ifaceOpen != null && genArgs != null) ? ifaceOpen.MakeGenericType(genArgs) : null;
            if (lastPanelKey != null && !VerifyShown(mvcManager, lastPanelKey, out string verr))
            { m.error = "communitymembers: not-shown (" + verr + ")"; yield return CaptureShot("communitymembers"); yield break; }

            // POLL 2 (OUTSIDE try): wait for the controller's post-load ResetToggle(true) to land on a
            // section. LoadCommunityDataAsync sets communityData several awaits BEFORE it calls
            // viewInstance.ResetToggle(true) (which sets the view's private nullable 'currentSection',
            // normally to ANNOUNCEMENTS). We must let that fire FIRST; otherwise our MEMBERS switch is
            // clobbered back to ANNOUNCEMENTS. Wait until currentSection becomes non-null, then a few
            // extra frames, up to ~5s (300 frames). If unreadable, a fixed settle below still applies.
            object viewInst = null;
            for (int i = 0; i < 300; i++)
            {
                bool toggled = false;
                try
                {
                    if (cardController == null) cardController = FindControllerByTypeName(mvcManager, "CommunityCardController");
                    if (viewInst == null) viewInst = cardController != null ? GetMember(cardController, "viewInstance") : null;
                    object cs = viewInst != null ? GetPrivateField(viewInst, "currentSection") : null;
                    toggled = cs != null;   // nullable Sections? has become non-null -> ResetToggle ran
                }
                catch { toggled = false; }
                if (toggled) break;
                yield return null;
            }
            // small extra settle so any follow-up post-load work after ResetToggle has quiesced.
            for (int i = 0; i < 30; i++) yield return null;

            // PHASE 3 (sync reflection only): switch the card to its MEMBERS section by invoking the
            // view's private ToggleSection(Sections.MEMBERS, invokeEvent:true). ToggleSection updates
            // the tab-selection visuals (MEMBERS highlighted, ANNOUNCEMENTS dimmed) AND raises
            // SectionChanged -> OnSectionChanged -> MembersListController.ShowMembersList. Read-only nav.
            object membersCtrl = null;     // MembersListController (to poll its isFetching flag)
            object sectionsEnumMembers = null;   // cached Sections.MEMBERS for the re-assert step
            MethodInfo toggleMethod = null;      // cached ToggleSection for the re-assert step
            try
            {
                if (cardController == null) cardController = FindControllerByTypeName(mvcManager, "CommunityCardController");
                membersCtrl = cardController != null ? GetPrivateField(cardController, "membersListController") : null;

                if (viewInst == null) viewInst = cardController != null ? GetMember(cardController, "viewInstance") : null;
                if (viewInst != null)
                {
                    Type sectionsEnum = FindType("DCL.Communities.CommunitiesCard.CommunityCardView+Sections");
                    sectionsEnumMembers = sectionsEnum != null ? System.Enum.Parse(sectionsEnum, "MEMBERS") : null;
                    if (sectionsEnumMembers != null)
                    {
                        foreach (MethodInfo c in viewInst.GetType().GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
                            if (c.Name == "ToggleSection") { toggleMethod = c; break; }
                        if (toggleMethod != null)
                        {
                            // ToggleSection(Sections section, bool invokeEvent = true): first param is the
                            // MEMBERS enum, any trailing bool forced true so SectionChanged fires.
                            ParameterInfo[] tp = toggleMethod.GetParameters();
                            object[] targs = new object[tp.Length];
                            targs[0] = sectionsEnumMembers;
                            for (int i = 1; i < tp.Length; i++)
                                targs[i] = (tp[i].ParameterType == typeof(bool)) ? (object)true
                                          : (tp[i].HasDefaultValue ? tp[i].DefaultValue : null);
                            toggleMethod.Invoke(viewInst, targs);
                        }
                        else
                        {
                            // Fallback: fire SectionChanged event directly (loads data, may not move tab visual).
                            FieldInfo evtField = viewInst.GetType().GetField("SectionChanged",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            object evtDelegate = evtField != null ? evtField.GetValue(viewInst) : null;
                            if (evtDelegate is System.Delegate del) del.DynamicInvoke(sectionsEnumMembers);
                        }
                    }
                }
            }
            catch { /* section switch is best-effort; card itself still captured */ }

            // POLL 3 (OUTSIDE try): wait for the members fetch to complete. MembersListController
            // (CommunityFetchingControllerBase) sets protected 'isFetching'=true during the fetch and
            // back to false when done. Wait until it has gone true and returned to false (data
            // populated), then a few extra frames for the grid to lay out. Up to ~8s (480 frames),
            // with a generous fixed fallback if the flag is unreadable.
            if (membersCtrl == null)
            {
                if (cardController == null) cardController = FindControllerByTypeName(mvcManager, "CommunityCardController");
                try { membersCtrl = cardController != null ? GetPrivateField(cardController, "membersListController") : null; }
                catch { membersCtrl = null; }
            }

            bool sawFetching = false;
            int settled = 0;
            for (int i = 0; i < 480; i++)
            {
                bool fetching = false;
                bool readable = false;
                try
                {
                    object f = membersCtrl != null ? GetPrivateField(membersCtrl, "isFetching") : null;
                    if (f is bool fb) { fetching = fb; readable = true; }
                }
                catch { readable = false; }

                if (!readable) break;          // can't read flag -> fall through to fixed settle
                if (fetching) sawFetching = true;
                if (sawFetching && !fetching)  // fetch started and finished
                {
                    settled++;
                    if (settled >= 24) break;  // let the grid render after fetch completes
                }
                yield return null;
            }

            // RE-ASSERT MEMBERS (sync reflection only, OUTSIDE the fetch loop): defensive — if any
            // late post-load work toggled the section back, re-select MEMBERS. The view's
            // 'if (section == currentSection) return;' guard makes this a no-op when already on MEMBERS.
            try
            {
                if (toggleMethod != null && viewInst != null && sectionsEnumMembers != null)
                {
                    object cs = GetPrivateField(viewInst, "currentSection");
                    bool onMembers = cs != null && cs.ToString() == "MEMBERS";
                    if (!onMembers)
                    {
                        ParameterInfo[] tp = toggleMethod.GetParameters();
                        object[] targs = new object[tp.Length];
                        targs[0] = sectionsEnumMembers;
                        for (int i = 1; i < tp.Length; i++)
                            targs[i] = (tp[i].ParameterType == typeof(bool)) ? (object)true
                                      : (tp[i].HasDefaultValue ? tp[i].DefaultValue : null);
                        toggleMethod.Invoke(viewInst, targs);
                    }
                }
            }
            catch { /* best-effort */ }

            // Generous fixed settle to guarantee the grid/thumbnails paint even if the flag never
            // toggled in time (e.g. cached data, or unreadable flag).
            for (int i = 0; i < 120; i++) yield return null;

            // FINAL RE-ASSERT MEMBERS (sync reflection, immediately before capture): the earlier
            // re-assert ran BEFORE this 120-frame settle, so a late ResetToggle(true) (which forces
            // ANNOUNCEMENTS — CommunityCardController.cs:499 -> CommunityCardView.ResetToggle ->
            // ToggleSection, CommunityCardView.cs:299-302) during the settle can clobber the section
            // back to ANNOUNCEMENTS (observed: the captured card still highlighted ANNOUNCEMENTS). Toggle
            // MEMBERS one last time at the very end; the view's 'if (section == currentSection) return;'
            // guard (CommunityCardView.cs:330) makes this a no-op when already on MEMBERS, so it is safe.
            try
            {
                if (toggleMethod != null && viewInst != null && sectionsEnumMembers != null)
                {
                    object cs = GetPrivateField(viewInst, "currentSection");
                    bool onMembers = cs != null && cs.ToString() == "MEMBERS";
                    if (!onMembers)
                    {
                        ParameterInfo[] tp = toggleMethod.GetParameters();
                        object[] targs = new object[tp.Length];
                        targs[0] = sectionsEnumMembers;
                        for (int i = 1; i < tp.Length; i++)
                            targs[i] = (tp[i].ParameterType == typeof(bool)) ? (object)true
                                      : (tp[i].HasDefaultValue ? tp[i].DefaultValue : null);
                        toggleMethod.Invoke(viewInst, targs);
                    }
                }
            }
            catch { /* best-effort final assert */ }
            // brief settle so the MEMBERS tab visual swap + list lays out before the shot
            for (int i = 0; i < 24; i++) yield return null;

            yield return CaptureShot("communitymembers");
            m.error = "shown";                                          // sentinel meaning success
        }

        // Open the CommunityCard panel for a live community, WAIT for the header data to finish loading (skeleton bars gone),
        // then switch to the PLACES content section, let it settle, and capture a fully-loaded card.
        // READ-ONLY: fetches the user's community list (signed GET) and opens/views a card. No join/leave/post/create -> no noise.
        private static IEnumerator AtlasCapture_communitycontent(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_communitycontent", ok = true };
            report.actions.Add(m);
            // leftover-panel isolation: the previously-opened ExplorePanel (Communities grid + Events
            // "Upcoming Events" overlay) is a PERSISTENT MVC view that CloseOpenPanels never closes, so its
            // full-screen grid + its own close button bleed behind this POPUP-layer CommunityCard (the stray
            // Upcoming-Events overlay + the second close button). Hard-hide it like inputsuggestions does
            // (DclPlaytestHarness.cs:4386). Covers every capture path below, incl. the early data-gated yields.
            yield return HideExplorePanel(mvcManager);

            string err = null;
            object communitiesDataProvider = null;
            object listTask = null;

            // Step 1 (sync setup): locate a CommunitiesDataProvider via an already-registered controller.
            // It is NOT a field of dynamicWorldContainer; it lives as a private field on the communities
            // controllers (CommunitiesBrowserController.dataProvider / CommunityCardController.communitiesDataProvider).
            try
            {
                object browser = FindControllerByTypeName(mvcManager, "CommunitiesBrowserController");
                if (browser != null) communitiesDataProvider = GetPrivateField(browser, "dataProvider");
                if (communitiesDataProvider == null)
                {
                    object card = FindControllerByTypeName(mvcManager, "CommunityCardController");
                    if (card != null) communitiesDataProvider = GetPrivateField(card, "communitiesDataProvider");
                }

                if (communitiesDataProvider != null)
                {
                    // UniTask<GetUserCommunitiesResponse> GetUserCommunitiesAsync(name, onlyMemberOf, pageNumber, elementsPerPage, ct, ...)
                    // pageNumber starts at 1 (the URL computes offset = page*per - per; page 0 -> negative offset).
                    MethodInfo getCommunities = communitiesDataProvider.GetType()
                        .GetMethod("GetUserCommunitiesAsync", BindingFlags.Public | BindingFlags.Instance);
                    if (getCommunities != null)
                    {
                        ParameterInfo[] ps = getCommunities.GetParameters();
                        object[] args = new object[ps.Length];
                        args[0] = "";    // name = all
                        args[1] = false; // onlyMemberOf = false (broaden -> a public community renders even if user is in none)
                        args[2] = 1;     // pageNumber
                        args[3] = 10;    // elementsPerPage
                        args[4] = System.Threading.CancellationToken.None;
                        for (int i = 5; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                        listTask = getCommunities.Invoke(communitiesDataProvider, args);
                    }
                }
            }
            catch (System.Exception e) { err = "communitycontent: provider lookup: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield return CaptureShot("communitycontent"); yield break; }

            // Step 2 (await OUTSIDE try): drive the list fetch if we have a task.
            string communityId = null;
            if (listTask != null)
            {
                yield return AwaitUniTask(listTask);
                if (awaitedError == null && awaitedResult != null)
                {
                    // GetUserCommunitiesResponse.data (field) -> GetUserCommunitiesData.results (CommunityData[]) -> .id (field)
                    try
                    {
                        object data = GetMember(awaitedResult, "data");
                        object results = data != null ? GetMember(data, "results") : null;
                        if (results is System.Collections.IEnumerable en)
                            foreach (object c in en)
                            {
                                object id = GetMember(c, "id");
                                if (id != null && !string.IsNullOrEmpty(id.ToString())) { communityId = id.ToString(); break; }
                            }
                    }
                    catch (System.Exception e) { err = "communitycontent: parse list: " + (e.InnerException?.Message ?? e.Message); }
                }
                if (err != null) { m.error = err; yield return CaptureShot("communitycontent"); yield break; }
            }

            // No community available (empty account / no public communities) -> capture whatever renders.
            if (string.IsNullOrEmpty(communityId))
            {
                m.error = "skipped:no-community-available (data-gated: no community id from GetUserCommunitiesAsync)";
                for (int i = 0; i < 18; i++) yield return null;
                yield return CaptureShot("communitycontent");
                yield break;
            }

            // Step 3 (sync setup): build CommunityCardParameter(communityId, spriteCache:null).
            object param = null;
            try
            {
                Type paramType = FindType("DCL.Communities.CommunitiesCard.CommunityCardParameter");
                if (paramType == null) err = "communitycontent: CommunityCardParameter type not found";
                else
                {
                    ConstructorInfo ctor = null;
                    foreach (ConstructorInfo c in paramType.GetConstructors())
                        if (c.GetParameters().Length >= 1 && c.GetParameters()[0].ParameterType == typeof(string)) { ctor = c; break; }
                    if (ctor == null) err = "communitycontent: CommunityCardParameter(string,...) ctor not found";
                    else
                    {
                        ParameterInfo[] cp = ctor.GetParameters();
                        object[] cargs = new object[cp.Length];
                        cargs[0] = communityId;
                        for (int i = 1; i < cp.Length; i++) cargs[i] = cp[i].HasDefaultValue ? cp[i].DefaultValue : null;
                        param = ctor.Invoke(cargs);
                    }
                }
            }
            catch (System.Exception e) { err = "communitycontent: build param: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield return CaptureShot("communitycontent"); yield break; }

            // Step 4 (sync): show the panel via the standard IssueCommand -> ShowAsync path.
            if (!TryShowPanelByName(mvcManager, "DCL.Communities.CommunitiesCard.CommunityCardController", param, out string showErr))
            {
                m.error = "communitycontent: show panel failed: " + showErr;
                yield return CaptureShot("communitycontent");
                yield break;
            }

            // Step 5 (resolve the view): viewInstance is a PROTECTED auto-property on ControllerBase whose value IS the
            // CommunityCardView (TView). Walk the type hierarchy to read it. communityName is a private TMP_Text field on the
            // view: while loading it is disabled with empty text; SetLoadingState(false) (after GetCommunityAsync resolves)
            // re-enables it and ConfigureCommunity fills the text -> our "header fully loaded, skeleton gone" signal.
            object cardView = null;
            try
            {
                object controller = FindControllerByTypeName(mvcManager, "CommunityCardController");
                for (Type t = controller?.GetType(); t != null && cardView == null; t = t.BaseType)
                {
                    PropertyInfo vp = t.GetProperty("viewInstance", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    if (vp != null) cardView = vp.GetValue(controller);
                }
            }
            catch (System.Exception) { cardView = null; }

            // Step 6 (load POLL, OUTSIDE try): wait up to ~6s for the community header to finish loading.
            // The skeleton (loadingObject) is up while communityName is disabled + empty; once data lands the text
            // becomes non-empty AND the TMP_Text is re-enabled. Poll both via reflection inside a tiny no-yield try.
            bool headerLoaded = false;
            for (int i = 0; i < 360 && !headerLoaded; i++)
            {
                try
                {
                    if (cardView != null)
                    {
                        // communityName is `[field: SerializeField] private TMP_Text communityName { get; set; }`
                        // -> reach it via the private auto-property, then fall back to its compiler backing field.
                        object nameText = null;
                        PropertyInfo nameProp = cardView.GetType().GetProperty("communityName",
                            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                        if (nameProp != null) nameText = nameProp.GetValue(cardView);
                        if (nameText == null)
                        {
                            FieldInfo nameField = cardView.GetType().GetField("<communityName>k__BackingField",
                                BindingFlags.NonPublic | BindingFlags.Instance);
                            if (nameField != null) nameText = nameField.GetValue(cardView);
                        }
                        if (nameText != null)
                        {
                            string txt = nameText.GetType().GetProperty("text")?.GetValue(nameText) as string;
                            object enabledObj = nameText.GetType().GetProperty("enabled")?.GetValue(nameText);
                            bool enabled = enabledObj is bool b && b;
                            if (enabled && !string.IsNullOrEmpty(txt)) headerLoaded = true;
                        }
                    }
                }
                catch (System.Exception) { /* keep polling; treat as not-yet-loaded */ }
                if (!headerLoaded) yield return null;
            }

            // Give the default section + thumbnails a few frames after the header lands.
            for (int i = 0; i < 24; i++) yield return null;

            // Step 7 (sync, best-effort): switch the view to the PLACES content section (has renderable list data).
            // ToggleSection(Sections, bool) is PRIVATE on the view. Failure here is non-fatal (card still captured).
            try
            {
                Type sectionsEnum = FindType("DCL.Communities.CommunitiesCard.CommunityCardView+Sections");
                if (cardView != null && sectionsEnum != null)
                {
                    object placesSection = System.Enum.Parse(sectionsEnum, "PLACES");
                    MethodInfo toggle = cardView.GetType().GetMethod("ToggleSection",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null, new[] { sectionsEnum, typeof(bool) }, null);
                    if (toggle != null)
                        toggle.Invoke(cardView, new object[] { placesSection, true });
                }
            }
            catch (System.Exception) { /* non-fatal: PLACES toggle is best-effort; the card itself is captured */ }

            // Step 8: settle (let the section's list/thumbnails finish) and capture.
            for (int i = 0; i < 90; i++) yield return null;
            yield return CaptureShot("communitycontent");
            m.error = headerLoaded ? "shown" : "shown:degraded-header-load-timeout";
        }

        // Open own Passport, switch to the Photos section, and screenshot the camera-reel gallery (read-only: own profile only).
        private static IEnumerator AtlasCapture_passportphotos(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_passportphotos", ok = true };
            report.actions.Add(m);
            // leftover-panel isolation: the previously-opened ExplorePanel (Communities grid) is a PERSISTENT
            // MVC view that CloseOpenPanels never closes, so it bleeds behind this POPUP-layer Passport (the
            // leftover Communities panel + its second close button on the left edge). Hard-hide it the same way
            // inputsuggestions does (DclPlaytestHarness.cs:4386). NOTE: the empty "no photos yet" gallery is a
            // separate needs-data condition (no saved camera-reel photos) and is NOT addressed here.
            yield return HideExplorePanel(mvcManager);

            string err = null;
            object selfProfile = null;
            string userId = null;
            object profileTask = null;   // UniTask<Profile?> if we need to await

            // --- Phase 1: resolve own userId synchronously (cached OwnProfile path). No yields here. ---
            try
            {
                // SelfProfile moved to profileContainer.SelfProfile (#8996); ReachSelfProfile chains old->new.
                selfProfile = ReachSelfProfile(dynamicContainer);
                if (selfProfile != null)
                {
                    object ownProfile = GetPublicProperty(selfProfile, "OwnProfile"); // SelfProfile.OwnProfile : Profile?
                    if (ownProfile != null)
                        userId = GetPublicProperty(ownProfile, "UserId") as string; // Profile.UserId : string

                    // Not cached yet -> prepare the async fetch to await outside the try.
                    if (string.IsNullOrEmpty(userId))
                    {
                        MethodInfo profileAsync = selfProfile.GetType()
                            .GetMethod("ProfileAsync", BindingFlags.Public | BindingFlags.Instance);
                        if (profileAsync != null)
                            profileTask = profileAsync.Invoke(selfProfile, new object[] { System.Threading.CancellationToken.None });
                    }
                }
            }
            catch (System.Exception e) { err = "passportphotos: identity-setup: " + (e.InnerException?.Message ?? e.Message); }
            // Non-fatal: missing identity still lets us open the panel (it just may render empty). Do not yield-break.

            // --- Phase 2: await the profile fetch if needed (OUTSIDE any try). ---
            if (string.IsNullOrEmpty(userId) && profileTask != null)
            {
                yield return AwaitUniTask(profileTask);
                if (awaitedError == null && awaitedResult != null)
                    userId = GetPublicProperty(awaitedResult, "UserId") as string;
            }

            // --- Phase 3: build PassportParams + show the Passport controller (own profile). No yields here. ---
            bool opened = false;
            string openErr = null;
            try
            {
                Type paramT = FindType("DCL.Passport.PassportParams");
                if (paramT != null && !string.IsNullOrEmpty(userId))
                {
                    // PassportParams(string userId, string? badgeIdSelected = null, bool isOwnProfile = false)
                    object param = Activator.CreateInstance(paramT, userId, null, true);
                    opened = TryShowPanelByName(mvcManager, "DCL.Passport.PassportController", param, out openErr);
                }
                else if (paramT == null)
                    openErr = "PassportParams type not found";
                else
                    openErr = "own userId unavailable";
            }
            catch (System.Exception e) { openErr = e.InnerException?.Message ?? e.Message; }
            if (openErr != null) err = (err == null ? "passportphotos: open: " + openErr : err + "; open: " + openErr);

            // Let the Passport view instantiate and OnViewShow run (default section = OVERVIEW).
            for (int i = 0; i < 18; i++) yield return null;

            // --- Phase 4: invoke private OpenPhotosSection() to switch into the Photos tab. No yields here. ---
            try
            {
                object controller = FindControllerByTypeName(mvcManager, "PassportController");
                if (controller != null)
                {
                    MethodInfo openPhotos = controller.GetType()
                        .GetMethod("OpenPhotosSection", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (openPhotos != null)
                        openPhotos.Invoke(controller, null);
                    // If the method is missing we still capture whatever the panel renders.
                }
            }
            catch (System.Exception e)
            {
                string phErr = e.InnerException?.Message ?? e.Message;
                err = (err == null ? "passportphotos: photos-section: " + phErr : err + "; photos-section: " + phErr);
            }

            // Wait for CameraReelGalleryController.ShowWalletGalleryAsync (storage-info + paginated thumbnails) to render.
            for (int i = 0; i < 40; i++) yield return null;

            yield return CaptureShot("passportphotos");

            // Non-gating: report whatever happened but always treat as "shown" for the atlas.
            m.error = err == null ? "shown" : ("shown; " + err);
        }

        // Open the local user's OWN passport (isOwnProfile:true), then force-show the "Add link" editor modal.
        // The AddLink modal is a MonoBehaviour exposed as PassportView.AddLinkModal; its public Show() just clears the
        // two TMP input fields and SetActive(true) on the modal GameObject -> NO network, NO save (OnSave never fires).
        // Best-effort: click the LinksEditionButton first so the links section is in edit mode behind the modal.
        // NO NOISE: capture-only. We never invoke Save / OnSave / UpdateProfileAsync, so nothing is published.
        private static IEnumerator AtlasCapture_addlink(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_addlink", ok = true };
            report.actions.Add(m);

            // --- Phase 1: resolve own userId synchronously (cached OwnProfile path). No yields here. ---
            string err = null;
            object selfProfile = null;
            string userId = null;
            object profileTask = null;
            try
            {
                selfProfile = ReachSelfProfile(dynamicContainer);
                if (selfProfile != null)
                {
                    object ownProfile = GetPublicProperty(selfProfile, "OwnProfile");
                    if (ownProfile != null)
                        userId = GetPublicProperty(ownProfile, "UserId") as string;

                    if (string.IsNullOrEmpty(userId))
                    {
                        MethodInfo profileAsync = selfProfile.GetType()
                            .GetMethod("ProfileAsync", BindingFlags.Public | BindingFlags.Instance);
                        if (profileAsync != null)
                            profileTask = profileAsync.Invoke(selfProfile, new object[] { System.Threading.CancellationToken.None });
                    }
                }
            }
            catch (System.Exception e) { err = "addlink: identity-setup: " + (e.InnerException?.Message ?? e.Message); }
            // Non-fatal: identity may resolve via the async path below.

            // --- Phase 2: await the profile fetch if needed (OUTSIDE any try). ---
            if (string.IsNullOrEmpty(userId) && profileTask != null)
            {
                yield return AwaitUniTask(profileTask);
                if (awaitedError == null && awaitedResult != null)
                    userId = GetPublicProperty(awaitedResult, "UserId") as string;
            }

            // --- Phase 3: build PassportParams(isOwnProfile:true) + show the Passport controller. No yields here. ---
            string openErr = null;
            try
            {
                Type paramT = FindType("DCL.Passport.PassportParams");
                if (paramT != null && !string.IsNullOrEmpty(userId))
                {
                    // PassportParams(string userId, string? badgeIdSelected = null, bool isOwnProfile = false)
                    object param = Activator.CreateInstance(paramT, userId, null, true);
                    TryShowPanelByName(mvcManager, "DCL.Passport.PassportController", param, out openErr);
                }
                else if (paramT == null)
                    openErr = "PassportParams type not found";
                else
                    openErr = "own userId unavailable";
            }
            catch (System.Exception e) { openErr = e.InnerException?.Message ?? e.Message; }
            if (openErr != null) err = (err == null ? "addlink: open: " + openErr : err + "; open: " + openErr);

            // Let the Passport view instantiate, OnViewShow run (default OVERVIEW section), and modules Setup.
            for (int i = 0; i < 40; i++) yield return null;

            // --- Phase 4: best-effort enter links edit mode, then force-show the AddLink modal. No yields here. ---
            bool modalShown = false;
            string modalErr = null;
            try
            {
                object controller = FindControllerByTypeName(mvcManager, "PassportController");
                object viewInstance = controller != null ? GetMember(controller, "viewInstance") : null;
                if (viewInstance == null)
                    modalErr = "PassportView viewInstance null";
                else
                {
                    // Best-effort: click LinksEditionButton (own profile) -> SetLinksSectionAsEditionMode(true).
                    // The button lives on UserDetailedInfo_PassportModuleView.LinksEditionButton.
                    try
                    {
                        object detailView = GetPublicProperty(viewInstance, "UserDetailedInfoModuleView");
                        object editBtn = detailView != null ? GetPublicProperty(detailView, "LinksEditionButton") : null;
                        if (editBtn != null)
                        {
                            object onClick = GetMember(editBtn, "onClick"); // UnityEngine.UI.Button.onClick (ButtonClickedEvent)
                            if (onClick != null)
                            {
                                MethodInfo invoke = onClick.GetType().GetMethod("Invoke", new Type[0]);
                                if (invoke != null) invoke.Invoke(onClick, null);
                            }
                        }
                    }
                    catch { /* edit-mode is cosmetic; the modal renders on top regardless */ }

                    // Force-show the add-link modal: AddLink_PassportModal.Show() (public, clears fields + SetActive(true)).
                    object addLinkModal = GetPublicProperty(viewInstance, "AddLinkModal");
                    if (addLinkModal == null)
                        modalErr = "AddLinkModal null on view";
                    else
                    {
                        MethodInfo show = addLinkModal.GetType()
                            .GetMethod("Show", BindingFlags.Public | BindingFlags.Instance, null, new Type[0], null);
                        if (show == null)
                            modalErr = "AddLink_PassportModal.Show() not found";
                        else
                        {
                            show.Invoke(addLinkModal, null);
                            modalShown = true;
                        }
                    }
                }
            }
            catch (System.Exception e) { modalErr = e.InnerException?.Message ?? e.Message; }
            if (modalErr != null) err = (err == null ? "addlink: modal: " + modalErr : err + "; modal: " + modalErr);

            // Settle so the modal GameObject activates and lays out before the shot.
            for (int i = 0; i < 18; i++) yield return null;

            yield return CaptureShot("addlink");

            // Non-gating: capture whatever rendered; report residual issues but keep ok=true.
            m.error = modalShown ? "shown" : ("shown; " + (err ?? "modal-not-shown"));
        }

        // Shows the Community Voice STREAM panel (DCL.VoiceChat.CommunityVoiceChat.CommunityVoiceChatPanelView) in its
        // idle/connecting state by force-showing the scene view. This sub-view lives inside the always-present
        // VoiceChatPanelView and is normally surfaced by CommunityVoiceChatPresenter.OnVoiceChatTypeChanged(COMMUNITY)
        // -> view.Show(). Show() calls SetConnectedPanel(false), which activates the InCall ConnectingPanel (the
        // "connecting/idle" stream UI) and hides the live ContentPanel/FooterPanel — so NO live stream, peer, or audio
        // is required. The presenter is a plain IDisposable (NOT an MVC ControllerBase), so it is unreachable via
        // MvcManager/IssueCommand; instead we find the scene MonoBehaviour with FindObjectsByType and invoke its public
        // Show() directly. NO NOISE: capture-only — we never join a call, publish a mic track, or change orchestrator state.
        private static IEnumerator AtlasCapture_communitystream(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_communitystream", ok = true };  // NON-GATING: ok stays true
            report.actions.Add(m);
            yield return HideExplorePanel(mvcManager);  // hide the persistent Explore/Communities grid bleeding behind this POPUP card (matches communitycontent)

            // S12 communitystream = a community CARD / live-stream surface, NOT the empty CommunityVoiceChatPanelView
            // (which renders nothing without a live call) and NOT the Communities browser grid. The correct, reachable
            // target is the CommunityCardController (the card that hosts the live-stream / StartStream UI), opened the
            // SAME way the proven S06 'communitycard' driver does: resolve a real communityId from the registered
            // CommunityCardController's communitiesDataProvider, then IssueCommand(new CommunityCardParameter(id)) ->
            // mvcManager.ShowAsync. The live voice stream itself is data-gated (needs voiceChatStatus.isActive on a
            // joinable community), so when no community is available we degrade gracefully and still capture an artifact.
            // READ-ONLY / NO-NOISE: only a read-only community list GET + a display of an existing card (no join/leave).
            string err = null;
            object communitiesDataProvider = null;
            object listTask = null;
            try
            {
                if (mvcManager == null) { err = "communitystream: mvcManager null"; }
                else
                {
                    object cardController = FindControllerByTypeName(mvcManager, "CommunityCardController");
                    if (cardController == null) err = "communitystream: CommunityCardController not registered";
                    else
                    {
                        communitiesDataProvider = GetPrivateField(cardController, "communitiesDataProvider");
                        if (communitiesDataProvider == null) err = "communitystream: communitiesDataProvider field not found";
                        else
                        {
                            // GetUserCommunitiesAsync(name, onlyMemberOf, pageNumber, elementsPerPage, ct, includeReq=false, isStreaming=false).
                            // isStreaming=true -> prefer communities that are actively streaming (closest to a live-stream card).
                            MethodInfo getCommunities = communitiesDataProvider.GetType()
                                .GetMethod("GetUserCommunitiesAsync", BindingFlags.Public | BindingFlags.Instance);
                            if (getCommunities == null) err = "communitystream: GetUserCommunitiesAsync not found";
                            else
                            {
                                ParameterInfo[] ps = getCommunities.GetParameters();
                                object[] args = new object[ps.Length];
                                for (int i = 0; i < ps.Length; i++)
                                {
                                    if (ps[i].ParameterType == typeof(string)) args[i] = "";
                                    else if (ps[i].ParameterType == typeof(bool)) args[i] = (ps[i].Name == "isStreaming"); // isStreaming=true, others false
                                    else if (ps[i].ParameterType == typeof(int)) args[i] = (ps[i].Name == "pageNumber") ? 1 : 10;
                                    else if (ps[i].ParameterType == typeof(System.Threading.CancellationToken)) args[i] = System.Threading.CancellationToken.None;
                                    else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                                }
                                listTask = getCommunities.Invoke(communitiesDataProvider, args);
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "communitystream: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }   // yield break OUTSIDE the try
            if (listTask == null) { m.error = "communitystream: list task null"; yield break; }

            // Await the community list OUTSIDE any try.
            yield return AwaitUniTask(listTask);
            if (awaitedError != null) { m.error = "communitystream: list fetch: " + awaitedError; yield break; }

            // Extract a communityId from response.data.results[].id (DTO field is lowercase 'id').
            string communityId = null;
            try
            {
                object data = awaitedResult != null ? GetMember(awaitedResult, "data") : null;
                object results = data != null ? GetMember(data, "results") : null;
                if (results is System.Collections.IEnumerable enumerable)
                    foreach (object community in enumerable)
                    {
                        string ids = GetMember(community, "id")?.ToString();
                        if (!string.IsNullOrEmpty(ids)) { communityId = ids; break; }
                    }
            }
            catch (System.Exception e) { err = "communitystream: id extract: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }
            if (string.IsNullOrEmpty(communityId))
            {
                // Data-gated: no streaming/joined community available -> nothing to show. Capture whatever renders.
                for (int i = 0; i < 18; i++) yield return null;
                yield return CaptureShot("communitystream");
                m.error = "communitystream: skipped:no-community-id (no streaming/member community for this account)";
                yield break;
            }

            // IssueCommand(new CommunityCardParameter(communityId)) -> mvcManager.ShowAsync<TView,TInput> (read-only display).
            try
            {
                Type controllerType = FindType("DCL.Communities.CommunitiesCard.CommunityCardController");
                Type paramType = FindType("DCL.Communities.CommunitiesCard.CommunityCardParameter");
                if (controllerType == null || paramType == null) err = "communitystream: card types not found";
                else
                {
                    object param = System.Activator.CreateInstance(paramType, communityId, null); // (string communityId, ISpriteCache? = null)
                    MethodInfo issueCommand = controllerType.GetMethod("IssueCommand",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy,
                        null, new[] { paramType }, null);
                    if (issueCommand == null) err = "communitystream: IssueCommand(param) not found";
                    else
                    {
                        object command = issueCommand.Invoke(null, new[] { param });
                        if (command == null) err = "communitystream: IssueCommand returned null";
                        else
                        {
                            MethodInfo showAsync = null;
                            foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                                if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                            if (showAsync == null) err = "communitystream: ShowAsync not found";
                            else
                            {
                                Type[] genArgs = command.GetType().GetGenericArguments();
                                showAsync.MakeGenericMethod(genArgs)
                                    .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });
                                Type ifaceOpen = FindType("MVC.IController`2");
                                lastPanelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(genArgs) : null;
                            }
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "communitystream: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Settle: let the card skeleton + signed community fetches resolve and content render (~3.5s @ 60fps).
            for (int i = 0; i < 210; i++) yield return null;
            if (lastPanelKey != null) VerifyShown(mvcManager, lastPanelKey, out _);
            yield return CaptureShot("communitystream");
            m.error = "shown";
        }

        // S12 SCREEN broadcast — REAL MUTATION, explicitly authorized. This drives the exact "owner GOES
        // LIVE" path that the Community Card's StartStream button uses:
        // CommunityCardVoiceChatPresenter.StartStream() -> IVoiceChatOrchestrator.StartCall(communityId,
        // VoiceChatType.COMMUNITY). For a no-active-call orchestrator that routes to
        // CommunityVoiceChatCallStatusService.StartCall -> ICommunityVoiceService.StartCommunityVoiceChatAsync
        // (a real RPC to the social service). On the Ok response the status flips to VOICE_CHAT_IN_CALL and the
        // orchestrator sets CurrentVoiceChatType=COMMUNITY, which surfaces the live CommunityVoiceChatPanelView.
        // THIS ACTUALLY STARTS A STREAM (goes live) — intended per explicit authorization.
        //
        // Two things must be resolved by reflection (both are plain C# objects, NOT MVC controllers or
        // MonoBehaviours):
        //   1) an OWNED community id — via CommunitiesDataProvider.GetUserCommunitiesAsync, preferring a row
        //      whose role==owner (CommunityMemberRole.owner). Falls back to any community if none is owned.
        //   2) the IVoiceChatOrchestrator instance — held by CommunityCardVoiceChatPresenter, which lives on the
        //      registered CommunityCardController. We deep-scan registered MVC controllers' fields (and their
        //      nested presenter fields) for any object whose type implements IVoiceChatOrchestrator.
        //
        // After firing StartCall we poll the orchestrator's CommunityCallStatus reactive property until it reaches
        // VOICE_CHAT_IN_CALL (LIVE), force the CommunityVoiceChatPanelView + ancestor chain active, and capture.
        // If the orchestrator can't be resolved, no owned community exists, or starting needs mic permission the
        // editor can't grant, we degrade gracefully: capture the best reachable stream UI and note why (ok stays
        // true regardless — NON-GATING).
        private static IEnumerator AtlasCapture_broadcast(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_broadcast", ok = true };  // NON-GATING: ok ALWAYS stays true
            report.actions.Add(m);

            string err = null;
            object dataProvider = null;     // CommunitiesDataProvider (reflected)
            object orchestrator = null;     // IVoiceChatOrchestrator (reflected)
            object listTask = null;         // UniTask<GetUserCommunitiesResponse>
            Type orchestratorIface = null;

            // PHASE 1 (sync reflection only — NO yield): locate CommunitiesDataProvider + IVoiceChatOrchestrator
            // by deep-scanning registered MVC controllers, then kick off the user-communities query.
            try
            {
                if (mvcManager == null) { err = "broadcast: mvcManager null"; }
                else
                {
                    orchestratorIface = FindType("DCL.VoiceChat.IVoiceChatOrchestrator");

                    object dict = GetPublicProperty(mvcManager, "Controllers");
                    if (dict == null)
                    {
                        object core = GetPrivateField(mvcManager, "core");
                        if (core != null) dict = GetPublicProperty(core, "Controllers");
                    }
                    object values = dict != null ? GetPublicProperty(dict, "Values") : null;
                    if (values is System.Collections.IEnumerable en)
                    {
                        foreach (object ctrl in en)
                        {
                            if (ctrl == null) continue;
                            foreach (FieldInfo fi in ctrl.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
                            {
                                object fv = fi.GetValue(ctrl);
                                if (fv == null) continue;

                                if (dataProvider == null && fv.GetType().Name == "CommunitiesDataProvider")
                                    dataProvider = fv;

                                if (orchestrator == null && orchestratorIface != null && orchestratorIface.IsInstanceOfType(fv))
                                    orchestrator = fv;

                                // One level deeper: the orchestrator usually hides inside a presenter
                                // (e.g. CommunityCardVoiceChatPresenter.voiceChatOrchestrator) rather than on
                                // the controller directly.
                                if (orchestrator == null && orchestratorIface != null && fv.GetType().Name.Contains("Presenter"))
                                {
                                    foreach (FieldInfo sfi in fv.GetType().GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public))
                                    {
                                        object sfv = sfi.GetValue(fv);
                                        if (sfv != null && orchestratorIface.IsInstanceOfType(sfv)) { orchestrator = sfv; break; }
                                    }
                                }
                            }
                            if (dataProvider != null && orchestrator != null) break;
                        }
                    }

                    if (dataProvider == null)
                        err = "broadcast: skipped:no-CommunitiesDataProvider-reachable (no registered controller holds it yet)";
                    else
                    {
                        // GetUserCommunitiesAsync(string name, bool onlyMemberOf, int pageNumber,
                        //   int elementsPerPage, CancellationToken ct, bool includeRequests=false, bool isStreaming=false)
                        var ct = System.Threading.CancellationToken.None;
                        MethodInfo mi = null;
                        foreach (MethodInfo c in dataProvider.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                            if (c.Name == "GetUserCommunitiesAsync") { mi = c; break; }
                        if (mi == null) err = "broadcast: GetUserCommunitiesAsync not found";
                        else
                        {
                            ParameterInfo[] ps = mi.GetParameters();
                            object[] args = new object[ps.Length];
                            for (int i = 0; i < ps.Length; i++)
                            {
                                if (ps[i].ParameterType == typeof(string)) args[i] = "";
                                else if (ps[i].ParameterType == typeof(bool)) args[i] = (ps[i].Name == "onlyMemberOf"); // onlyMemberOf=true -> communities I belong to (incl. owned)
                                else if (ps[i].ParameterType == typeof(int)) args[i] = (ps[i].Name == "pageNumber") ? 1 : 20;
                                else if (ps[i].ParameterType == typeof(System.Threading.CancellationToken)) args[i] = ct;
                                else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                            }
                            listTask = mi.Invoke(dataProvider, args);
                            if (listTask == null) err = "broadcast: GetUserCommunitiesAsync returned null";
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "broadcast: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Await the community query OUTSIDE any try.
            yield return AwaitUniTask(listTask);
            if (awaitedError != null) { m.error = "broadcast: list-failed: " + awaitedError; yield break; }

            // PHASE 2 (sync reflection only): pick an OWNED community id (role==owner) from the response,
            // falling back to the first community. Response shape:
            // GetUserCommunitiesResponse.data.results[] -> CommunityData{ id, role, ownerAddress }.
            string communityId = null;
            string ownedNote = "any-community (no owned row found)";
            try
            {
                object resp = awaitedResult;
                object payload = resp;
                object maybeValue = resp != null ? GetMember(resp, "Value") : null;
                if (maybeValue != null && maybeValue.GetType().Name.Contains("Response")) payload = maybeValue;

                object data = payload != null ? GetMember(payload, "data") : null;
                object results = data != null ? GetMember(data, "results") : null;

                string firstAny = null;
                if (results is System.Collections.IEnumerable ren)
                {
                    foreach (object comm in ren)
                    {
                        string id = GetMember(comm, "id") as string;   // 'id' is a public field
                        if (string.IsNullOrEmpty(id)) continue;
                        if (firstAny == null) firstAny = id;

                        // role is CommunityMemberRole enum; owner == 2. Compare by name to avoid enum-binding.
                        object roleObj = GetMember(comm, "role");
                        if (roleObj != null && string.Equals(roleObj.ToString(), "owner", System.StringComparison.OrdinalIgnoreCase))
                        {
                            communityId = id;
                            ownedNote = "owned-community";
                            break;
                        }
                    }
                }
                if (string.IsNullOrEmpty(communityId)) communityId = firstAny;

                if (string.IsNullOrEmpty(communityId))
                    err = "broadcast: dataGated:no-community-in-results (cannot go live without a community)";
            }
            catch (System.Exception e) { err = "broadcast: pick-community: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // PHASE 3 (sync reflection only): fire the REAL go-live action via the orchestrator. If the
            // orchestrator wasn't resolvable, we skip the mutation and just capture the panel UI.
            bool fired = false;
            try
            {
                if (orchestrator == null)
                {
                    err = "broadcast: skipped-mutation:no-IVoiceChatOrchestrator-reachable (capturing UI only)";
                }
                else
                {
                    Type vcTypeEnum = FindType("DCL.VoiceChat.VoiceChatType");
                    MethodInfo startCall = null;
                    foreach (MethodInfo mi in orchestratorIface.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
                        if (mi.Name == "StartCall" && mi.GetParameters().Length == 2) { startCall = mi; break; }
                    if (startCall == null) startCall = orchestrator.GetType().GetMethod("StartCall",
                        BindingFlags.Public | BindingFlags.Instance, null,
                        new[] { typeof(string), vcTypeEnum }, null);

                    if (startCall == null) err = "broadcast: StartCall(string,VoiceChatType) not found";
                    else if (vcTypeEnum == null) err = "broadcast: VoiceChatType enum not found";
                    else
                    {
                        object communityVal = System.Enum.Parse(vcTypeEnum, "COMMUNITY");
                        // REAL MUTATION: owner goes live (StartCommunityVoiceChatAsync RPC fires async inside).
                        startCall.Invoke(orchestrator, new object[] { communityId, communityVal });
                        fired = true;
                    }
                }
            }
            catch (System.Exception e) { err = "broadcast: StartCall failed: " + (e.InnerException?.Message ?? e.Message); }
            // Non-fatal: even if the mutation could not be fired we still try to render the stream panel.

            // PHASE 4: poll the orchestrator's CommunityCallStatus until LIVE (VOICE_CHAT_IN_CALL), giving the
            // async RPC time to land. Read CommunityCallStatus.Value each frame (reflection, no yield-in-try).
            string liveState = "unknown";
            if (fired && orchestrator != null)
            {
                object callStatusProp = null;
                try { callStatusProp = GetMember(orchestrator, "CommunityCallStatus"); } catch { callStatusProp = null; }

                for (int i = 0; i < 240; i++) // ~4s at editor frame rate; RPC round-trip + main-thread switch
                {
                    string sv = null;
                    try
                    {
                        object val = callStatusProp != null ? GetMember(callStatusProp, "Value") : null;
                        if (val != null) sv = val.ToString();
                    }
                    catch { sv = null; }
                    if (sv != null) liveState = sv;
                    if (sv == "VOICE_CHAT_IN_CALL") break;
                    yield return null;
                }
            }

            // PHASE 5: force the live CommunityVoiceChatPanelView + its ancestor chain active so it renders, then
            // capture. Done even when the mutation didn't fully connect, to yield a best-effort artifact.
            try
            {
                Type viewType = FindType("DCL.VoiceChat.CommunityVoiceChat.CommunityVoiceChatPanelView");
                if (viewType != null)
                {
                    var found = UnityEngine.Object.FindObjectsByType(viewType, FindObjectsInactive.Include);
                    if (found != null && found.Length > 0 && found[0] is Component pvComp && pvComp != null)
                    {
                        for (Transform tr = pvComp.transform; tr != null; tr = tr.parent)
                            if (!tr.gameObject.activeSelf) tr.gameObject.SetActive(true);

                        var showM = viewType.GetMethod("Show", BindingFlags.Public | BindingFlags.Instance, null, System.Type.EmptyTypes, null);
                        if (showM != null) showM.Invoke(found[0], null);
                    }
                }
            }
            catch { /* capture-best-effort: ignore view-surface failures */ }

            // Settle so the live panel (ContentPanel/FooterPanel for an in-call owner) finishes laying out.
            for (int i = 0; i < 24; i++) yield return null;
            yield return CaptureShot("communitystream");

            // Compose the final note (NON-GATING).
            if (err != null)
                m.error = err + " [community=" + ownedNote + ", liveState=" + liveState + "] (captured fallback)";
            else if (liveState == "VOICE_CHAT_IN_CALL")
                m.error = "shown";
            else
                m.error = "shown (mutation fired; liveState=" + liveState + ", community=" + ownedNote +
                          " — stream may need mic permission or backend ownership to reach IN_CALL)";
        }

        // Force-shows the generic right-click context menu (GenericContextMenuController) populated with the
        // REAL user-profile context-menu entries as they appear when right-clicking a user nametag / chat name.
        // Labels + order verified against GenericUserProfileContextMenuController.cs and the serialized
        // GenericUserProfileContextMenuSettings.asset: header + separator, Mention, View Profile, Chat, Call,
        // Gift, Jump to Location, separator, Report, Block. Report/Block carry the real destructive-red text
        // colour (ContextMenuColors.DESTRUCTIVE_ACTION = rgba(1, 0.176, 0.333, 1)). "Copy" is the chat-message
        // variant; the user menu above is the canonical generic context menu, so we render that.
        // NO NOISE: GenericContextMenu is a purely-local UI popup. We build the config with control settings
        // whose click callbacks are no-ops we never invoke; nothing is sent to the network, no
        // profile/chat/friend/account mutation occurs. We open it fire-and-forget (the controller blocks on
        // WaitForCloseIntentAsync forever, so we must NOT await ShowAsync) and screenshot whatever renders.
        private static IEnumerator AtlasCapture_contextmenu(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_contextmenu", ok = true };  // NON-GATING: ok always stays true
            report.actions.Add(m);

            // wrong-grid isolation: drop the leftover persistent Communities/Explore grid so the generic context
            // menu popup is captured over the clean HUD instead of over a stale Communities grid background.
            yield return HideExplorePanel(mvcManager);

            string err = null;
            Type panelKey = null;

            // ---- All reflection / synchronous setup (NO yield in here) ----
            try
            {
                // 1) Resolve the config + control-settings types (all in DCL.UI / DCL.UI.Controls.Configs).
                Type menuT = FindType("DCL.UI.GenericContextMenu");
                Type paramT = FindType("DCL.UI.GenericContextMenuParameter");
                Type controllerT = FindType("DCL.UI.GenericContextMenuController");
                Type textSettingsT = FindType("DCL.UI.Controls.Configs.TextContextMenuControlSettings");
                Type buttonSettingsT = FindType("DCL.UI.Controls.Configs.SimpleButtonContextMenuControlSettings");
                Type separatorSettingsT = FindType("DCL.UI.Controls.Configs.SeparatorContextMenuControlSettings");

                if (menuT == null) err = "contextmenu: GenericContextMenu type not found";
                else if (paramT == null) err = "contextmenu: GenericContextMenuParameter type not found";
                else if (controllerT == null) err = "contextmenu: GenericContextMenuController type not found";
                else if (textSettingsT == null) err = "contextmenu: TextContextMenuControlSettings type not found";
                else if (buttonSettingsT == null) err = "contextmenu: SimpleButtonContextMenuControlSettings type not found";
                else if (separatorSettingsT == null) err = "contextmenu: SeparatorContextMenuControlSettings type not found";

                // 2) Build the GenericContextMenu config (all ctor args are optional -> default ctor instance).
                object menuConfig = null;
                if (err == null)
                {
                    menuConfig = CreateContextMenuOptional(menuT, new object[0]);
                    if (menuConfig == null) err = "contextmenu: could not instantiate GenericContextMenu";
                }

                // 3) Add a couple of sample controls via GenericContextMenu.AddControl(IContextMenuControlSettings).
                //    The button callbacks are harmless no-op Actions that are never invoked by the harness.
                if (err == null)
                {
                    MethodInfo addControl = null;
                    foreach (MethodInfo mi in menuT.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (mi.Name != "AddControl") continue;
                        ParameterInfo[] ps = mi.GetParameters();
                        // Pick the overload taking IContextMenuControlSettings (not the GenericContextMenuElement one).
                        if (ps.Length == 1 && ps[0].ParameterType.Name == "IContextMenuControlSettings") { addControl = mi; break; }
                    }
                    if (addControl == null) err = "contextmenu: AddControl(IContextMenuControlSettings) not found";

                    if (err == null)
                    {
                        System.Action noop = () => { };

                        // Real destructive-action text colour from ContextMenuColors.DESTRUCTIVE_ACTION.
                        var redColor = new UnityEngine.Color(1f, 0.176f, 0.333f, 1f);

                        // Header: the target user's display name (TextContextMenuControlSettings(string text)).
                        object header = CreateContextMenuOptional(textSettingsT, new object[] { "Alice.dcl" });
                        object sep1 = CreateContextMenuOptional(separatorSettingsT, new object[0]);

                        // Non-destructive entries (default text colour). SimpleButtonContextMenuControlSettings
                        // ctor: (string buttonText, Action clickAction, ...) -> pass first 2.
                        object btnMention = CreateContextMenuOptional(buttonSettingsT, new object[] { "Mention", noop });
                        object btnProfile = CreateContextMenuOptional(buttonSettingsT, new object[] { "View Profile", noop });
                        object btnChat = CreateContextMenuOptional(buttonSettingsT, new object[] { "Chat", noop });
                        object btnCall = CreateContextMenuOptional(buttonSettingsT, new object[] { "Call", noop });
                        object btnGift = CreateContextMenuOptional(buttonSettingsT, new object[] { "Gift", noop });
                        object btnJump = CreateContextMenuOptional(buttonSettingsT, new object[] { "Jump to Location", noop });

                        object sep2 = CreateContextMenuOptional(separatorSettingsT, new object[0]);

                        // Destructive entries get the red text colour. The ctor's 6th param is textColor; fill
                        // the in-between optionals (RectOffset=null, spacing=10, reverse=false) explicitly.
                        object btnReport = CreateContextMenuOptional(buttonSettingsT,
                            new object[] { "Report", noop, null, 10, false, redColor });
                        object btnBlock = CreateContextMenuOptional(buttonSettingsT,
                            new object[] { "Block", noop, null, 10, false, redColor });
                        // Fallback if the 6-arg colour overload didn't bind (keeps the entry, just default colour).
                        if (btnReport == null) btnReport = CreateContextMenuOptional(buttonSettingsT, new object[] { "Report", noop });
                        if (btnBlock == null) btnBlock = CreateContextMenuOptional(buttonSettingsT, new object[] { "Block", noop });

                        object[] controls = { header, sep1, btnMention, btnProfile, btnChat, btnCall, btnGift,
                                              btnJump, sep2, btnReport, btnBlock };

                        bool anyNull = false;
                        foreach (object c in controls) if (c == null) anyNull = true;

                        if (anyNull)
                            err = "contextmenu: failed to build one or more context-menu control settings";
                        else
                            foreach (object c in controls)
                                addControl.Invoke(menuConfig, new object[] { c });
                    }
                }

                // 4) Build GenericContextMenuParameter(config, anchorPosition[, overlapRect, ...]).
                //    Anchor roughly at screen centre so the menu renders fully on-screen.
                object param = null;
                if (err == null)
                {
                    var anchor = new UnityEngine.Vector2(Screen.width * 0.5f, Screen.height * 0.5f);

                    ConstructorInfo paramCtor = null;
                    foreach (ConstructorInfo ci in paramT.GetConstructors())
                        if (ci.GetParameters().Length >= 2) { paramCtor = ci; break; }
                    if (paramCtor == null) err = "contextmenu: GenericContextMenuParameter ctor not found";

                    if (err == null)
                    {
                        ParameterInfo[] cps = paramCtor.GetParameters();
                        object[] args = new object[cps.Length];
                        args[0] = menuConfig;
                        args[1] = anchor;
                        // Remaining params (overlapRect, actionOnShow, actionOnHide, closeTask) are all optional.
                        for (int i = 2; i < cps.Length; i++) args[i] = Type.Missing;
                        param = paramCtor.Invoke(args);
                    }
                }

                // 5) GenericContextMenuController.IssueCommand(param) -> ShowCommand<TView,TInput> (inherited static).
                object command = null;
                if (err == null)
                {
                    MethodInfo issue = null;
                    foreach (MethodInfo mi in controllerT.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
                        if (mi.Name == "IssueCommand" && mi.GetParameters().Length == 1) { issue = mi; break; }
                    if (issue == null) err = "contextmenu: IssueCommand(1-arg) not found";

                    command = issue != null ? issue.Invoke(null, new object[] { param }) : null;
                    if (err == null && command == null) err = "contextmenu: IssueCommand returned null";
                }

                // 6) mvcManager.ShowAsync<TView,TInput>(command, CancellationToken.None) — FIRE-AND-FORGET.
                //    The controller waits on a close intent that never comes, so we must NOT await it.
                if (err == null)
                {
                    MethodInfo showAsync = null;
                    foreach (MethodInfo mi in mvcManager.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
                        if (mi.Name == "ShowAsync" && mi.IsGenericMethodDefinition) { showAsync = mi; break; }
                    if (showAsync == null) err = "contextmenu: ShowAsync not found";

                    if (err == null)
                    {
                        Type[] genArgs = command.GetType().GetGenericArguments();
                        showAsync.MakeGenericMethod(genArgs)
                                 .Invoke(mvcManager, new object[] { command, System.Threading.CancellationToken.None });

                        Type ifaceOpen = FindType("MVC.IController`2");
                        panelKey = ifaceOpen != null ? ifaceOpen.MakeGenericType(genArgs) : null;
                    }
                }
            }
            catch (System.Exception e) { err = "contextmenu: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }

            // Settle: pool warm-up + control instantiation + layout rebuild + show animation.
            for (int i = 0; i < 20; i++) yield return null;

            // Best-effort verification (non-gating): State != ViewHidden/ViewHiding.
            if (panelKey != null && !VerifyShown(mvcManager, panelKey, out string verifyErr))
                m.error = "contextmenu: " + verifyErr;

            yield return CaptureShot("contextmenu");

            if (m.error == null)
                m.error = "shown";
        }

        // Builds an object via the ctor whose leading params match args, filling trailing optionals with Type.Missing.
        private static object CreateContextMenuOptional(Type t, object[] args)
        {
            foreach (ConstructorInfo ci in t.GetConstructors())
            {
                ParameterInfo[] ps = ci.GetParameters();
                if (ps.Length < args.Length) continue;
                object[] full = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++) full[i] = i < args.Length ? args[i] : Type.Missing;
                try { return ci.Invoke(full); } catch { /* try next ctor */ }
            }
            return null;
        }

        // Force-show the SHARED generic ConfirmationDialogController (DCL.UI.ConfirmationDialog) with a sample
        // title/sub-text and Cancel/Confirm button labels, then screenshot. This is the reusable yes/cancel popup
        // used across the app (delete outfit, leave community, gift transfer, report user, etc.). NO-NOISE:
        // we only OPEN the local POPUP-layer view via IssueCommand->ShowAsync; we never await its close intent and
        // NEVER click Confirm/Cancel, so no account mutation or anything visible to other users occurs. We pass a
        // ResultCallback of null and no Image/profile so no profile fetch or network access is triggered.
        private static IEnumerator AtlasCapture_confirm(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_confirm", ok = true };  // NON-GATING: ok stays true
            report.actions.Add(m);

            // wrong-grid isolation: drop the leftover persistent Communities/Explore grid so the shared confirmation
            // dialog popup is captured over the clean HUD instead of over a stale Communities grid background.
            yield return HideExplorePanel(mvcManager);

            string err = null;
            object param = null;
            try
            {
                if (mvcManager == null) { err = "confirm: mvcManager is null"; }
                else
                {
                    // ConfirmationDialogParameter is a struct in DCL.UI.ConfirmationDialog.Opener. Its single ctor:
                    //   (string text, string cancelButtonText, string confirmButtonText, Sprite image,
                    //    bool showImageRim, bool showQuitImage, Action<ConfirmationResult> resultCallback = null,
                    //    string subText = "", Profile.CompactInfo userInfo = default, string linkText = "",
                    //    Action<string> onLinkClickCallback = null, bool preserveAspect = false,
                    //    Profile.CompactInfo fromUserInfo = default)
                    // We supply the 6 required leading args (image null, no rim, no quit) and Type.Missing for the
                    // 7 trailing optionals so the struct builds without any sprite/profile dependency.
                    Type paramT = FindType("DCL.UI.ConfirmationDialog.Opener.ConfirmationDialogParameter");
                    if (paramT == null) { err = "confirm: ConfirmationDialogParameter type not found"; }
                    else
                    {
                        ConstructorInfo[] ctors = paramT.GetConstructors();
                        ConstructorInfo ctor = null;
                        for (int i = 0; i < ctors.Length; i++)
                            if (ctors[i].GetParameters().Length >= 6) { ctor = ctors[i]; break; }
                        if (ctor == null) { err = "confirm: matching ctor not found"; }
                        else
                        {
                            ParameterInfo[] ps = ctor.GetParameters();
                            object[] args = new object[ps.Length];
                            args[0] = "Are you sure?";          // text (main title)
                            args[1] = "Cancel";                  // cancelButtonText
                            args[2] = "Confirm";                 // confirmButtonText
                            args[3] = null;                      // image (Sprite) -> no main image
                            args[4] = false;                     // showImageRim
                            args[5] = false;                     // showQuitImage
                            for (int i = 6; i < ps.Length; i++) args[i] = Type.Missing; // optionals (incl. null callback)
                            param = ctor.Invoke(BindingFlags.OptionalParamBinding, null, args, null);
                        }
                    }
                }
            }
            catch (System.Exception e) { err = "confirm: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = "not-shown: " + err; yield break; }

            // Show via the shared IssueCommand(param) -> MVCManager.ShowAsync<TView,TInput> helper. POPUP layer; local UI only.
            string showErr;
            bool opened = TryShowPanelByName(mvcManager, "DCL.UI.ConfirmationDialog.ConfirmationDialogController", param, out showErr);
            if (!opened) { m.error = "not-shown: confirm: show-failed (" + showErr + ")"; yield break; }

            for (int i = 0; i < 18; i++) yield return null; // settle (view configures synchronously on show)
            yield return CaptureShot("confirm");

            string rerr = null;
            try { if (lastPanelKey != null && !VerifyShown(mvcManager, lastPanelKey, out rerr)) rerr = "not-shown: " + rerr; else rerr = null; }
            catch (System.Exception e) { rerr = "verify-failed: " + (e.InnerException?.Message ?? e.Message); }
            m.error = rerr ?? "shown";
        }

        // X03 gallery — the shared full-screen image viewer (PhotoDetailController, the camera-reel /
        // passport / community-card photo lightbox). M05-photo opens this same viewer on the signed-in
        // user's REAL reel gallery, which is empty on the test account (data-gated -> degraded). Here we
        // instead force-show the viewer with a SYNTHETIC placeholder reel so the lightbox chrome + image
        // render regardless of account data. Read-only/no-noise: we never delete/share/set-public; the
        // only network traffic is GETs (the placeholder image + best-effort metadata for a fabricated id).
        // OpenedFromPublicBoard=true hides the delete + set-public controls (no destructive UI surfaced).
        // PhotoDetailController : ControllerBase<PhotoDetailView, PhotoDetailParameter>; shown via
        // IssueCommand(param) -> mvcManager.ShowAsync (fire-and-forget, the view renders synchronously).
        //
        // FIX (image was a perpetual spinner): the previous placeholder
        // "https://decentraland.org/images/ui/dark-atlas-v3.png" is DEAD — it returns 200 text/html (an
        // SPA error page), not PNG bytes, so UnityWebRequestTexture.GetTexture(reel.url) decodes nothing
        // and mainImageLoadingSpinner never turns off. ShowReelAsync does the uncompressed path:
        //   reelTexture = await cameraReelScreenshotsStorage.GetScreenshotImageAsync(reel.url,false,ct)
        //   -> UnityWebRequestTexture.GetTexture(reel.url); spinner hides only AFTER a valid texture.
        // We now point reel.url/thumbnailUrl at a REAL, immutable, content-addressed Decentraland asset
        // (the Genesis Plaza place card PNG on the content peer — CID-based, verified 200 + "PNG image
        // data 1140x800"), so the viewer downloads an actual picture. The old camera-reel-service S3
        // objects 403 now (expired), and CID content-server URLs never change -> stable for the atlas.
        // Settle bumped 40 -> 280 frames: a generous fixed wait for the ~1.3MB fetch + decode + fade-in.
        private static IEnumerator AtlasCapture_gallery(object mvcManager, object staticContainer, object dynamicContainer, object realmNavigator, Report report)
        {
            var m = new PhaseMarker { label = "atlas_gallery", ok = true };  // NON-GATING
            report.actions.Add(m);

            // X03 gallery = the CameraReel gallery THUMBNAIL GRID (not the single PhotoDetail lightbox; that is
            // M05 'photo'). Same proven path the working M04 'reel' driver uses: open the ExplorePanel on the
            // CameraReel section (DCL.UI.ExploreSections.CameraReel) then directly Activate() the CameraReel
            // ISection so its ShowAsync (storage-info GET + wallet-gallery thumbnail listing) re-runs. The empty
            // grid ("no photos yet") is a valid capture for an account with no reels. READ-ONLY / NO-NOISE.
            string err = null;
            try
            {
                if (mvcManager == null) err = "gallery: mvcManager null";
                else if (!TryOpenExplorePanel(mvcManager, "CameraReel", null, out string openErr))
                    err = "gallery: open CameraReel section: " + openErr;
                // TryOpenExplorePanel sets lastPanelKey to the ExplorePanel controller key on success.
            }
            catch (System.Exception e) { err = "gallery: " + (e.InnerException?.Message ?? e.Message); }
            if (err != null) { m.error = err; yield break; }   // yield break OUTSIDE the try

            // Let the ExplorePanel view instantiate and OnViewShow toggle the CameraReel section on.
            for (int i = 0; i < 18; i++) yield return null;

            // Belt-and-suspenders: directly Activate() the CameraReel ISection (public, read-only) so its
            // ShowAsync re-runs even if the section-selector skipped re-activation. Non-gating.
            try
            {
                object explorePanelCtl = FindControllerByTypeName(mvcManager, "ExplorePanelController");
                if (explorePanelCtl != null)
                {
                    object cameraReelController = GetMember(explorePanelCtl, "CameraReelController"); // public prop, ISection
                    if (cameraReelController != null)
                    {
                        MethodInfo activate = cameraReelController.GetType()
                            .GetMethod("Activate", BindingFlags.Public | BindingFlags.Instance);
                        activate?.Invoke(cameraReelController, null);
                    }
                }
            }
            catch (System.Exception e)
            {
                string aErr = e.InnerException?.Message ?? e.Message;
                err = (err == null ? "gallery: activate: " + aErr : err + "; activate: " + aErr);
            }

            // Wait for the storage-info GET + gallery thumbnail listing GET to resolve and the empty/populated state to render.
            for (int i = 0; i < 45; i++) yield return null;

            if (lastPanelKey != null) VerifyShown(mvcManager, lastPanelKey, out _);  // best-effort render note (non-gating)
            yield return CaptureShot("gallery");
            m.error = err == null ? "shown" : ("shown; " + err);
        }

        // ATLAS_METHODS_END

        // =====================================================================
        //  DTOs + tiny JSON writer (no Newtonsoft dependency from Editor asmdef)
        // =====================================================================
        private class Report
        {
            public string startedUtc, finishedUtc;
            public double totalWallSeconds;
            public string fatal;
            public bool reachedInteractive;
            public float timeToInteractiveSeconds;
            public string lastLoadingStage;
            public bool foundRealmNavigator, foundChatBus, foundProfiler;
            public int totalLogMessages, warningCount, errorCount;
            public List<PhaseMetrics> phases = new();
            public List<PhaseMarker>  actions = new();
            public List<LogEntry> warnings = new();
            public List<LogEntry> errors = new();

            public string ToJson()
            {
                var sb = new StringBuilder();
                sb.Append("{\n");
                J(sb, "startedUtc", startedUtc); J(sb, "finishedUtc", finishedUtc);
                Jn(sb, "totalWallSeconds", totalWallSeconds);
                if (fatal != null) J(sb, "fatal", fatal);
                Jb(sb, "reachedInteractive", reachedInteractive);
                Jn(sb, "timeToInteractiveSeconds", timeToInteractiveSeconds);
                J(sb, "lastLoadingStage", lastLoadingStage);
                Jb(sb, "foundRealmNavigator", foundRealmNavigator);
                Jb(sb, "foundChatBus", foundChatBus);
                Jb(sb, "foundProfiler", foundProfiler);
                Jn(sb, "totalLogMessages", totalLogMessages);
                Jn(sb, "warningCount", warningCount);
                Jn(sb, "errorCount", errorCount);
                // phases
                sb.Append("  \"phases\": [\n");
                for (int i = 0; i < phases.Count; i++) { sb.Append("    "); sb.Append(phases[i].ToJson()); sb.Append(i < phases.Count - 1 ? ",\n" : "\n"); }
                sb.Append("  ],\n");
                // actions
                sb.Append("  \"actions\": [\n");
                for (int i = 0; i < actions.Count; i++) { sb.Append("    "); sb.Append(actions[i].ToJson()); sb.Append(i < actions.Count - 1 ? ",\n" : "\n"); }
                sb.Append("  ],\n");
                // errors / warnings (capped)
                AppendLogs(sb, "errors", errors, true);
                AppendLogs(sb, "warnings", warnings, false);
                sb.Append("}\n");
                return sb.ToString();
            }

            private static void AppendLogs(StringBuilder sb, string key, List<LogEntry> logs, bool more)
            {
                sb.Append("  \"" + key + "\": [\n");
                for (int i = 0; i < logs.Count; i++)
                {
                    sb.Append("    {");
                    sb.Append("\"type\":\"" + Esc(logs[i].type) + "\",");
                    sb.Append("\"message\":\"" + Esc(logs[i].message) + "\"");
                    sb.Append("}");
                    sb.Append(i < logs.Count - 1 ? ",\n" : "\n");
                }
                sb.Append(more ? "  ],\n" : "  ]\n");
            }

            private static void J(StringBuilder sb, string k, string v) => sb.Append("  \"" + k + "\": " + (v == null ? "null" : "\"" + Esc(v) + "\"") + ",\n");
            private static void Jn(StringBuilder sb, string k, double v) => sb.Append("  \"" + k + "\": " + v.ToString(CultureInfo.InvariantCulture) + ",\n");
            private static void Jb(StringBuilder sb, string k, bool v) => sb.Append("  \"" + k + "\": " + (v ? "true" : "false") + ",\n");
        }

        private class PhaseMetrics
        {
            public string label;
            public int frames; public double durationSeconds;
            public double cpuMsAvg, cpuMsP99Worst, cpuMsMax, fpsAvg, gpuMsAvg, gpuMsMax;
            public long hiccupFramesOver50ms;
            public double gcAllocBytesTotal, systemUsedMemoryMB;
            public long drawCallsLast, batchesLast, setPassLast, trianglesLast;
            public string ToJson() =>
                "{" +
                $"\"label\":\"{Esc(label)}\",\"frames\":{frames},\"durationSeconds\":{durationSeconds.ToString(CultureInfo.InvariantCulture)}," +
                $"\"fpsAvg\":{fpsAvg.ToString("F2", CultureInfo.InvariantCulture)},\"cpuMsAvg\":{cpuMsAvg.ToString("F3", CultureInfo.InvariantCulture)}," +
                $"\"cpuMsP99Worst\":{cpuMsP99Worst.ToString("F3", CultureInfo.InvariantCulture)},\"cpuMsMax\":{cpuMsMax.ToString("F3", CultureInfo.InvariantCulture)}," +
                $"\"gpuMsAvg\":{gpuMsAvg.ToString("F3", CultureInfo.InvariantCulture)},\"gpuMsMax\":{gpuMsMax.ToString("F3", CultureInfo.InvariantCulture)}," +
                $"\"hiccupFramesOver50ms\":{hiccupFramesOver50ms},\"gcAllocBytesTotal\":{gcAllocBytesTotal.ToString("F0", CultureInfo.InvariantCulture)}," +
                $"\"systemUsedMemoryMB\":{systemUsedMemoryMB.ToString("F1", CultureInfo.InvariantCulture)}," +
                $"\"drawCallsLast\":{drawCallsLast},\"batchesLast\":{batchesLast},\"setPassLast\":{setPassLast},\"trianglesLast\":{trianglesLast}" +
                "}";
        }

        private class PhaseMarker
        {
            public string label; public bool ok; public string error;
            public string ToJson() => $"{{\"label\":\"{Esc(label)}\",\"ok\":{(ok ? "true" : "false")},\"error\":{(error == null ? "null" : "\"" + Esc(error) + "\"")}}}";
        }

        private static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4")); else sb.Append(c); break;
                }
            return sb.ToString();
        }
    }

    // -------------------------------------------------------------------------
    //  Minimal editor coroutine pump (no MonoBehaviour, survives in Edit+Play).
    //  Drives IEnumerators off EditorApplication.update. yield return null ==
    //  "next editor tick". Good enough for our polling/sampling cadence.
    //  NOTE: editor ticks are NOT 1:1 with render frames, but ProfilerRecorder
    //  LastValue still reflects the latest frame, so per-sample metrics are valid.
    //  TODO(LIVE): if sampling cadence is too coarse, switch to a hidden
    //  [ExecuteAlways] MonoBehaviour spawned at play start for true per-frame ticks.
    // -------------------------------------------------------------------------
    internal static class HarnessRunner
    {
        // Each running routine is a coroutine STACK so that `yield return <IEnumerator>`
        // (e.g. `yield return SamplePhase(...)`) descends into the nested coroutine and
        // drives it to completion before resuming the parent — exactly like Unity's
        // StartCoroutine. The previous naive pump only MoveNext'd the top-level
        // enumerator, so nested coroutines (the perf samplers) never actually ran.
        private static readonly List<Stack<IEnumerator>> stacks = new();
        private static bool hooked;

        public static void Start(IEnumerator routine)
        {
            var s = new Stack<IEnumerator>();
            s.Push(routine);
            stacks.Add(s);
            if (!hooked) { EditorApplication.update += Tick; hooked = true; }
        }

        private static void Tick()
        {
            for (int i = stacks.Count - 1; i >= 0; i--)
            {
                var stack = stacks[i];
                if (stack.Count == 0) { stacks.RemoveAt(i); continue; }
                IEnumerator top = stack.Peek();
                bool moved;
                try { moved = top.MoveNext(); }
                catch (Exception e) { Debug.LogError("[HarnessRunner] " + e); stacks.RemoveAt(i); continue; }
                if (moved)
                {
                    if (top.Current is IEnumerator nested) stack.Push(nested); // descend
                    // any other yield value (null, etc.) => wait one editor tick
                }
                else
                {
                    stack.Pop();                       // finished; resume parent next tick
                    if (stack.Count == 0) stacks.RemoveAt(i);
                }
            }
            if (stacks.Count == 0 && hooked) { EditorApplication.update -= Tick; hooked = false; }
        }
    }
}
#endif

