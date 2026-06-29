#!/usr/bin/env python3
"""pr-review-server.py — server-side-rendered PR review/audit tool (self-contained).

Run from your own shell:
    python3 pr-review-server.py                 # serves http://127.0.0.1:8099
    PR_REVIEW_DIR=~/pr-review PR_REVIEW_PORT=8099 python3 pr-review-server.py

Reads the loop's PR-<N>-review-<NN>.md + PR-<N>/*.png screenshots, overlays a persisted human
audit layer (audit.json: status + notes + comments), and renders a funnel board + per-PR pages.

No GitHub/API calls. Per-PR git facts (changed code, commit count, last commit id) come from a
local checkout when PR_REVIEW_REPO_DIR points at one (computed once, cached to PR-<N>/_git.json);
otherwise the page still shows whatever the review file recorded plus a link to the PR.
Works fully WITHOUT JavaScript (plain links + POST forms); JS adds keyboard nav, autosave, filter.
No third-party deps.

Env:
  PR_REVIEW_DIR       review output dir (default ~/pr-review)
  PR_REVIEW_PORT      listen port (default 8099)
  PR_REVIEW_REPO      owner/repo for the PR link (default decentraland/unity-explorer)
  PR_REVIEW_REPO_DIR  optional local git checkout to derive the diff/commits from
  PR_REVIEW_BASE      base ref to diff a PR head against (default origin/dev)
"""
import os, re, glob, json, html, threading, subprocess, urllib.parse
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer

DIR      = os.environ.get("PR_REVIEW_DIR", os.path.expanduser("~/pr-review"))
PORT     = int(os.environ.get("PR_REVIEW_PORT", "8099"))
REPO     = os.environ.get("PR_REVIEW_REPO", "decentraland/unity-explorer")
REPO_DIR = os.environ.get("PR_REVIEW_REPO_DIR", "")
BASE     = os.environ.get("PR_REVIEW_BASE", "origin/dev")
AUDIT    = os.path.join(DIR, "audit.json")
STATUSES = ["unaudited", "reviewing", "approved", "changes-requested", "blocked", "merged"]
DIFF_CAP = 200_000          # bytes of diff to render before truncating
_lock = threading.Lock()

# ---------------- audit persistence ----------------
def load_audit():
    try: return json.load(open(AUDIT))
    except Exception: return {}

def save_audit(a):
    with _lock:
        json.dump(a, open(AUDIT, "w"), indent=2)

# ---------------- review files ----------------
def _f(body, label):
    m = re.search(r"-\s*\*\*" + re.escape(label) + r":\*\*\s*(.+)", body)
    return m.group(1).strip() if m else ""

def load_prs():
    by = {}
    for p in glob.glob(os.path.join(DIR, "PR-*-review-*.md")):
        m = re.match(r"PR-(\d+)-review-(\d+)\.md$", os.path.basename(p))
        if not m: continue
        n, idx = int(m.group(1)), int(m.group(2))
        if n not in by or idx > by[n][0]: by[n] = (idx, p)
    out = []
    for n, (idx, path) in by.items():
        b = open(path, encoding="utf-8", errors="replace").read()
        bt = re.search(r"Build/test verdict:\s*([A-Z\-]+)", b) or re.search(r"Verdict:\s*([A-Z\-]+)", b)
        cr = re.search(r"code review:\s*(PASS|FAIL)", b, re.I) or re.search(r"^REVIEW_RESULT:\s*(PASS|FAIL)", b, re.M)
        comp = re.search(r"^COMPLEXITY:\s*(SIMPLE|COMPLEX)", b, re.M)
        qa = re.search(r"^QA_REQUIRED:\s*(YES|NO)", b, re.M)
        shots = sorted(os.path.basename(s) for s in glob.glob(os.path.join(DIR, f"PR-{n}", "*.png")))
        out.append(dict(num=n, rev=idx, title=_f(b, "title") or "(untitled)",
                        head=(_f(b, "pr head sha") or "")[:12], build=(bt.group(1) if bt else "?"),
                        code=(cr.group(1).upper() if cr else ""), complexity=(comp.group(1) if comp else ""),
                        qa=(qa.group(1) if qa else ""), shots=shots, md=b,
                        url=f"https://github.com/{REPO}/pull/{n}"))
    out.sort(key=lambda p: p["num"], reverse=True)
    return out

def stage(p):
    """Where the PR is stuck in the automated funnel."""
    bt = p["build"]
    if bt == "REBASE-CONFLICT": return ("Rebase", "blocked")
    if bt == "CONFLICT":        return ("Stack", "blocked")
    if bt == "COMPILE-FAIL":    return ("Build", "blocked")
    if bt == "REGRESSION":      return ("Screens", "blocked")
    if bt in ("RUN-INCOMPLETE", "ATLAS-FAIL", "ERROR", "?"): return ("Test", "warn")
    if p["code"] == "FAIL": return ("Code review", "warn")
    if p["code"] == "PASS" and bt == "PASS": return ("Ready", "ok")
    return ("Tested", "ok")

# ---------------- git facts (local checkout, cached; no network API) ----------------
def _git(*args):
    return subprocess.run(["git", "-C", REPO_DIR, *args],
                          capture_output=True, text=True, timeout=60).stdout.strip()

def git_facts(p):
    """Return {commits, head, files, diff, truncated} or None. Cached to PR-<N>/_git.json."""
    cp = os.path.join(DIR, f"PR-{p['num']}", "_git.json")
    if os.path.exists(cp):
        try: return json.load(open(cp))
        except Exception: pass
    head = p["head"]
    if not REPO_DIR or not head:
        return None
    try:
        rng = f"{BASE}...{head}"
        commits = _git("rev-list", "--count", f"{BASE}..{head}") or "?"
        full = _git("rev-parse", head) or head
        files = _git("diff", "--stat", rng)
        diff = _git("diff", rng)
        truncated = len(diff) > DIFF_CAP
        data = {"commits": commits, "head": full, "files": files,
                "diff": diff[:DIFF_CAP], "truncated": truncated}
        os.makedirs(os.path.dirname(cp), exist_ok=True)
        json.dump(data, open(cp, "w"))
        return data
    except Exception:
        return None

def git_section(p):
    n = p["num"]
    g = git_facts(p)
    big = (f"<p><a class=ghbtn href='{p['url']}' rel=noopener>View PR #{n} on GitHub &nearr;</a></p>")
    if not g:
        head = html.escape(p["head"]) or "?"
        return (f"<h2 id=changes>Changed code</h2>{big}"
                f"<p class=mut>Last commit <code>{head}</code>. "
                f"Set <code>PR_REVIEW_REPO_DIR</code> to a local checkout to render the diff here.</p>")
    o = [f"<h2 id=changes>Changed code</h2>", big,
         f"<p><span class='b mut'>{html.escape(str(g['commits']))} commits</span>"
         f"<span class='b mut'>last commit <code>{html.escape(g['head'][:12])}</code></span></p>"]
    if g.get("files"):
        o.append(f"<details open><summary>Files changed</summary><pre>{html.escape(g['files'])}</pre></details>")
    if g.get("diff"):
        note = "<p class=mut>diff truncated — open on GitHub for the rest</p>" if g.get("truncated") else ""
        o.append(f"<details><summary>Full diff</summary><pre class=diff>{html.escape(g['diff'])}</pre>{note}</details>")
    return "".join(o)

def changelog_section(p):
    import time as _t
    revs = sorted(glob.glob(os.path.join(DIR, f"PR-{p['num']}-review-*.md")))
    rows = []
    for f in revs:
        nn = re.search(r"review-(\d+)", f).group(1)
        b = open(f, encoding="utf-8", errors="replace").read()
        v = re.search(r"verdict:\s*([A-Z\-]+)", b, re.I)
        cr = re.search(r"code review:\s*(PASS|FAIL)", b, re.I)
        ts = _t.strftime("%Y-%m-%d %H:%M", _t.localtime(os.path.getmtime(f)))
        rows.append(f"<li>rev <b>{nn}</b> · {ts} · build <span class='b {v.group(1) if v else 'mut'}'>{v.group(1) if v else '?'}</span>"
                    + (f" · review <span class='b {cr.group(1).upper()}'>{cr.group(1).upper()}</span>" if cr else "") + "</li>")
    return f"<h2 id=changelog>Review changelog ({len(revs)})</h2><ul>{''.join(rows)}</ul>"

# ---------------- markdown (tiny) ----------------
def md2html(t):
    t = html.escape(t)
    t = re.sub(r"```(.*?)```", lambda m: "<pre>" + m.group(1) + "</pre>", t, flags=re.S)
    out, inl = [], False
    for ln in t.split("\n"):
        if re.match(r"#### ", ln): out.append(f"<h4>{ln[5:]}</h4>"); continue
        if re.match(r"### ", ln):  out.append(f"<h3>{ln[4:]}</h3>"); continue
        if re.match(r"## ", ln):   out.append(f"<h2>{ln[3:]}</h2>"); continue
        if re.match(r"# ", ln):    out.append(f"<h2>{ln[2:]}</h2>"); continue
        if ln.startswith("&gt; "): out.append(f"<blockquote>{ln[5:]}</blockquote>"); continue
        if re.match(r"\s*[-*] ", ln):
            if not inl: out.append("<ul>"); inl = True
            out.append("<li>" + re.sub(r"^\s*[-*] ", "", ln) + "</li>"); continue
        if inl: out.append("</ul>"); inl = False
        if ln.strip(): out.append(f"<p>{ln}</p>")
    if inl: out.append("</ul>")
    s = "\n".join(out)
    s = re.sub(r"\*\*(.+?)\*\*", r"<b>\1</b>", s)
    s = re.sub(r"`([^`]+)`", r"<code>\1</code>", s)
    return s

# ---------------- views ----------------
CSS = """
/* Palette + tokens cherry-picked from the Decentraland client UI port:
   Inter type, ruby --brand #ff2d55, orange --accent #ff743a, dark-purple panels,
   translucent-white surfaces, pill/card radii (control 10 / card 14 / panel 18 / pill 999). */
:root{
  --bg:#15121b;--bg2:#0f0d14;--card:#1c1922;--panel:#1c1922;
  --bd:rgba(255,255,255,.1);--bd2:rgba(255,255,255,.06);
  --fg:#fcfcfc;--mut:rgba(255,255,255,.55);
  --brand:#ff2d55;--brand-hover:#ff4d70;--accent:#ff743a;
  --online:#57df41;--gold:#ffc95b;--purple:#982de2;--sky:#79c0ff;
  --r-control:10px;--r-card:14px;--r-panel:18px;--r-pill:999px;
  --ac:var(--brand);
}
*{box-sizing:border-box}
body{margin:0;font:14px/1.55 "Inter",system-ui,-apple-system,Segoe UI,Roboto,sans-serif;background:radial-gradient(120% 100% at 50% 0,#1c1726 0,var(--bg) 60%);color:var(--fg);min-height:100vh}
a{color:var(--accent);text-decoration:none}a:hover{text-decoration:underline}
a:focus,button:focus,select:focus,textarea:focus,[tabindex]:focus{outline:2px solid var(--brand);outline-offset:2px}
.mut{color:var(--mut)}
.skip{position:absolute;left:-999px}.skip:focus{left:8px;top:8px;background:var(--brand);color:#fff;padding:6px;z-index:99;border-radius:8px}
header{padding:16px 24px;border-bottom:1px solid var(--bd);position:sticky;top:0;background:rgba(21,18,27,.85);backdrop-filter:blur(12px);z-index:5;display:flex;gap:16px;align-items:baseline;flex-wrap:wrap}
header h1{font-size:18px;font-weight:700;margin:0;letter-spacing:-.01em}header .mut{font-size:12px}
.kbd{font:11px ui-monospace,monospace;background:rgba(255,255,255,.08);border:1px solid var(--bd);border-radius:6px;padding:1px 6px}
.board{display:grid;grid-template-columns:repeat(auto-fill,minmax(280px,1fr));gap:14px;padding:20px 24px;align-items:start}
.col{background:rgba(255,255,255,.03);border:1px solid var(--bd2);border-radius:var(--r-panel);padding:12px}
.col h2{font-size:12px;margin:2px 6px 12px;color:var(--mut);text-transform:uppercase;letter-spacing:.06em;font-weight:700}
.card{display:block;background:var(--card);border:1px solid var(--bd);border-radius:var(--r-card);padding:12px;margin-bottom:10px;text-decoration:none;color:var(--fg);transition:border-color .12s,transform .12s}
.card:hover,.card:focus{border-color:var(--brand);transform:translateY(-1px)}
.card.sel{border-color:var(--brand);box-shadow:0 0 0 1px var(--brand)}
.card .pr{color:var(--mut);font-size:11px}.card .t{font-weight:600;margin:3px 0 9px}
.b{font-size:10px;padding:2px 9px;border-radius:var(--r-pill);font-weight:700;display:inline-block;margin:0 4px 4px 0;letter-spacing:.02em}
.b.PASS,.b.ok{background:rgba(87,223,65,.16);color:var(--online)}
.b.FAIL,.b.blocked{background:rgba(255,45,85,.18);color:var(--brand-hover)}
.b.warn,.b.REBASE-CONFLICT,.b.CONFLICT,.b.COMPILE-FAIL,.b.REGRESSION{background:rgba(255,116,58,.18);color:var(--accent)}
.b.SIMPLE{background:rgba(87,223,65,.14);color:var(--online)}
.b.COMPLEX{background:rgba(152,45,226,.22);color:#c79bff}
.b.QA{background:rgba(121,192,255,.16);color:var(--sky)}
.b.mut{background:rgba(255,255,255,.08);color:rgba(255,255,255,.7)}
main{padding:20px 24px;max-width:1060px;margin:0 auto}
.detail-grid{display:grid;grid-template-columns:1fr 320px;gap:22px}@media(max-width:860px){.detail-grid{grid-template-columns:1fr}}
.mdwrap h2{font-size:15px;font-weight:700;border-bottom:1px solid var(--bd);padding-bottom:5px;margin-top:22px}
.mdwrap code{background:rgba(255,255,255,.08);padding:1px 6px;border-radius:6px;font-family:ui-monospace,monospace}
.mdwrap pre{background:var(--bg2);border:1px solid var(--bd);padding:12px;border-radius:var(--r-control);overflow:auto}
.mdwrap pre.diff{max-height:520px}
.mdwrap blockquote{border-left:3px solid var(--brand);margin:8px 0;padding:2px 14px}
.ghbtn{display:inline-block;background:var(--brand);color:#fff;font-weight:700;font-size:15px;padding:11px 20px;border-radius:var(--r-pill);text-decoration:none;box-shadow:0 4px 16px rgba(255,45,85,.3)}
.ghbtn:hover{background:var(--brand-hover);text-decoration:none}
.toc{display:flex;gap:14px;flex-wrap:wrap;font-size:12px;background:rgba(255,255,255,.04);border:1px solid var(--bd2);border-radius:var(--r-control);padding:9px 14px;margin:6px 0;position:sticky;top:60px;z-index:2}
details{margin:8px 0;border:1px solid var(--bd);border-radius:var(--r-control);padding:7px 14px;background:rgba(255,255,255,.03)}
details summary{cursor:pointer;color:var(--accent);font-weight:600}
details ul{margin:6px 0}details li{margin:2px 0}
.panel{background:var(--panel);border:1px solid var(--bd);border-radius:var(--r-panel);padding:16px;position:sticky;top:68px}
.panel h3{margin:0 0 10px;font-size:13px;font-weight:700}
.panel label{display:block;font-size:12px;color:var(--mut);margin:10px 0 4px}
select,textarea,input[type=text]{width:100%;background:var(--bg2);color:var(--fg);border:1px solid var(--bd);border-radius:var(--r-control);padding:8px 10px;font:13px inherit}
textarea{min-height:120px;resize:vertical;font-family:ui-monospace,monospace}
button{background:var(--brand);color:#fff;border:0;border-radius:var(--r-pill);padding:9px 18px;font-weight:700;cursor:pointer;margin-top:10px;font-family:inherit}
button:hover{background:var(--brand-hover)}
.gal{display:grid;grid-template-columns:repeat(auto-fill,minmax(140px,1fr));gap:8px}
.gal img{width:100%;border:1px solid var(--bd);border-radius:var(--r-control);background:#000;aspect-ratio:16/10;object-fit:cover}
.gal figcaption{font-size:10px;color:var(--mut);text-align:center;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.cmt{border-left:2px solid var(--bd);padding:4px 10px;margin:8px 0;font-size:13px}.cmt .ts{color:var(--mut);font-size:11px}
.help{position:fixed;inset:0;background:rgba(0,0,0,.7);backdrop-filter:blur(4px);display:none;z-index:50}.help div{max-width:440px;margin:80px auto;background:var(--card);border:1px solid var(--bd);border-radius:var(--r-panel);padding:22px}
table.kb{width:100%;font-size:13px}table.kb td{padding:3px 0}
"""

KBHELP = [("j / k", "next / previous card"), ("o, Enter", "open selected PR"),
          ("u", "back to board"), ("e", "focus notes"), ("1–6", "set audit status"),
          ("/", "filter"), ("g h", "open PR on GitHub"), ("?", "toggle this help")]

def page(title, body):
    return ("<!doctype html><html lang=en><head><meta charset=utf-8>"
            "<meta name=viewport content='width=device-width,initial-scale=1'>"
            f"<title>{html.escape(title)}</title><style>{CSS}</style></head><body>"
            "<a class=skip href='#main'>Skip to content</a>" + body +
            "<div class=help id=help><div><h3>Keyboard shortcuts</h3><table class=kb>" +
            "".join(f"<tr><td><span class=kbd>{html.escape(k)}</span></td><td>{html.escape(v)}</td></tr>" for k, v in KBHELP) +
            "</table><p class=mut style='color:var(--mut)'>Works without JavaScript too — every action is a link or form.</p></div></div>"
            f"<script>{JS}</script></body></html>")

def board_view(prs, audit, flt):
    cols = {s: [] for s in STATUSES}
    for p in prs:
        st = audit.get(str(p["num"]), {}).get("status", "unaudited")
        if flt and flt.lower() not in (p["title"] + " " + str(p["num"]) + " " + p["build"] + " " + p["code"]).lower():
            continue
        cols[st].append(p)
    n_fail = sum(1 for p in prs if p["code"] == "FAIL")
    hdr = (f"<header><h1>PR review board</h1>"
           f"<span class=mut>{len(prs)} PRs · {n_fail} code-review FAIL · "
           f"{sum(1 for p in prs if stage(p)[1]=='blocked')} blocked · press <span class=kbd>?</span> for keys</span>"
           f"<form method=get style='margin-left:auto'><input type=text name=q placeholder='filter…' value='{html.escape(flt)}' id=filter aria-label=filter></form></header>")
    body = ["<main id=main><div class=board>"]
    ci = 0
    for s in STATUSES:
        items = cols[s]
        body.append(f"<section class=col aria-label='{s}'><h2>{s.replace('-',' ')} ({len(items)})</h2>")
        for p in items:
            sg, scl = stage(p)
            badges = (f"<span class='b {scl}'>{sg}</span>"
                      f"<span class='b {p['build']}'>{p['build']}</span>"
                      + (f"<span class='b {p['code']}'>review {p['code']}</span>" if p['code'] else "")
                      + (f"<span class='b {p['complexity']}'>{p['complexity']}</span>" if p['complexity'] else "")
                      + (f"<span class=b QA>QA</span>" if p['qa'] == 'YES' else ""))
            body.append(f"<a class=card href='/pr/{p['num']}' data-i='{ci}' data-num='{p['num']}'>"
                        f"<div class=pr>PR #{p['num']} · {html.escape(p['head'])}</div>"
                        f"<div class=t>{html.escape(p['title'])}</div><div>{badges}</div>"
                        f"<div class=pr>{len(p['shots'])} screenshots</div></a>")
            ci += 1
        body.append("</section>")
    body.append("</div></main>")
    return page("PR review board", hdr + "".join(body))

def detail_view(p, audit):
    a = audit.get(str(p["num"]), {})
    st = a.get("status", "unaudited")
    notes = a.get("notes", "")
    comments = a.get("comments", [])
    opts = "".join(f"<option value='{s}'{' selected' if s==st else ''}>{s}</option>" for s in STATUSES)
    gal = "".join(f"<figure style=margin:0><a href='/PR-{p['num']}/{html.escape(s)}'><img loading=lazy src='/PR-{p['num']}/{html.escape(s)}' alt='{html.escape(s)}'></a><figcaption>{html.escape(s[:-4])}</figcaption></figure>" for s in p["shots"]) or "<p class=mut>none</p>"
    cmts = "".join(f"<div class=cmt><div class=ts>{html.escape(c.get('ts',''))}</div>{html.escape(c.get('text',''))}</div>" for c in comments) or "<p class=mut style='color:var(--mut)'>No comments yet.</p>"
    sg, scl = stage(p)
    hdr = (f"<header><h1><a href='/'>&larr;</a> PR #{p['num']}</h1>"
           f"<span class=mut>{html.escape(p['title'])}</span>"
           f"<span style='margin-left:auto'><a href='{p['url']}' id=ghlink rel=noopener>open on GitHub &nearr;</a></span></header>")
    toc = ("<nav class=toc aria-label='sections'>"
           "<a href='#changes'>Changed code</a><a href='#review'>Our review</a>"
           "<a href='#shots'>Screenshots</a><a href='#changelog'>Changelog</a></nav>")
    left = ("<div class=mdwrap>"
            f"<p><span class='b {scl}'>{sg}</span><span class='b {p['build']}'>{p['build']}</span>"
            + (f"<span class='b {p['code']}'>review {p['code']}</span>" if p['code'] else "")
            + (f"<span class='b {p['complexity']}'>{p['complexity']}</span>" if p['complexity'] else "")
            + (f"<span class=b QA>QA required</span>" if p['qa']=='YES' else "")
            + f" <span class='b mut'>head {html.escape(p['head'])}</span> <span class='b mut'>rev {p['rev']}</span></p>"
            + toc
            + git_section(p)
            + f"<h2 id=review>Our in-depth review</h2><div>{md2html(p['md'])}</div>"
            + "<h2 id=shots>Screenshots</h2><div class=gal>" + gal + "</div>"
            + changelog_section(p)
            + "</div>")
    right = (f"<aside class=panel aria-label='audit'><h3>Audit</h3>"
             f"<form method=post action='/pr/{p['num']}/audit' id=auditform>"
             f"<label for=status>Status</label><select name=status id=status>{opts}</select>"
             f"<label for=notes>Notes <span class=kbd>e</span></label>"
             f"<textarea name=notes id=notes placeholder='Audit notes (markdown)…'>{html.escape(notes)}</textarea>"
             f"<button type=submit>Save</button> <span id=saved class=mut></span></form>"
             f"<h3 style='margin-top:18px'>Comments</h3>{cmts}"
             f"<form method=post action='/pr/{p['num']}/comment'>"
             f"<label for=ctext>Add comment</label><textarea name=text id=ctext style='min-height:60px'></textarea>"
             f"<button type=submit>Comment</button></form></aside>")
    return page(f"PR #{p['num']} — {p['title']}",
                hdr + f"<main id=main><div class=detail-grid>{left}{right}</div></main>")

JS = r"""
(function(){
 var cards=[].slice.call(document.querySelectorAll('.card'));var sel=-1;
 function mark(){cards.forEach(function(c,i){c.classList.toggle('sel',i===sel);});if(sel>=0){cards[sel].focus();cards[sel].scrollIntoView({block:'nearest'});}}
 function onBoard(){return cards.length>0;}
 document.addEventListener('keydown',function(e){
   var t=e.target.tagName;if(t==='INPUT'||t==='TEXTAREA'||t==='SELECT'){if(e.key==='Escape')e.target.blur();return;}
   if(e.key==='?'){var h=document.getElementById('help');h.style.display=h.style.display==='block'?'none':'block';return;}
   if(e.key==='Escape'){var h=document.getElementById('help');if(h)h.style.display='none';return;}
   if(onBoard()){
     if(e.key==='j'){sel=Math.min(cards.length-1,sel+1);mark();e.preventDefault();}
     else if(e.key==='k'){sel=Math.max(0,sel-1);mark();e.preventDefault();}
     else if((e.key==='o'||e.key==='Enter')&&sel>=0){location.href=cards[sel].getAttribute('href');}
     else if(e.key==='/'){var f=document.getElementById('filter');if(f){f.focus();e.preventDefault();}}
   } else {
     if(e.key==='u'){location.href='/';}
     else if(e.key==='e'){var n=document.getElementById('notes');if(n){n.focus();e.preventDefault();}}
     else if(e.key==='g'){window._g=1;setTimeout(function(){window._g=0;},600);}
     else if(e.key==='h'&&window._g){var l=document.getElementById('ghlink');if(l)window.open(l.href,'_blank');}
     else if('123456'.indexOf(e.key)>=0){var s=document.getElementById('status');if(s){s.selectedIndex=parseInt(e.key)-1;autosave();}}
   }
 });
 var af=document.getElementById('auditform');
 function autosave(){if(!af)return;var fd=new FormData(af);fetch(af.action,{method:'POST',body:new URLSearchParams(fd),headers:{'X-Ajax':'1'}}).then(function(){var s=document.getElementById('saved');if(s){s.textContent='saved ✓';setTimeout(function(){s.textContent='';},1500);}});}
 window.autosave=autosave;
 if(af){var st=document.getElementById('status');if(st)st.addEventListener('change',autosave);var nt=document.getElementById('notes');if(nt){var tmr;nt.addEventListener('input',function(){clearTimeout(tmr);tmr=setTimeout(autosave,800);});}
   af.addEventListener('submit',function(e){e.preventDefault();autosave();});}
 var fi=document.getElementById('filter');
 if(fi&&onBoard()){fi.closest('form').addEventListener('submit',function(e){e.preventDefault();});
   fi.addEventListener('input',function(){var q=fi.value.toLowerCase();cards.forEach(function(c){c.style.display=(c.textContent.toLowerCase().indexOf(q)>=0)?'':'none';});});}
})();
"""

# ---------------- server ----------------
class H(SimpleHTTPRequestHandler):
    def __init__(self, *a, **k): super().__init__(*a, directory=DIR, **k)
    def log_message(self, *a): pass
    def _send(self, body, code=200, ctype="text/html; charset=utf-8"):
        b = body.encode("utf-8"); self.send_response(code)
        self.send_header("Content-Type", ctype); self.send_header("Content-Length", str(len(b)))
        self.end_headers(); self.wfile.write(b)
    def do_GET(self):
        u = urllib.parse.urlparse(self.path); path = u.path
        if path == "/" or path == "":
            q = urllib.parse.parse_qs(u.query).get("q", [""])[0]
            return self._send(board_view(load_prs(), load_audit(), q))
        m = re.match(r"/pr/(\d+)/?$", path)
        if m:
            n = int(m.group(1)); pr = next((p for p in load_prs() if p["num"] == n), None)
            if not pr: return self._send("<p>not found</p>", 404)
            return self._send(detail_view(pr, load_audit()))
        return super().do_GET()   # serves PR-<n>/*.png and other static files
    def do_POST(self):
        m = re.match(r"/pr/(\d+)/(audit|comment)$", self.path)
        if not m: return self._send("bad", 400)
        n = m.group(1); kind = m.group(2)
        ln = int(self.headers.get("Content-Length", 0))
        form = urllib.parse.parse_qs(self.rfile.read(ln).decode("utf-8"))
        a = load_audit(); rec = a.setdefault(n, {})
        if kind == "audit":
            rec["status"] = form.get("status", ["unaudited"])[0]
            rec["notes"] = form.get("notes", [""])[0]
        else:
            txt = form.get("text", [""])[0].strip()
            if txt:
                import datetime
                rec.setdefault("comments", []).append({"ts": datetime.datetime.now().strftime("%Y-%m-%d %H:%M"), "text": txt})
        save_audit(a)
        if self.headers.get("X-Ajax"):
            return self._send("ok")
        self.send_response(303); self.send_header("Location", f"/pr/{n}"); self.end_headers()

if __name__ == "__main__":
    os.chdir(DIR)
    print(f"PR review tool: http://127.0.0.1:{PORT}  (dir={DIR})  Ctrl-C to stop")
    ThreadingHTTPServer(("127.0.0.1", PORT), H).serve_forever()
