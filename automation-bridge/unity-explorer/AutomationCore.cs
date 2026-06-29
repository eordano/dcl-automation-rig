#if UNITY_EDITOR || DCL_AUTOMATION_BRIDGE   // editor (ClaudeIPC reflection) OR define-gated dev/automation build; stripped from release IL
// AutomationCore — player-safe, reflection-only UI/automation command engine.
//
// Factored out of the editor-only ClaudeIPC so the SAME command implementations run in
// BOTH the Unity Editor (ClaudeIPC file-pipe) AND a built player (AutomationBridgeHandler
// over the CDP WebSocket bridge). No UnityEditor dependency -> compiles into the player.
//
// CONTRACT: every method here touches Unity API (FindObjectsByType, VisualElement, etc.)
// and therefore MUST be called on the Unity MAIN THREAD. The bridge handler marshals to
// the main thread before calling Dispatch (the CDP bridge invokes handlers off-thread).
//
// Ops: capabilities, find, click, list, hierarchy, component-get, component-set, text, exec.
// Returns a JSON string ({"ok":true,"result":...} | {"ok":false,"error":...}).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace DCL.Automation
{
    public static partial class AutomationCore
    {
        public static readonly string[] Ops =
        {
            "capabilities", "find", "click", "list", "hierarchy", "component-get", "component-set", "text", "exec",
            // input ops -> AutomationInput.Handle (sibling file); "screenshot" -> async path on AutomationBridgeHandler
            "click-at", "key", "type", "swipe", "screenshot",
        };

        // Single entry point. `args` mirrors ClaudeIPC's flat string map (incl. "arg.*" for exec).
        public static string Dispatch(string op, IReadOnlyDictionary<string, string> args)
        {
            try
            {
                string A(string k) => args != null && args.TryGetValue(k, out var v) ? v : null;
                int Ai(string k, int d) => int.TryParse(A(k), out var n) ? n : d;
                switch (op)
                {
                    case "capabilities":  return Capabilities();
                    case "find":          return Find(A("text"), A("scope"));
                    case "list":          return UiList(A("scope"));
                    case "click":         return UiClick(A("text"), A("scope"));
                    case "hierarchy":     return Hierarchy(A("scope"), Ai("maxDepth", 40), Ai("max", 4000));
                    case "component-get": return ComponentGet(A("object"), A("component"), A("member"));
                    case "component-set": return ComponentSet(A("object"), A("component"), A("member"), A("value"));
                    case "text":          return TextInput(A("target"), A("value"));
                    case "exec":          return Exec(A("method"), args);
                    // Raw input injection lives in AutomationInput.cs (Input System backend, sibling file).
                    case "click-at":
                    case "key":
                    case "type":
                    case "swipe":         return AutomationInput.Handle(op, args);
                    // NOTE: "screenshot" is intentionally NOT routed here — it needs WaitForEndOfFrame
                    // (a coroutine) and is served on the async path in AutomationBridgeHandler.
                    default:              return Err("unknown op: " + op);
                }
            }
            catch (Exception e) { return Err("dispatch threw: " + (e.InnerException?.Message ?? e.Message)); }
        }

        private static string Capabilities()
        {
            var sb = new StringBuilder("{\"ok\":true,\"result\":{\"version\":\"1\",\"runtime\":");
            sb.Append(JsonString(Application.platform.ToString()))
              .Append(",\"unity\":").Append(JsonString(Application.unityVersion))
              .Append(",\"ops\":[");
            for (int i = 0; i < Ops.Length; i++) { if (i > 0) sb.Append(','); sb.Append(JsonString(Ops[i])); }
            return sb.Append("]}}").ToString();
        }

        // ---- UI Toolkit (the current client is 100% UITK) -----------------------------------
        private static List<VisualElement> AllVisualElements()
        {
            var all = new List<VisualElement>();
            foreach (var doc in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                var root = doc != null ? doc.rootVisualElement : null;
                if (root == null) continue;
                all.AddRange(root.Query<VisualElement>().ToList());
            }
            return all;
        }
        private static bool HasClickable(VisualElement ve)
        {
            var f = ve.GetType().GetField("m_Clickable", BindingFlags.Instance | BindingFlags.NonPublic);
            return f != null && f.GetValue(ve) != null;
        }
        private static bool VeClickable(VisualElement ve) => ve is Button || ve is Toggle || HasClickable(ve);
        private static string VeText(VisualElement ve)
        {
            switch (ve) { case Button b: return b.text; case Label l: return l.text; case TextElement t: return t.text; }
            var p = ve.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.PropertyType == typeof(string)) { try { return p.GetValue(ve) as string; } catch { } }
            return null;
        }
        private static string VePath(VisualElement ve)
        {
            var sb = new StringBuilder(string.IsNullOrEmpty(ve.name) ? ve.GetType().Name : ve.name);
            for (var p = ve.parent; p != null; p = p.parent)
                sb.Insert(0, (string.IsNullOrEmpty(p.name) ? p.GetType().Name : p.name) + "/");
            return sb.ToString();
        }
        private static bool ClickVe(VisualElement ve, out string err)
        {
            err = null;
            try
            {
                var clk = ve.GetType().GetField("m_Clickable", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(ve);
                if (clk != null)
                {
                    var del = clk.GetType().GetField("clicked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(clk) as Delegate;
                    if (del != null) { del.DynamicInvoke(); return true; }
                }
                if (ve is Toggle tg) { tg.value = !tg.value; return true; }
                err = "no Clickable on " + ve.GetType().Name; return false;
            }
            catch (Exception e) { err = e.InnerException?.Message ?? e.Message; return false; }
        }

        // ---- uGUI is OPTIONAL (reflection-only; not referenced by the DCL.Network asmdef — the client DOES ship com.unity.ugui) ----
        private static List<Component> UGuiButtons()
        {
            var list = new List<Component>();
            Type t = ResolveType("UnityEngine.UI.Button");
            if (t == null) return list;
            foreach (var o in Resources.FindObjectsOfTypeAll(t))
            {
                var c = o as Component;
                if (c != null && c.gameObject.scene.IsValid() && c.gameObject.activeInHierarchy) list.Add(c);
            }
            return list;
        }
        private static bool BtnInteractable(Component b)
        {
            try { var p = b.GetType().GetProperty("interactable", BindingFlags.Public | BindingFlags.Instance); return p == null || !(p.GetValue(b) is bool bo) || bo; }
            catch { return true; }
        }
        private static string BtnInvoke(Component b)
        {
            try
            {
                var oc = b.GetType().GetProperty("onClick", BindingFlags.Public | BindingFlags.Instance)?.GetValue(b);
                var inv = oc?.GetType().GetMethod("Invoke", Type.EmptyTypes);
                if (inv == null) return "no onClick.Invoke()";
                inv.Invoke(oc, null); return null;
            }
            catch (Exception e) { return e.InnerException?.Message ?? e.Message; }
        }
        private static string ButtonLabel(Component b)
        {
            foreach (var c in b.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var t = c.GetType();
                if (!t.Name.ToLowerInvariant().Contains("text") && !t.Name.ToLowerInvariant().Contains("label")) continue;
                var prop = t.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                if (prop == null || prop.PropertyType != typeof(string)) continue;
                try { var v = prop.GetValue(c) as string; if (!string.IsNullOrWhiteSpace(v)) return v.Trim(); } catch { }
            }
            return null;
        }

        private static string Find(string text, string scope) => UiList(scope);   // alias; list already filters by scope
        private static string UiList(string scope)
        {
            var sb = new StringBuilder("{\"ok\":true,\"result\":["); bool first = true;
            foreach (var b in UGuiButtons())
            {
                string path = GetGoPath(b.gameObject);
                if (!string.IsNullOrEmpty(scope) && path.IndexOf(scope, StringComparison.OrdinalIgnoreCase) < 0) continue;
                AppendEntry(sb, ref first, ButtonLabel(b) ?? "", path, BtnInteractable(b), false);
            }
            foreach (var ve in AllVisualElements())
            {
                if (!VeClickable(ve)) continue;
                string path = VePath(ve);
                if (!string.IsNullOrEmpty(scope) && path.IndexOf(scope, StringComparison.OrdinalIgnoreCase) < 0) continue;
                AppendEntry(sb, ref first, VeText(ve) ?? "", path, ve.enabledInHierarchy, true);
            }
            return sb.Append("]}").ToString();
        }
        private static void AppendEntry(StringBuilder sb, ref bool first, string label, string path, bool inter, bool tk)
        {
            if (!first) sb.Append(','); first = false;
            sb.Append("{\"label\":").Append(JsonString(label)).Append(",\"path\":").Append(JsonString(path))
              .Append(",\"interactable\":").Append(inter ? "true" : "false").Append(",\"tk\":").Append(tk ? "true" : "false").Append('}');
        }

        private static string UiClick(string text, string scope)
        {
            if (string.IsNullOrEmpty(text)) return Err("missing text");
            // uGUI first
            var hits = new List<Component>();
            foreach (var b in UGuiButtons())
            {
                string label = ButtonLabel(b);
                if (string.IsNullOrEmpty(label) || label.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!string.IsNullOrEmpty(scope) && GetGoPath(b.gameObject).IndexOf(scope, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (BtnInteractable(b)) hits.Add(b);
            }
            if (hits.Count == 1) { var e = BtnInvoke(hits[0]); return e == null ? Ok("clicked \"" + ButtonLabel(hits[0]) + "\"") : Err("onClick threw: " + e); }
            if (hits.Count > 1) return Err("ambiguous (uGUI): " + hits.Count + " match \"" + text + "\"");
            // UI Toolkit
            var tk = new List<VisualElement>();
            foreach (var ve in AllVisualElements())
            {
                if (!VeClickable(ve)) continue;
                string label = VeText(ve);
                if (string.IsNullOrEmpty(label) || label.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!string.IsNullOrEmpty(scope) && VePath(ve).IndexOf(scope, StringComparison.OrdinalIgnoreCase) < 0) continue;
                tk.Add(ve);
            }
            if (tk.Count == 1) return ClickVe(tk[0], out var e) ? Ok("clicked (UITK) \"" + (VeText(tk[0]) ?? "") + "\" @ " + VePath(tk[0])) : Err("UITK click failed: " + e);
            if (tk.Count > 1) return Err("ambiguous (UITK): " + tk.Count + " match \"" + text + "\"");
            return Err("no interactable element matches text=\"" + text + "\"");
        }

        private static string TextInput(string target, string value)
        {
            if (string.IsNullOrEmpty(target)) return Err("text needs target");
            value ??= "";
            foreach (var ve in AllVisualElements())
                if (ve is TextField tf && (ve.name == target || VePath(ve).EndsWith("/" + target, StringComparison.Ordinal)))
                { tf.value = value; return Ok("set UITK TextField '" + target + "'"); }
            var go = FindGo(target);
            if (go != null)
                foreach (var c in go.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || !c.GetType().Name.Contains("InputField")) continue;
                    var prop = c.GetType().GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                    if (prop != null && prop.CanWrite) { prop.SetValue(c, value); return Ok("set " + c.GetType().Name + ".text on " + target); }
                }
            return Err("no TextField/InputField named " + target);
        }

        private static string Hierarchy(string scope, int maxDepth, int maxNodes)
        {
            var sb = new StringBuilder("{\"ok\":true,\"result\":{\"gameObjects\":["); int count = 0; bool first = true;
            void Walk(Transform tr, int depth)
            {
                if (count >= maxNodes || depth > maxDepth) return;
                var go = tr.gameObject; string path = GetGoPath(go);
                if (string.IsNullOrEmpty(scope) || path.IndexOf(scope, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (!first) sb.Append(','); first = false; count++;
                    sb.Append("{\"name\":").Append(JsonString(go.name)).Append(",\"depth\":").Append(depth)
                      .Append(",\"active\":").Append(go.activeInHierarchy ? "true" : "false").Append(",\"path\":").Append(JsonString(path)).Append('}');
                }
                for (int i = 0; i < tr.childCount && count < maxNodes; i++) Walk(tr.GetChild(i), depth + 1);
            }
            for (int s = 0; s < SceneManager.sceneCount && count < maxNodes; s++)
                foreach (var root in SceneManager.GetSceneAt(s).GetRootGameObjects()) Walk(root.transform, 0);
            sb.Append("],\"uitk\":["); bool fve = true; int vc = 0;
            foreach (var ve in AllVisualElements())
            {
                if (vc >= maxNodes) break; string path = VePath(ve);
                if (!string.IsNullOrEmpty(scope) && path.IndexOf(scope, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!fve) sb.Append(','); fve = false; vc++;
                sb.Append("{\"type\":").Append(JsonString(ve.GetType().Name)).Append(",\"name\":").Append(JsonString(ve.name ?? ""))
                  .Append(",\"text\":").Append(JsonString(VeText(ve) ?? "")).Append(",\"clickable\":").Append(VeClickable(ve) ? "true" : "false")
                  .Append(",\"path\":").Append(JsonString(path)).Append('}');
            }
            return sb.Append("]}}").ToString();
        }

        // ---- component + exec ----------------------------------------------------------------
        private static GameObject FindGo(string nameOrPath)
        {
            if (string.IsNullOrEmpty(nameOrPath)) return null;
            foreach (var go in UnityEngine.Object.FindObjectsByType<GameObject>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (go.name == nameOrPath || GetGoPath(go) == nameOrPath || GetGoPath(go).EndsWith("/" + nameOrPath, StringComparison.Ordinal)) return go;
            return null;
        }
        private static string ComponentGet(string obj, string comp, string member)
        {
            if (string.IsNullOrEmpty(obj) || string.IsNullOrEmpty(member)) return Err("component-get needs object + member");
            var go = FindGo(obj); if (go == null) return Err("GameObject not found: " + obj);
            var c = ResolveComponent(go, comp, out var e); if (c == null) return Err(e);
            var v = GetMemberValue(c, member, out var me); if (me != null) return Err(me);
            return "{\"ok\":true,\"result\":" + JsonValue(v) + "}";
        }
        private static string ComponentSet(string obj, string comp, string member, string value)
        {
            if (string.IsNullOrEmpty(obj) || string.IsNullOrEmpty(member)) return Err("component-set needs object, member, value");
            var go = FindGo(obj); if (go == null) return Err("GameObject not found: " + obj);
            var c = ResolveComponent(go, comp, out var e); if (c == null) return Err(e);
            var se = SetMemberValue(c, member, value); if (se != null) return Err(se);
            return Ok("set " + comp + "." + member + " = " + value + " on " + obj);
        }
        private static Component ResolveComponent(GameObject go, string compName, out string err)
        {
            err = null;
            if (string.IsNullOrEmpty(compName)) { err = "specify component=<TypeName>"; return null; }
            foreach (var c in go.GetComponents<Component>())
                if (c != null && (c.GetType().Name == compName || c.GetType().FullName == compName)) return c;
            err = "component not found on " + go.name + ": " + compName; return null;
        }
        private static object GetMemberValue(object o, string m, out string err)
        {
            err = null; var t = o.GetType();
            var p = t.GetProperty(m, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanRead) { try { return p.GetValue(o); } catch (Exception e) { err = "get threw: " + (e.InnerException?.Message ?? e.Message); return null; } }
            var f = t.GetField(m, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) return f.GetValue(o);
            err = "public instance property/field not found: " + m; return null;
        }
        private static string SetMemberValue(object o, string m, string raw)
        {
            var t = o.GetType();
            var p = t.GetProperty(m, BindingFlags.Public | BindingFlags.Instance);
            if (p != null && p.CanWrite) { try { p.SetValue(o, ParseArg(raw, p.PropertyType)); return null; } catch (Exception e) { return "set threw: " + (e.InnerException?.Message ?? e.Message); } }
            var f = t.GetField(m, BindingFlags.Public | BindingFlags.Instance);
            if (f != null) { try { f.SetValue(o, ParseArg(raw, f.FieldType)); return null; } catch (Exception e) { return "set threw: " + (e.InnerException?.Message ?? e.Message); } }
            return "writable public member not found: " + m;
        }
        private static string Exec(string fullName, IReadOnlyDictionary<string, string> cmd)
        {
            // SECURITY: arbitrary static-method invocation is OPT-IN. Off unless DCL_AUTOMATION_ALLOW_EXEC=1,
            // so even when the (define-gated) bridge runs, exec is not a default-on RCE surface.
            if (Environment.GetEnvironmentVariable("DCL_AUTOMATION_ALLOW_EXEC") != "1") return Err("exec disabled (set DCL_AUTOMATION_ALLOW_EXEC=1)");
            if (string.IsNullOrEmpty(fullName)) return Err("missing method");
            int dot = fullName.LastIndexOf('.'); if (dot < 0) return Err("expected Namespace.Class.Method");
            var t = ResolveType(fullName.Substring(0, dot)); if (t == null) return Err("type not found: " + fullName.Substring(0, dot));
            string name = fullName.Substring(dot + 1);
            var argMap = new Dictionary<string, string>();
            foreach (var kv in cmd) if (kv.Key.StartsWith("arg.")) argMap[kv.Key.Substring(4)] = kv.Value;
            var m = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                     .FirstOrDefault(mi => mi.Name == name && mi.GetParameters().Length == argMap.Count);
            if (m == null) return Err("static method not found / arity mismatch: " + fullName);
            var ps = m.GetParameters(); var args = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
            {
                if (!argMap.TryGetValue(ps[i].Name, out var raw)) return Err("missing arg." + ps[i].Name);
                try { args[i] = ParseArg(raw, ps[i].ParameterType); } catch (Exception e) { return Err("arg." + ps[i].Name + ": " + e.Message); }
            }
            object ret; try { ret = m.Invoke(null, args); } catch (Exception e) { return Err("invoke threw: " + (e.InnerException ?? e)); }
            return m.ReturnType == typeof(void) ? Ok("invoked " + fullName) : "{\"ok\":true,\"result\":" + JsonValue(ret) + "}";
        }

        // ---- shared helpers ------------------------------------------------------------------
        private static Type ResolveType(string fullName)
        {
            var d = Type.GetType(fullName); if (d != null) return d;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) { var t = a.GetType(fullName, false); if (t != null) return t; }
            return null;
        }
        private static object ParseArg(string raw, Type target)
        {
            if (target == typeof(string)) return raw;
            if (target == typeof(int)) return int.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(long)) return long.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(float)) return float.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(double)) return double.Parse(raw, CultureInfo.InvariantCulture);
            if (target == typeof(bool)) return raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (target.IsEnum) return Enum.Parse(target, raw, true);
            throw new ArgumentException("unsupported parameter type: " + target.FullName);
        }
        private static string GetGoPath(GameObject go)
        {
            var sb = new StringBuilder(go.name);
            for (var t = go.transform.parent; t != null; t = t.parent) sb.Insert(0, t.name + "/");
            return sb.ToString();
        }
        private static string JsonValue(object v)
        {
            var inv = CultureInfo.InvariantCulture;
            if (v == null) return "null";
            if (v is bool b) return b ? "true" : "false";
            if (v is string s) return JsonString(s);
            if (v is float f) return f.ToString("R", inv);
            if (v is double d) return d.ToString("R", inv);
            if (v is Enum) return JsonString(v.ToString());
            if (v is sbyte || v is byte || v is short || v is ushort || v is int || v is uint || v is long || v is ulong) return Convert.ToString(v, inv);
            return JsonString(v.ToString());
        }
        private static string Ok(string result) => "{\"ok\":true,\"result\":" + JsonString(result) + "}";
        private static string Err(string msg) => "{\"ok\":false,\"error\":" + JsonString(msg) + "}";
        private static string JsonString(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (var ch in s)
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break; case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break; case '\r': sb.Append("\\r"); break; case '\t': sb.Append("\\t"); break;
                    default: if (ch < 0x20) sb.AppendFormat("\\u{0:x4}", (int)ch); else sb.Append(ch); break;
                }
            return sb.Append('"').ToString();
        }
    }
}
#endif
