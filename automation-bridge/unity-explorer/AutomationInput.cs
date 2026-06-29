#if UNITY_EDITOR || DCL_AUTOMATION_BRIDGE   // stripped from release IL (see AutomationCore)
// AutomationInput — raw input injection for the in-player automation bridge (BUILD STEP 3).
//
// Sibling of AutomationCore. AutomationCore.Dispatch routes the four raw-input ops
// ("click-at","key","type","swipe") here via AutomationInput.Handle(op, args). Dispatch — and
// therefore Handle — is already invoked on the Unity MAIN THREAD (AutomationBridgeHandler
// marshals the background bridge-thread call through MainThreadPump), so everything below runs
// on the main thread and may freely touch Unity / Input System API.
//
// HOW IT REACHES THE GAME
// -----------------------
// The client uses the *new* Input System and reads input from the CURRENT devices
// (DCL/Input/Utils/DCLInputUtilities.cs reads Pointer/Mouse/Touchscreen.current.position; the
// DCLInput.inputactions bindings resolve against those same devices). We therefore inject device
// STATE onto Mouse/Keyboard/Touchscreen.current via UnityEngine.InputSystem.LowLevel state
// structs + InputSystem.QueueStateEvent. One injection reaches BOTH:
//   * gameplay InputActions (DCLInput), and
//   * UI Toolkit, via InputSystemUIInputModule (which itself reads those devices/actions).
// This is the runtime, IL2CPP-safe path — NOT InputTestFixture, which is editor/test-only.
//
// ASSEMBLY WIRING (REQUIRED — this file will not compile without it)
// -----------------------------------------------------------------
// These ops live in the DCL.Network assembly (the WebRequests folder is an .asmref into it,
// alongside ChromeDevToolHandler / AutomationBridgeHandler). DCL.Network does NOT currently
// reference the Input System package. Add ONE reference to
//   Explorer/Assets/DCL/NetworkDefinitions/DCL.Network.asmdef  ->  "references":
//   "GUID:75469ad4d38634e559750d17036d5f7c"   // Unity.InputSystem (same GUID DCL.Input uses)
// (UnityEngine / UnityEngine.UIElements are already referenced there.) See WIRING.md.
//
// TIMING / CONSUMPTION CAVEAT (read this)
// ---------------------------------------
// QueueStateEvent only ENQUEUES; the event is applied during the next Input System update (run by
// the player loop, before MonoBehaviour.Update). We deliberately DO NOT call InputSystem.Update()
// to force a flush: Handle runs from inside MainThreadPump.Update (mid-frame), and forcing an input
// update there re-enters the input pipeline mid-frame (re-entrant action callbacks / double
// processing) — a known footgun. Instead we queue and let the events be consumed naturally on the
// next frame's input update (~1 frame latency). For a click/keypress we queue press AND release in
// the same frame: the action system processes each state-change event in order, so consumers still
// observe a full down->up transition (i.e. a real click / keypress) within that single update.
// Sustained gestures that must span real time (key `hold`, `swipe` `ms`) are spread across frames
// with a coroutine on MainThreadPump, letting each frame's natural input update consume one step.
//
// COORDINATES: x/y are screen pixels with origin BOTTOM-LEFT (Input System convention, matching
// .position.ReadValue()). Callers working in top-left/CSS pixels must flip y = screenHeight - y.
//
// Returns a JSON string: {"ok":true,"result":...} | {"ok":false,"error":...}. The JSON helpers are
// local (this file does not depend on AutomationCore's private helpers).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;
using TouchPhase = UnityEngine.InputSystem.TouchPhase; // disambiguate from UnityEngine.TouchPhase

namespace DCL.Automation
{
    public static class AutomationInput
    {
        // Single entry point for the raw-input ops. `args` is the same flat string map AutomationCore
        // uses. Always returns a JSON envelope; never throws to the caller.
        public static string Handle(string op, IReadOnlyDictionary<string, string> args)
        {
            try
            {
                string A(string k) => args != null && args.TryGetValue(k, out var v) ? v : null;
                float Af(string k) => float.TryParse(A(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : float.NaN;
                int Ai(string k, int d) => int.TryParse(A(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : d;

                switch (op)
                {
                    case "click-at": return ClickAt(Af("x"), Af("y"), A("button"));
                    case "key":      return KeyPress(A("key"), Ai("hold", 0));
                    case "type":     return TypeText(A("text"));
                    case "swipe":    return Swipe(Af("x1"), Af("y1"), Af("x2"), Af("y2"), Ai("ms", 300));
                    default:         return Err("unknown input op: " + op);
                }
            }
            catch (Exception e) { return Err("input threw: " + (e.InnerException?.Message ?? e.Message)); }
        }

        // ---- click-at ------------------------------------------------------------------------
        // Move the mouse to (x,y) so hover/enter registers, then press and release `button`.
        private static string ClickAt(float x, float y, string button)
        {
            if (float.IsNaN(x) || float.IsNaN(y)) return Err("click-at needs numeric x and y (screen px, origin bottom-left)");
            if (!ParseButton(button, out MouseButton mb, out string bErr)) return Err(bErr);

            // Ensure a Mouse device exists; use the returned/captured reference (don't re-read
            // Mouse.current, which only becomes non-null once the device receives input).
            Mouse mouse = Mouse.current ?? InputSystem.AddDevice<Mouse>();
            var pos = new Vector2(x, y);

            // 3 events, consumed together next input update: move -> button down -> button up.
            // The down->up transition is what the action system / UITK module reads as a click.
            InputSystem.QueueStateEvent(mouse, new MouseState { position = pos });
            InputSystem.QueueStateEvent(mouse, new MouseState { position = pos }.WithButton(mb, true));
            InputSystem.QueueStateEvent(mouse, new MouseState { position = pos }.WithButton(mb, false));
            return Ok("click-at (" + Fmt(x) + "," + Fmt(y) + ") button=" + mb + " (queued; consumed next input update)");
        }

        private static bool ParseButton(string s, out MouseButton mb, out string err)
        {
            err = null; mb = MouseButton.Left;
            if (string.IsNullOrEmpty(s)) return true;
            switch (s.Trim().ToLowerInvariant())
            {
                case "left":   case "l": mb = MouseButton.Left;   return true;
                case "right":  case "r": mb = MouseButton.Right;  return true;
                case "middle": case "m": mb = MouseButton.Middle; return true;
                default: err = "unknown button '" + s + "' (use left/right/middle)"; return false;
            }
        }

        // ---- key -----------------------------------------------------------------------------
        // Press a Key by its UnityEngine.InputSystem.Key name (e.g. "Enter","W","Space"). With an
        // optional hold (ms) the release is scheduled on a coroutine so the key stays down across
        // frames; without it press+release are queued into the same frame (a tap).
        private static string KeyPress(string keyName, int holdMs)
        {
            if (string.IsNullOrEmpty(keyName)) return Err("key needs key=<Name> (UnityEngine.InputSystem.Key, e.g. Enter, W, Space)");
            if (!Enum.TryParse<Key>(keyName, true, out Key key) || key == Key.None)
                return Err("unknown key '" + keyName + "' (use a UnityEngine.InputSystem.Key name, e.g. Enter, W, Space)");

            Keyboard kbd = Keyboard.current ?? InputSystem.AddDevice<Keyboard>();

            var down = new KeyboardState();
            down.Set(key, true);
            InputSystem.QueueStateEvent(kbd, down);

            if (holdMs > 0 && MainThreadPump.Instance != null)
            {
                MainThreadPump.Instance.StartCoroutine(ReleaseKeyAfter(kbd, holdMs / 1000f));
                return Ok("key " + key + " down (release queued after " + holdMs + "ms)");
            }

            InputSystem.QueueStateEvent(kbd, new KeyboardState()); // empty == all keys released
            return Ok("key " + key + " tap (queued; consumed next input update)");
        }

        private static IEnumerator ReleaseKeyAfter(Keyboard kbd, float seconds)
        {
            yield return new WaitForSecondsRealtime(seconds); // realtime: key still releases at timeScale 0 (paused/loading)
            InputSystem.QueueStateEvent(kbd, new KeyboardState()); // release everything
        }

        // ---- type ----------------------------------------------------------------------------
        // Enter a string. IMPORTANT LIMITATION: the new Input System does NOT synthesize the
        // character/text-input stream (Keyboard.onTextInput) from queued *state* events — state
        // events drive key press/release (so gameplay InputActions and key-based UI navigation see
        // them), but a UI Toolkit TextField fills its text from the text-input stream, not key
        // state. So:
        //   (1) ROBUST path: if a UITK TextField currently has focus, set its .value directly
        //       (this is what AutomationCore's "text" op does for a named field).
        //   (2) FALLBACK device path: best-effort per-character keyboard state events. Useful for
        //       key-driven UI / gameplay, but will NOT populate an unfocused/absent UITK field.
        // For reliable text into a specific field, prefer the "text" op (AutomationCore.TextInput).
        private static string TypeText(string text)
        {
            if (text == null) return Err("type needs text");

            TextField tf = FocusedTextField();
            if (tf != null)
            {
                tf.value = (tf.value ?? string.Empty) + text;
                string name = string.IsNullOrEmpty(tf.name) ? tf.GetType().Name : tf.name;
                return Ok("typed " + text.Length + " char(s) into focused TextField '" + name + "' (UITK value path)");
            }

            Keyboard kbd = Keyboard.current ?? InputSystem.AddDevice<Keyboard>();
            int mapped = 0;
            foreach (char ch in text)
            {
                if (!MapChar(ch, out Key key, out bool shift)) continue;
                var down = new KeyboardState();
                if (shift) down.Set(Key.LeftShift, true);
                down.Set(key, true);
                InputSystem.QueueStateEvent(kbd, down);
                InputSystem.QueueStateEvent(kbd, new KeyboardState()); // release
                mapped++;
            }
            return Ok("typed " + mapped + "/" + text.Length + " char(s) as keyboard state events (no focused TextField; "
                      + "state events do not fill UITK fields — use the 'text' op for those)");
        }

        // Returns the currently focused UITK TextField across all UIDocuments, or null. A TextField's
        // inner text-input element may itself hold focus, so we walk the parent chain to the owner.
        private static TextField FocusedTextField()
        {
            foreach (var doc in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                VisualElement root = doc != null ? doc.rootVisualElement : null;
                IPanel panel = root != null ? root.panel : null;
                Focusable focused = panel != null && panel.focusController != null ? panel.focusController.focusedElement : null;
                if (focused == null) continue;
                if (focused is TextField direct) return direct;
                for (var p = focused as VisualElement; p != null; p = p.parent)
                    if (p is TextField owner) return owner;
            }
            return null;
        }

        // Map an ASCII char to (Key, shift) on a US layout. Returns false for unmapped chars.
        // NB: in the Key enum, A..Z are contiguous, and Digit1..Digit9 are contiguous but Digit0
        // comes AFTER Digit9 — so '0' is handled separately, not via Digit0 + offset.
        private static bool MapChar(char c, out Key key, out bool shift)
        {
            shift = false; key = Key.None;
            if (c >= 'a' && c <= 'z') { key = Key.A + (c - 'a'); return true; }
            if (c >= 'A' && c <= 'Z') { key = Key.A + (c - 'A'); shift = true; return true; }
            if (c == '0') { key = Key.Digit0; return true; }
            if (c >= '1' && c <= '9') { key = Key.Digit1 + (c - '1'); return true; }
            switch (c)
            {
                case ' ':  key = Key.Space; return true;
                case '\n': case '\r': key = Key.Enter; return true;
                case '\t': key = Key.Tab; return true;
                // unshifted punctuation
                case '-':  key = Key.Minus;        return true;
                case '=':  key = Key.Equals;       return true;
                case '[':  key = Key.LeftBracket;  return true;
                case ']':  key = Key.RightBracket; return true;
                case '\\': key = Key.Backslash;    return true;
                case ';':  key = Key.Semicolon;    return true;
                case '\'': key = Key.Quote;        return true;
                case ',':  key = Key.Comma;        return true;
                case '.':  key = Key.Period;       return true;
                case '/':  key = Key.Slash;        return true;
                case '`':  key = Key.Backquote;    return true;
                // shifted punctuation (US layout)
                case '_':  key = Key.Minus;        shift = true; return true;
                case '+':  key = Key.Equals;       shift = true; return true;
                case '{':  key = Key.LeftBracket;  shift = true; return true;
                case '}':  key = Key.RightBracket; shift = true; return true;
                case '|':  key = Key.Backslash;    shift = true; return true;
                case ':':  key = Key.Semicolon;    shift = true; return true;
                case '"':  key = Key.Quote;        shift = true; return true;
                case '<':  key = Key.Comma;        shift = true; return true;
                case '>':  key = Key.Period;       shift = true; return true;
                case '?':  key = Key.Slash;        shift = true; return true;
                case '~':  key = Key.Backquote;    shift = true; return true;
                // shifted digits
                case '!':  key = Key.Digit1; shift = true; return true;
                case '@':  key = Key.Digit2; shift = true; return true;
                case '#':  key = Key.Digit3; shift = true; return true;
                case '$':  key = Key.Digit4; shift = true; return true;
                case '%':  key = Key.Digit5; shift = true; return true;
                case '^':  key = Key.Digit6; shift = true; return true;
                case '&':  key = Key.Digit7; shift = true; return true;
                case '*':  key = Key.Digit8; shift = true; return true;
                case '(':  key = Key.Digit9; shift = true; return true;
                case ')':  key = Key.Digit0; shift = true; return true;
                default: return false;
            }
        }

        // ---- swipe ---------------------------------------------------------------------------
        // A single-finger Touchscreen gesture (touchId 1): Began at (x1,y1) -> interpolated Moved
        // steps -> Ended at (x2,y2). Spread across `ms` via a coroutine when a pump exists, so a
        // gesture recognizer sees real movement over time rather than a teleport. (We inject onto
        // Touchscreen.current — adding one if absent — per spec; a mouse-drag variant could be added
        // for desktop gameplay that reads the mouse, but is intentionally left out to avoid
        // double-input.)
        private const int SWIPE_STEPS = 8;

        private static string Swipe(float x1, float y1, float x2, float y2, int ms)
        {
            if (float.IsNaN(x1) || float.IsNaN(y1) || float.IsNaN(x2) || float.IsNaN(y2))
                return Err("swipe needs numeric x1,y1,x2,y2 (screen px, origin bottom-left)");
            if (ms < 0) ms = 0;

            Touchscreen touch = Touchscreen.current ?? InputSystem.AddDevice<Touchscreen>();
            var from = new Vector2(x1, y1);
            var to = new Vector2(x2, y2);

            if (ms > 0 && MainThreadPump.Instance != null)
            {
                MainThreadPump.Instance.StartCoroutine(SwipeRoutine(touch, from, to, ms / 1000f, SWIPE_STEPS));
                return Ok("swipe (" + Fmt(x1) + "," + Fmt(y1) + ")->(" + Fmt(x2) + "," + Fmt(y2)
                          + ") over " + ms + "ms via Touchscreen (queued)");
            }

            // Synchronous fallback (ms==0 or no pump): whole gesture into this frame's buffer.
            InputSystem.QueueStateEvent(touch, Touch(TouchPhase.Began, from));
            for (int i = 1; i < SWIPE_STEPS; i++)
                InputSystem.QueueStateEvent(touch, Touch(TouchPhase.Moved, Vector2.Lerp(from, to, i / (float)SWIPE_STEPS)));
            InputSystem.QueueStateEvent(touch, Touch(TouchPhase.Ended, to));
            return Ok("swipe (" + Fmt(x1) + "," + Fmt(y1) + ")->(" + Fmt(x2) + "," + Fmt(y2)
                      + ") instantaneous (ms<=0 or no pump) via Touchscreen");
        }

        private static IEnumerator SwipeRoutine(Touchscreen touch, Vector2 from, Vector2 to, float seconds, int steps)
        {
            InputSystem.QueueStateEvent(touch, Touch(TouchPhase.Began, from));
            float dt = steps > 0 ? seconds / steps : seconds;
            for (int i = 1; i <= steps; i++)
            {
                yield return new WaitForSecondsRealtime(dt); // realtime: swipe completes even at timeScale 0
                var p = Vector2.Lerp(from, to, i / (float)steps);
                InputSystem.QueueStateEvent(touch, Touch(i < steps ? TouchPhase.Moved : TouchPhase.Ended, p));
            }
        }

        private static TouchState Touch(TouchPhase phase, Vector2 p) =>
            new TouchState { touchId = 1, phase = phase, position = p };

        // ---- JSON helpers (local; independent of AutomationCore) ------------------------------
        private static string Ok(string result) => "{\"ok\":true,\"result\":" + JsonString(result) + "}";
        private static string Err(string msg) => "{\"ok\":false,\"error\":" + JsonString(msg) + "}";
        private static string Fmt(float f) => f.ToString("0.###", CultureInfo.InvariantCulture);

        private static string JsonString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (char ch in s)
                switch (ch)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default: if (ch < 0x20) sb.AppendFormat("\\u{0:x4}", (int)ch); else sb.Append(ch); break;
                }
            return sb.Append('"').ToString();
        }
    }
}
#endif
