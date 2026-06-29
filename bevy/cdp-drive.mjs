// cdp-drive.mjs — drive the CDP-attached explorer chromium (rig/bevy/cdp-explorer.sh).
// Usage (CDP_PORT defaults 9344):
//   node cdp-drive.mjs info                 -> engine/canvas/HUD/scene state
//   node cdp-drive.mjs eval '<jsExpr>'      -> evaluate an expression in the page
//   node cdp-drive.mjs click <x> <y>        -> mouse click at viewport coords
//   node cdp-drive.mjs move  <x> <y>        -> mouse move
//   node cdp-drive.mjs key   <Key>          -> keyboard press (e.g. Enter, w)
//   node cdp-drive.mjs type  <text...>      -> type text
//   node cdp-drive.mjs shot  <path>         -> CDP screenshot (DOM; canvas may be black)
//   node cdp-drive.mjs console <secs>       -> stream page console for N seconds
import { createRequire } from "node:module";
// puppeteer-core lives in design/slides/node_modules (two levels up: rig/bevy -> <repo>).
const require = createRequire(new URL("../../design/slides/node_modules/", import.meta.url));
const puppeteer = require("puppeteer-core");
const CDP = process.env.CDP_PORT || "9344";

const browser = await puppeteer.connect({ browserURL: `http://127.0.0.1:${CDP}`, defaultViewport: null });
const pages = await browser.pages();
const page = pages.find((p) => /localhost:8080|127\.0\.0\.1:8080/.test(p.url())) || pages[pages.length - 1];
const [cmd, ...rest] = process.argv.slice(2);

try {
  if (cmd === "info" || !cmd) {
    const r = await page.evaluate(async () => {
      const c = document.getElementById("mygame-canvas");
      let scenes = null;
      try { if (window.engine && window.engine.liveScenes) scenes = await window.engine.liveScenes(); } catch (e) { scenes = "err:" + e; }
      return {
        url: location.href, title: document.title,
        canvas: c ? { w: c.width, h: c.height, started: !!c.started } : "no-canvas",
        engine: typeof window.engine,
        engineMethods: window.engine ? Object.keys(window.engine).slice(0, 30) : [],
        dclBridge: !!window.dclBridge,
        ui3overlay: !!document.querySelector(".ui3-overlay"),
        liveScenes: scenes,
        bodyChildren: document.body.children.length,
      };
    });
    console.log(JSON.stringify(r, null, 2));
  } else if (cmd === "eval") {
    const r = await page.evaluate(`(async()=>{ return (${rest.join(" ")}); })()`);
    console.log(typeof r === "object" ? JSON.stringify(r, null, 2) : String(r));
  } else if (cmd === "click") {
    await page.mouse.click(Number(rest[0]), Number(rest[1])); console.log("clicked", rest[0], rest[1]);
  } else if (cmd === "move") {
    await page.mouse.move(Number(rest[0]), Number(rest[1])); console.log("moved", rest[0], rest[1]);
  } else if (cmd === "key") {
    await page.keyboard.press(rest[0]); console.log("key", rest[0]);
  } else if (cmd === "type") {
    await page.keyboard.type(rest.join(" ")); console.log("typed");
  } else if (cmd === "nav") {
    await page.goto(rest.join(" "), { waitUntil: "domcontentloaded", timeout: 60000 }); console.log("navigated ->", rest.join(" "));
  } else if (cmd === "reload") {
    await page.reload({ waitUntil: "domcontentloaded", timeout: 60000 }); console.log("reloaded");
  } else if (cmd === "shot") {
    await page.screenshot({ path: rest[0] || "/tmp/cdp-shot.png" }); console.log("shot ->", rest[0] || "/tmp/cdp-shot.png");
  } else if (cmd === "console") {
    const secs = Number(rest[0] || 8);
    page.on("console", (m) => console.log("[console]", m.type(), m.text().slice(0, 240)));
    page.on("pageerror", (e) => console.log("[pageerror]", String(e).slice(0, 240)));
    await new Promise((r) => setTimeout(r, secs * 1000));
  } else { console.log("unknown cmd:", cmd); }
} finally { browser.disconnect(); }
