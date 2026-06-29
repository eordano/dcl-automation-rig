// MainThreadPump — marshals work from background threads onto the Unity main thread.
//
// The CDP bridge (decentraland/chrome-devtool-protocol-unity) invokes handleMethod on a
// BACKGROUND thread, but the Unity API is main-thread-only. AutomationBridgeHandler enqueues
// the actual work here and blocks until Update() drains it on the main thread.
//
// It is a MonoBehaviour (not just a static queue) so that coroutine-based ops — e.g.
// AutomationScreenshot, which needs WaitForEndOfFrame — can do
//     MainThreadPump.Instance.StartCoroutine(...)
// from inside an enqueued (main-thread) callback.
//
// Lives outside the #if !UNITY_WEBGL guard so the screenshot/input helpers can rely on it on
// every platform; it is an inert MonoBehaviour with no networking, safe to compile anywhere.
using System;
using System.Collections.Concurrent;
using UnityEngine;

namespace DCL.Automation
{
    internal sealed class MainThreadPump : MonoBehaviour
    {
        private static readonly ConcurrentQueue<Action> Queue = new();

        /// <summary>The live pump component. Non-null after <see cref="Ensure"/> has run on the main thread.</summary>
        public static MainThreadPump Instance { get; private set; }

        /// <summary>Create the DontDestroyOnLoad pump GameObject if it doesn't exist. Main thread only.</summary>
        public static void Ensure()
        {
            if (Instance != null) return;
            var go = new GameObject("DclAutomationPump") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<MainThreadPump>();
        }

        /// <summary>Queue an action to run on the next main-thread Update(). Callable from any thread.</summary>
        public static void Enqueue(Action a)
        {
            if (a != null) Queue.Enqueue(a);
        }

        private void Update()
        {
            while (Queue.TryDequeue(out Action a))
            {
                try { a(); }
                catch (Exception e) { Debug.LogWarning("[AutomationPump] " + e); } // full e (incl. stack), not just .Message
            }
        }
    }
}
