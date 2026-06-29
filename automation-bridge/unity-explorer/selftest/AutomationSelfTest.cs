// AutomationSelfTest — self-driven runtime smoke test for the in-player automation bridge.
// Opens an EMPTY scene (no DCL world-boot), enters Play, starts AutomationBridgeHandler, and drives
// the CDP WebSocket on :PORT with an in-process ClientWebSocket — exercising the REAL path:
//   ClientWebSocket -> Fleck server (Bridge) -> handleMethod (bg thread) -> MainThreadPump
//   -> AutomationCore / AutomationInput / AutomationScreenshot -> response. Logs AB_SELFTEST PASS/FAIL.
//
// Run (graphics ON so screenshot has a framebuffer; NO -quit so EditorApplication.Exit ends it):
//   Unity -batchmode -projectPath <proj> -executeMethod AutomationSelfTest.EnterPlay -logFile <log>
#if UNITY_EDITOR
using System;
using System.Collections;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace DCL.Automation
{
    public static class AutomationSelfTest
    {
        private const string KEY = "AB_SELFTEST_RUN";
        private const int PORT = 14931;
        private static int pass, fail;
        private static ClientWebSocket ws;
        private static string last;

        private static void Log(string n, bool ok, string d = "")
        {
            if (ok) { pass++; Debug.Log($"AB_SELFTEST PASS {n} | {d}"); }
            else    { fail++; Debug.Log($"AB_SELFTEST FAIL {n} | {d}"); }
        }

        // -executeMethod entry: empty scene (skip DCL bootstrap) + enter Play.
        public static void EnterPlay()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            SessionState.SetBool(KEY, true);
            EditorApplication.EnterPlaymode();
        }

        [InitializeOnLoadMethod]
        private static void Hook()
        {
            EditorApplication.playModeStateChanged -= OnState;
            EditorApplication.playModeStateChanged += OnState;
        }

        private static void OnState(PlayModeStateChange s)
        {
            if (s == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool(KEY, false))
            {
                SessionState.SetBool(KEY, false);
                var go = new GameObject("AB_SelfTestRunner");
                go.AddComponent<Runner>().StartCoroutine(Run());
            }
        }

        private sealed class Runner : MonoBehaviour { }

        private static IEnumerator Run()
        {
            Debug.Log("AB_SELFTEST BEGIN");
            pass = 0; fail = 0;

            // --- minimal scene: a Light + a UITK Button ---
            var lightGo = new GameObject("TestLight", typeof(Light));
            lightGo.GetComponent<Light>().intensity = 1f;
            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            var docGo = new GameObject("TestUIDoc", typeof(UIDocument));
            var doc = docGo.GetComponent<UIDocument>();
            doc.panelSettings = panel;
            yield return null; // build rootVisualElement

            bool clicked = false;
            var btn = new Button { text = "PlayTestButton", name = "PlayTestButton" };
            btn.clicked += () => clicked = true;
            doc.rootVisualElement.Add(btn);
            yield return null;

            // --- start bridge ---
            AutomationBridgeHandler handler = null;
            try { handler = new AutomationBridgeHandler(port: PORT); }
            catch (Exception e) { Log("ctor", false, e.Message); }
            if (handler != null)
            {
                var sr = handler.Start();
                Log("bridge-start", !sr.IsBridgeStartError(out _));
            }
            yield return new WaitForSecondsRealtime(0.5f);

            // --- connect ---
            ws = new ClientWebSocket();
            var connect = ws.ConnectAsync(new Uri($"ws://127.0.0.1:{PORT}"), CancellationToken.None);
            yield return Until(connect, 6f);
            Log("ws-connect", ws.State == WebSocketState.Open, ws.State.ToString());

            if (ws.State == WebSocketState.Open)
            {
                yield return Rpc(1, "capabilities", "{}");
                Log("capabilities", last.Contains("\"ops\"") && last.Contains("click-at") && last.Contains("screenshot"), Trunc(last));

                yield return Rpc(2, "hierarchy", "{}");
                Log("hierarchy", last.Contains("TestLight"), Trunc(last));

                yield return Rpc(3, "list", "{}");
                Log("list-uitk", last.Contains("PlayTestButton"), Trunc(last));

                yield return Rpc(4, "click", "{\"text\":\"PlayTestButton\"}");
                Log("click-resp", last.Contains("\"ok\":true"), Trunc(last));
                yield return null;
                Log("click-fired", clicked);

                yield return Rpc(5, "component-get", "{\"object\":\"TestLight\",\"component\":\"Light\",\"member\":\"intensity\"}");
                Log("component-get", last.Contains("\"result\":1"), Trunc(last));

                yield return Rpc(6, "click-at", "{\"x\":\"120\",\"y\":\"120\"}");
                Log("click-at-inject", last.Contains("\"ok\":true"), Trunc(last));

                yield return Rpc(7, "screenshot", "{}", 15f);
                Log("screenshot", last.Contains("\"ok\":"), "ok=" + last.Contains("\"ok\":true") + " len=" + (last?.Length ?? 0));
            }

            try { ws?.Abort(); ws?.Dispose(); } catch { }
            try { handler?.Dispose(); } catch { }

            Debug.Log($"AB_SELFTEST SUMMARY pass={pass} fail={fail}");
            yield return null;
            EditorApplication.Exit(fail == 0 ? 0 : 1);
        }

        private static IEnumerator Rpc(int id, string method, string paramsJson, float timeout = 8f)
        {
            last = null;
            string msg = $"{{\"id\":{id},\"method\":\"Automation.{method}\",\"params\":{paramsJson}}}";
            var send = ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(msg)), WebSocketMessageType.Text, true, CancellationToken.None);
            yield return Until(send, timeout);
            var buf = new byte[1 << 20];
            var recv = ws.ReceiveAsync(new ArraySegment<byte>(buf), CancellationToken.None);
            yield return Until(recv, timeout);
            last = recv.Status == TaskStatus.RanToCompletion
                ? Encoding.UTF8.GetString(buf, 0, recv.Result.Count)
                : "{\"ok\":false,\"error\":\"no response within timeout\"}";
        }

        private static IEnumerator Until(Task t, float timeoutSeconds)
        {
            float end = Time.realtimeSinceStartup + timeoutSeconds;
            while (!t.IsCompleted && Time.realtimeSinceStartup < end) yield return null;
        }

        private static string Trunc(string s) => s == null ? "<null>" : s.Length > 220 ? s.Substring(0, 220) + "…" : s;
    }
}
#endif
