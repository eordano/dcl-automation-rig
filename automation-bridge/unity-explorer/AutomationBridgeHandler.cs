// AutomationBridgeHandler — mounts a license-free automation command channel on DCL's own
// CDP WebSocket bridge (decentraland/chrome-devtool-protocol-unity), in the BUILT PLAYER.
//
//   driver (Python / Claude / rig CDP harness)  --ws-->  Bridge(:1474)  -->  this handler
//                                                              |  (background thread)
//                                                     marshal to main thread (MainThreadPump)
//                                                              v
//                                                       AutomationCore.Dispatch  (reflection)
//
// No instrumented AltTester SDK, no Desktop relay, no per-seat license. Gated behind an
// app-arg flag and constructed with NoopBrowser so it never tries to launch Chrome.
//
// Wire-format: a CDP request { "id":N, "method":"Automation.<op>", "params":{...} }
// returns     { "id":N, "result": <AutomationCore json> }.
//
// Two dispatch paths:
//   * most ops  -> synchronous AutomationCore.Dispatch, marshalled onto the main thread.
//   * screenshot-> async AutomationScreenshot.CaptureBase64 (needs a WaitForEndOfFrame coroutine),
//                  which signals back through a TaskCompletionSource the bridge thread blocks on.
// Input ops (click-at/key/type/swipe) ride the normal Dispatch path -> AutomationInput.Handle.
// SECURITY: gated behind a dedicated compile define so the WS command channel is STRIPPED from
// release players entirely (mirrors AltTester's `#if ALTTESTER`) — not merely dormant behind a
// launch flag. Define DCL_AUTOMATION_BRIDGE only in editor + dev/automation builds; CI must NOT
// set it for release. !UNITY_WEBGL because a web page can't host a WebSocket server.
#if !UNITY_WEBGL && DCL_AUTOMATION_BRIDGE
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CDPBridges;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ILogger = Microsoft.Extensions.Logging.ILogger; // Bridge wants M.E.Logging.ILogger (DCLLogger implements it), not UnityEngine.ILogger

namespace DCL.Automation
{
    public sealed class AutomationBridgeHandler : IDisposable
    {
        private const int DEFAULT_PORT = 1474;          // distinct from the CDP network monitor (1473)
        private const int MAIN_THREAD_TIMEOUT_MS = 10000;

        private readonly IBridge bridge;

        public AutomationBridgeHandler(int port = DEFAULT_PORT, ILogger logger = null)
        {
            MainThreadPump.Ensure();
            bridge = new Bridge(handleMethod: Handle, port: port, browser: new NoopBrowser(), logger: logger);
        }

        public BridgeStartResult Start() => bridge.Start();
        public BridgeStatus Status => bridge.Status;
        public void Dispose() => bridge.Dispose();

        // Called on a BACKGROUND thread by the bridge -> marshal to the Unity main thread.
        private CDPResult? Handle(int id, CDPMethod method)
        {
            if (!method.IsCustom(out CDPMethod.Custom custom)) return null;
            if (custom.Method == null || !custom.Method.StartsWith("Automation.", StringComparison.Ordinal)) return null;

            string op = custom.Method.Substring("Automation.".Length);
            Dictionary<string, string> args = ToArgs(custom.Params);

            // "screenshot" can't run synchronously (needs WaitForEndOfFrame) -> its own async path.
            string json = op == "screenshot"
                ? CaptureScreenshot()
                : RunOnMainThread(() => AutomationCore.Dispatch(op, args));

            json ??= "{\"ok\":false,\"error\":\"automation: main-thread timeout\"}";
            return CDPResult.FromJson(new CDPResult.Json(json));
        }

        // Marshals synchronous reflection work onto the main thread and blocks (bounded) for it.
        // TaskCompletionSource (not ManualResetEventSlim): a late main-thread completion after the
        // bridge thread has timed out is a harmless no-op TrySetResult, never a Set() on a disposed primitive.
        private static string RunOnMainThread(Func<string> work)
        {
            var tcs = new TaskCompletionSource<string>();
            MainThreadPump.Enqueue(() =>
            {
                try { tcs.TrySetResult(work()); }
                catch (Exception e) { tcs.TrySetResult("{\"ok\":false,\"error\":\"automation threw: " + Sanitize(e.Message) + "\"}"); }
            });
            return tcs.Task.Wait(MAIN_THREAD_TIMEOUT_MS) ? tcs.Task.Result : null;
        }

        // Async screenshot: AutomationScreenshot schedules a WaitForEndOfFrame coroutine on the pump and
        // invokes the callback once the PNG is encoded; we block the bridge thread until it completes.
        private static string CaptureScreenshot()
        {
            var tcs = new TaskCompletionSource<string>();
            try
            {
                AutomationScreenshot.CaptureBase64(json => tcs.TrySetResult(json));
            }
            catch (Exception e)
            {
                return "{\"ok\":false,\"error\":\"screenshot threw: " + Sanitize(e.Message) + "\"}";
            }
            return tcs.Task.Wait(MAIN_THREAD_TIMEOUT_MS) ? tcs.Task.Result : null;
        }

        private static string Sanitize(string s) => s == null ? "" : s.Replace('\\', '/').Replace('"', '\'');

        private static Dictionary<string, string> ToArgs(JObject p)
        {
            var d = new Dictionary<string, string>();
            if (p == null) return d;
            foreach (var kv in p)
                d[kv.Key] = kv.Value == null ? null
                          : kv.Value.Type == JTokenType.String ? kv.Value.Value<string>()
                          : kv.Value.ToString(Formatting.None);
            return d;
        }
    }
}
#endif
