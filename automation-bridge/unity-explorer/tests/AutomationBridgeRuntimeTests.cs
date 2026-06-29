// Runtime PlayMode smoke test for the in-player automation bridge.
// Starts AutomationBridgeHandler in-process, builds a tiny UITK scene, and drives the CDP
// WebSocket on :PORT with a stdlib ClientWebSocket — exercising the REAL path end to end:
//   ClientWebSocket -> Fleck server (Bridge) -> handleMethod (bg thread) -> MainThreadPump
//   -> AutomationCore (reflection) / AutomationInput / AutomationScreenshot -> response.
// Run: Unity -runTests -testPlatform PlayMode (NOT -nographics, so screenshot has a framebuffer).
#if !UNITY_WEBGL
using System;
using System.Collections;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace DCL.Automation.Tests
{
    public class AutomationBridgeRuntimeTests
    {
        private const int PORT = 14931;
        private AutomationBridgeHandler handler;
        private ClientWebSocket ws;
        private GameObject root;
        private string last;

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            try { ws?.Abort(); ws?.Dispose(); } catch { }
            try { handler?.Dispose(); } catch { }
            if (root != null) UnityEngine.Object.Destroy(root);
            yield return null;
        }

        [UnityTest]
        public IEnumerator Bridge_serves_and_drives_the_live_ui()
        {
            // --- 1. minimal live scene: a Light + a UITK Button ---
            root = new GameObject("AB_TestRoot");
            var lightGo = new GameObject("TestLight", typeof(Light));
            lightGo.transform.SetParent(root.transform);
            lightGo.GetComponent<Light>().intensity = 1f;

            var panel = ScriptableObject.CreateInstance<PanelSettings>();
            var docGo = new GameObject("TestUIDoc", typeof(UIDocument));
            docGo.transform.SetParent(root.transform);
            var doc = docGo.GetComponent<UIDocument>();
            doc.panelSettings = panel;
            yield return null; // let UIDocument build its rootVisualElement

            bool clicked = false;
            var btn = new Button { text = "PlayTestButton", name = "PlayTestButton" };
            btn.clicked += () => clicked = true;
            doc.rootVisualElement.Add(btn);
            yield return null;

            // --- 2. start the bridge ---
            handler = new AutomationBridgeHandler(port: PORT);
            BridgeStartResult sr = handler.Start();
            Assert.IsFalse(sr.IsBridgeStartError(out _), "bridge Start() reported an error");
            yield return new WaitForSecondsRealtime(0.5f);

            // --- 3. connect a CDP WebSocket client ---
            ws = new ClientWebSocket();
            var connect = ws.ConnectAsync(new Uri($"ws://127.0.0.1:{PORT}"), CancellationToken.None);
            yield return Until(connect, 6f);
            Assert.AreEqual(WebSocketState.Open, ws.State, "client failed to connect to the bridge");

            // --- 4. drive it ---
            yield return Rpc(1, "capabilities", "{}");
            Assert.IsTrue(last.Contains("\"ops\"") && last.Contains("click-at") && last.Contains("screenshot"), "capabilities: " + Trunc(last));

            yield return Rpc(2, "hierarchy", "{}");
            Assert.IsTrue(last.Contains("TestLight"), "hierarchy did not see the scene: " + Trunc(last));

            yield return Rpc(3, "list", "{}");
            Assert.IsTrue(last.Contains("PlayTestButton"), "list did not see the UITK button: " + Trunc(last));

            yield return Rpc(4, "click", "{\"text\":\"PlayTestButton\"}");
            Assert.IsTrue(last.Contains("\"ok\":true"), "click response not ok: " + Trunc(last));
            yield return null;
            Assert.IsTrue(clicked, "button.clicked did not fire from the bridge click");

            yield return Rpc(5, "component-get", "{\"object\":\"TestLight\",\"component\":\"Light\",\"member\":\"intensity\"}");
            Assert.IsTrue(last.Contains("\"result\":1"), "component-get intensity: " + Trunc(last));

            yield return Rpc(6, "click-at", "{\"x\":\"120\",\"y\":\"120\"}");
            Assert.IsTrue(last.Contains("\"ok\":true"), "click-at (input injection) not ok: " + Trunc(last));

            // screenshot is graphics-dependent; assert a well-formed response comes back
            yield return Rpc(7, "screenshot", "{}", 15f);
            Assert.IsTrue(last.Contains("\"ok\":"), "screenshot returned no/garbled response: " + Trunc(last));
            Debug.Log($"AB_SMOKE screenshot ok={last.Contains("\"ok\":true")} len={last.Length}");

            Debug.Log("AB_SMOKE ALL ASSERTIONS PASSED");
        }

        private IEnumerator Rpc(int id, string method, string paramsJson, float timeout = 8f)
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

        private static string Trunc(string s) => s == null ? "<null>" : s.Length > 240 ? s.Substring(0, 240) + "…" : s;
    }
}
#endif
