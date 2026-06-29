#if UNITY_EDITOR || DCL_AUTOMATION_BRIDGE   // stripped from release IL (see AutomationCore)
// AutomationScreenshot — composited screenshot (INCLUDING the UI overlay) returned as a base64 PNG.
//
// Mirrors DCL's own working recipe (ScreenRecorder.cs:54-77): a coroutine that yields WaitForEndOfFrame
// (so uGUI screen-space + UITK overlays are composited in — its comment: "for UI to appear on screenshot"),
// then ScreenCapture.CaptureScreenshotAsTexture(), then the Linear-colorspace round-trip into an RGB24
// Texture2D (Unity Linear-space capture bug workaround). IL2CPP-safe (ScreenCapture is runtime API).
//
// THREADING: AutomationBridgeHandler calls CaptureBase64 on the bridge BACKGROUND thread, then blocks a
// TaskCompletionSource (10s) until `done(json)` fires. So we marshal onto the main thread via
// MainThreadPump.Enqueue before StartCoroutine, and call `done` EXACTLY ONCE on every path (incl. errors)
// so the handler's wait can never hang.
using System;
using System.Collections;
using UnityEngine;

namespace DCL.Automation
{
    public static class AutomationScreenshot
    {
        public static void CaptureBase64(Action<string> done)
        {
            if (done == null) return;

            MainThreadPump pump = MainThreadPump.Instance;   // handler ctor calls Ensure(), so this is non-null in practice
            if (pump == null) { done(Err("MainThreadPump not initialized")); return; }

            // We are on the bridge background thread — hop to the main thread, then start the coroutine there.
            MainThreadPump.Enqueue(() =>
            {
                try { pump.StartCoroutine(CaptureRoutine(done)); }
                catch (Exception e) { done(Err("schedule failed: " + Clean(e.Message))); }
            });
        }

        private static IEnumerator CaptureRoutine(Action<string> done)
        {
            bool called = false;
            void Finish(string json) { if (called) return; called = true; try { done(json); } catch { } }

            // Must be AFTER end-of-frame so the UI is in the back buffer (same as ScreenRecorder).
            yield return new WaitForEndOfFrame();

            Texture2D shot = null, rgb = null;
            try
            {
                shot = ScreenCapture.CaptureScreenshotAsTexture();
                int w = shot.width, h = shot.height;

                // Linear -> RGB24 round-trip (correct colors in Linear color space, per ScreenRecorder).
                rgb = new Texture2D(w, h, TextureFormat.RGB24, false);
                rgb.SetPixels(shot.GetPixels());
                rgb.Apply();

                byte[] png = rgb.EncodeToPNG();
                string b64 = Convert.ToBase64String(png);
                Finish("{\"ok\":true,\"result\":{\"png_base64\":\"" + b64 + "\",\"w\":" + w + ",\"h\":" + h + ",\"bytes\":" + png.Length + "}}");
            }
            catch (Exception e) { Finish(Err("capture failed: " + Clean(e.Message))); }
            finally
            {
                if (shot != null) UnityEngine.Object.Destroy(shot);
                if (rgb != null) UnityEngine.Object.Destroy(rgb);
            }

            // Backstop: guarantee the handler's MRE is released even if the try somehow fell through.
            Finish(Err("capture produced no result"));
        }

        private static string Err(string m) => "{\"ok\":false,\"error\":\"" + Clean(m) + "\"}";
        private static string Clean(string s) => s == null ? "" : s.Replace('\\', '/').Replace('"', '\'');
    }
}
#endif
