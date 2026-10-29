#!/usr/bin/env python3
"""Three app-icon concepts (1024x1024), small-size masters, document icons and the preview sheet."""
import os, base64

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, '..', 'app-icon')
os.makedirs(OUT, exist_ok=True)

# Shared body: the macOS icon grid (824 px body, 100 px margin, r=185) — also fine for Windows/Linux.
def body(grad_id, c1, c2):
    return (f'<defs><linearGradient id="{grad_id}" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{c1}"/>'
            f'<stop offset="1" stop-color="{c2}"/></linearGradient>'
            f'<filter id="sh" x="-10%" y="-10%" width="120%" height="125%"><feDropShadow dx="0" dy="14" stdDeviation="18" flood-color="#000" flood-opacity=".28"/></filter></defs>'
            f'<rect x="100" y="100" width="824" height="824" rx="185" fill="url(#{grad_id})" filter="url(#sh)"/>'
            f'<rect x="100" y="100" width="824" height="824" rx="185" fill="none" stroke="#fff" stroke-opacity=".12" stroke-width="4"/>')

def svg(inner, size=1024):
    return f'<svg xmlns="http://www.w3.org/2000/svg" width="{size}" height="{size}" viewBox="0 0 1024 1024">{inner}</svg>\n'

# ---------------- Concept 1 — "Lit screen": four screens of the class, one lit and under the teacher's pointer.
C1 = body('g1', '#3A4FE0', '#1A2170') + (
    '<g>'
    '<rect x="232" y="262" width="262" height="196" rx="34" fill="#E8EEFF"/>'
    '<rect x="530" y="262" width="262" height="196" rx="34" fill="#E8EEFF"/>'
    '<rect x="232" y="494" width="262" height="196" rx="34" fill="#E8EEFF"/>'
    '<rect x="530" y="494" width="262" height="196" rx="34" fill="#FFB224"/>'
    '<rect x="232" y="726" width="560" height="22" rx="11" fill="#E8EEFF" opacity=".45"/>'
    # pointer (the teacher's control) on the lit screen
    '<path d="M640 548 L640 790 L700 732 L742 820 L784 800 L742 714 L826 714 Z" fill="#fff" stroke="#1A2170" stroke-width="22" stroke-linejoin="round"/>'
    '</g>')
# Small master (16/32 px): no pointer, thicker gaps, the lit screen kept.
C1S = body('g1s', '#3A4FE0', '#1A2170') + (
    '<rect x="222" y="232" width="276" height="260" rx="44" fill="#E8EEFF"/>'
    '<rect x="526" y="232" width="276" height="260" rx="44" fill="#E8EEFF"/>'
    '<rect x="222" y="532" width="276" height="260" rx="44" fill="#E8EEFF"/>'
    '<rect x="526" y="532" width="276" height="260" rx="44" fill="#FFB224"/>')

# ---------------- Concept 2 — "Beacon": the teacher's monitor sending its picture to the room.
C2 = body('g2', '#13B4A6', '#0B4F7A') + (
    '<path d="M330 330a250 250 0 0 1 364 0" fill="none" stroke="#fff" stroke-width="46" stroke-linecap="round" opacity=".55"/>'
    '<path d="M395 395a160 160 0 0 1 234 0" fill="none" stroke="#fff" stroke-width="46" stroke-linecap="round" opacity=".8"/>'
    '<circle cx="512" cy="468" r="40" fill="#fff"/>'
    '<rect x="252" y="540" width="520" height="290" rx="42" fill="#fff"/>'
    '<rect x="292" y="580" width="440" height="210" rx="18" fill="#0E6F86"/>'
    '<rect x="320" y="608" width="180" height="26" rx="13" fill="#7FE7DA"/>'
    '<rect x="320" y="652" width="300" height="20" rx="10" fill="#BDEFF0" opacity=".8"/>'
    '<rect x="320" y="690" width="240" height="20" rx="10" fill="#BDEFF0" opacity=".8"/>'
    '<rect x="452" y="830" width="120" height="40" rx="8" fill="#fff" opacity=".85"/>')
C2S = body('g2s', '#13B4A6', '#0B4F7A') + (
    '<path d="M360 360a215 215 0 0 1 304 0" fill="none" stroke="#fff" stroke-width="70" stroke-linecap="round" opacity=".75"/>'
    '<rect x="232" y="500" width="560" height="330" rx="60" fill="#fff"/>'
    '<rect x="292" y="560" width="440" height="210" rx="24" fill="#0E6F86"/>')

# ---------------- Concept 3 — "Room plan": the classroom from above, desks facing the teacher's desk.
desk = lambda x, y: f'<rect x="{x}" y="{y}" width="150" height="104" rx="26" fill="#F4F6FA"/><rect x="{x+44}" y="{y+118}" width="62" height="28" rx="14" fill="#F4F6FA" opacity=".55"/>'
C3 = body('g3', '#2B3445', '#11151D') + ''.join(desk(x, y) for y in (240, 440) for x in (252, 437, 622)) + (
    '<rect x="302" y="700" width="420" height="88" rx="30" fill="#FFB224"/>'
    '<circle cx="512" cy="826" r="34" fill="#FFB224" opacity=".7"/>')
C3S = body('g3s', '#2B3445', '#11151D') + ''.join(f'<rect x="{x}" y="{y}" width="230" height="170" rx="40" fill="#F4F6FA"/>' for y in (230, 450) for x in (252, 542)) + '<rect x="272" y="690" width="480" height="110" rx="40" fill="#FFB224"/>'

CONCEPTS = [
    ('concept-1', 'Lit screen · Світлий екран', C1, C1S),
    ('concept-2', 'Beacon · Промінь', C2, C2S),
    ('concept-3', 'Room plan · План класу', C3, C3S),
]
for name, _, big, small in CONCEPTS:
    open(os.path.join(OUT, name + '.svg'), 'w').write(svg(big))
    open(os.path.join(OUT, name + '-small.svg'), 'w').write(svg(small))

# ---------------- Document icons for the recommended concept (256x256 grid).
def doc(ext, color, glyph):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">'
            f'<defs><filter id="d" x="-20%" y="-10%" width="140%" height="130%"><feDropShadow dx="0" dy="4" stdDeviation="5" flood-opacity=".25"/></filter></defs>'
            f'<path d="M52 16h104l52 52v164a12 12 0 0 1-12 12H52a12 12 0 0 1-12-12V28a12 12 0 0 1 12-12z" fill="#FFFFFF" stroke="#C9CFD8" stroke-width="2" filter="url(#d)"/>'
            f'<path d="M156 16v40a12 12 0 0 0 12 12h40" fill="#E6EAF0" stroke="#C9CFD8" stroke-width="2"/>'
            # mini app mark (concept 1 small)
            f'<g transform="translate(74 70)"><rect width="108" height="108" rx="26" fill="#2A37A8"/>'
            f'<rect x="18" y="20" width="32" height="30" rx="7" fill="#E8EEFF"/><rect x="58" y="20" width="32" height="30" rx="7" fill="#E8EEFF"/>'
            f'<rect x="18" y="58" width="32" height="30" rx="7" fill="#E8EEFF"/><rect x="58" y="58" width="32" height="30" rx="7" fill="#FFB224"/></g>'
            f'<g transform="translate(150 140)"><circle cx="24" cy="24" r="26" fill="{color}" stroke="#fff" stroke-width="5"/>'
            f'<g fill="none" stroke="#fff" stroke-width="5" stroke-linecap="round" stroke-linejoin="round">{glyph}</g></g>'
            f'<rect x="40" y="200" width="168" height="32" rx="0" fill="{color}"/>'
            f'<text x="124" y="223" text-anchor="middle" font-family="Inter,Segoe UI,Arial,sans-serif" font-size="20" font-weight="800" fill="#fff" letter-spacing="1">{ext}</text></svg>\n')

DOCS = [
    ('lcbak', '#B45309', '<rect x="12" y="13" width="24" height="7" rx="1"/><path d="M14 20v14h20V20M20 26h8"/>', 'Повна резервна копія (адміністратор): ключ, коди, лабораторія'),
    ('lclab', '#0F766E', '<rect x="12" y="12" width="10" height="10" rx="2"/><rect x="26" y="12" width="10" height="10" rx="2"/><rect x="12" y="26" width="10" height="10" rx="2"/><rect x="26" y="26" width="10" height="10" rx="2"/>', 'Файл лабораторії для викладачів: без ключа й кодів'),
    ('lcreq', '#7C3AED', '<path d="M24 34V14M16 22l8-8 8 8"/>', 'Запит доступу від пристрою викладача'),
    ('lcgrant', '#15803D', '<path d="M14 25l7 7 13-14"/>', 'Дозвіл адміністратора у відповідь на запит'),
]
for ext, color, glyph, _ in DOCS:
    open(os.path.join(OUT, f'doc-{ext}.svg'), 'w').write(doc('.' + ext.upper(), color, glyph))

# Logo for the prototype (data URI of concept 1 small master)
logo = base64.b64encode(svg(C1S).encode()).decode()
open(os.path.join(HERE, 'logo.txt'), 'w').write('data:image/svg+xml;base64,' + logo)

# ---------------- Preview sheet
def uri(name):
    return 'data:image/svg+xml;base64,' + base64.b64encode(open(os.path.join(OUT, name + '.svg'), 'rb').read()).decode()

def img(name, px):
    return f'<img src="{uri(name)}" width="{px}" height="{px}" alt="{name}">'

rows = ''
for i, (name, title, _, _) in enumerate(CONCEPTS):
    rec = ' <span class="rec">Recommended · рекомендовано</span>' if i == 0 else ''
    sizes = lambda: ''.join(f'<figure>{img(name + ("-small" if px <= 32 else ""), px)}<figcaption>{px}</figcaption></figure>' for px in (16, 32, 64, 128, 512))
    dock = ''.join(f'<span class="gen" style="background:{c}"></span>' for c in ('#8E9AAF', '#E9A23B', '#5B8DEF')) + img(name, 64) + ''.join(f'<span class="gen" style="background:{c}"></span>' for c in ('#3FB57C', '#C35BD9'))
    task = ''.join(f'<span class="tgen" style="background:{c}"></span>' for c in ('#4C8BF5', '#F2B233', '#7A7A7A')) + f'<span class="tact">{img(name + "-small", 24)}</span>' + ''.join(f'<span class="tgen" style="background:{c}"></span>' for c in ('#2E9E5B',))
    rows += f'''<section><h2>{i+1}. {title}{rec}</h2>
<div class="pair"><div class="bg light">{sizes()}</div><div class="bg dark">{sizes()}</div></div>
<div class="pair"><div class="dock"><div class="bar">{dock}</div><small>macOS Dock (64 px)</small></div><div class="taskbar"><div class="tbar"><span class="start"></span>{task}<span class="clock">10:42</span></div><small>Windows 11 taskbar (24 px)</small></div></div></section>'''

docs = ''.join(f'<figure class="docf">{img("doc-"+e,128)}{img("doc-"+e,48)}{img("doc-"+e,32)}<figcaption><b>.{e}</b><br>{d}</figcaption></figure>' for e, _, _, d in DOCS)

page = f'''<!doctype html><html lang="uk"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>LabControl app icon</title><style>
:root{{--bg:#F4F5F7;--card:#fff;--fg:#15181C;--muted:#5B6470;--line:#D5DAE1}}
@media (prefers-color-scheme:dark){{:root:not([data-theme=light]){{--bg:#0F1114;--card:#181B20;--fg:#ECEEF1;--muted:#A7AFBA;--line:#323841}}}}
:root[data-theme=dark]{{--bg:#0F1114;--card:#181B20;--fg:#ECEEF1;--muted:#A7AFBA;--line:#323841}}
body{{margin:0;padding:24px 16px;background:var(--bg);color:var(--fg);font:14px/1.5 Inter,system-ui,sans-serif}}
main{{max-width:1200px;margin:auto}} h1{{margin:0 0 6px;font-size:24px}} h2{{font-size:17px;margin:0 0 12px}}
section{{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:16px;margin:16px 0}}
.rec{{background:#15803D;color:#fff;border-radius:6px;padding:2px 8px;font-size:12px;margin-left:8px}}
.pair{{display:grid;grid-template-columns:1fr 1fr;gap:12px;margin-bottom:12px}}
@media (max-width:820px){{.pair{{grid-template-columns:1fr}}}}
.bg{{border-radius:10px;padding:14px;display:flex;align-items:flex-end;gap:14px;overflow-x:auto}}
.bg.light{{background:#FFFFFF;border:1px solid #E3E6EB;color:#333}} .bg.dark{{background:#1E1F22;color:#ccc}}
.bg img[width="512"]{{width:200px;height:200px}}
figure{{margin:0;display:flex;flex-direction:column;align-items:center;gap:4px;font-size:11px}}
.dock{{background:linear-gradient(#7A93C4,#C9A5C8);border-radius:10px;padding:30px 12px 10px;display:flex;flex-direction:column;align-items:center;gap:6px;color:#fff}}
.dock .bar{{background:rgba(255,255,255,.35);backdrop-filter:blur(10px);border-radius:20px;padding:8px 10px;display:flex;gap:10px;align-items:center}}
.gen{{width:56px;height:56px;border-radius:13px;display:block;opacity:.9}}
.taskbar{{background:linear-gradient(#5C7FAF,#203A5E);border-radius:10px;padding:40px 0 0;display:flex;flex-direction:column;justify-content:flex-end;color:#fff}}
.tbar{{background:#202020;height:48px;display:flex;align-items:center;justify-content:center;gap:6px;position:relative;border-radius:0 0 10px 10px}}
.start{{width:22px;height:22px;background:linear-gradient(135deg,#4CC2FF,#0078D4);border-radius:3px;margin-right:6px}}
.tgen{{width:22px;height:22px;border-radius:5px;display:block;margin:0 9px}}
.tact{{padding:8px 9px;border-radius:5px;background:#2D2D2D;position:relative;display:flex}} .tact::after{{content:"";position:absolute;bottom:2px;left:50%;width:16px;height:3px;margin-left:-8px;border-radius:2px;background:#4CC2FF}}
.clock{{position:absolute;right:14px;font-size:12px;color:#ddd}}
.taskbar small,.dock small{{padding:4px;text-align:center;opacity:.85}}
.docs{{display:grid;grid-template-columns:repeat(auto-fit,minmax(240px,1fr));gap:12px}}
.docf{{flex-direction:row;flex-wrap:wrap;align-items:flex-end;gap:10px;background:var(--bg);padding:12px;border-radius:10px;font-size:12.5px}}
.docf figcaption{{flex-basis:100%;color:var(--muted)}}
ul{{color:var(--muted)}} button{{font:inherit;padding:4px 10px;border-radius:6px;border:1px solid var(--line);background:var(--card);color:var(--fg);cursor:pointer}}
</style></head><body><main>
<h1>LabControl — app icon: three concepts</h1>
<p style="color:var(--muted);max-width:900px">Each concept has a 1024 px master (<code>concept-N.svg</code>) and a simplified small master (<code>concept-N-small.svg</code>) used at 16–32 px, the way the Windows <code>.ico</code> and macOS <code>.icns</code> carry per-size art. Rendered from the same SVG files on light and dark, in a Dock and a Windows 11 taskbar.
<button onclick="document.documentElement.dataset.theme=document.documentElement.dataset.theme==='dark'?'light':'dark'">Light / dark page</button></p>
{rows}
<section><h2>Why concept 1</h2><ul>
<li><b>It is the product in one glance</b>: the mosaic of student screens (the class screen is the home of the redesign) and the teacher's pointer on one of them — watch and take control.</li>
<li><b>It survives 16 px</b>: the small master is four blocks with one amber — still readable in a Windows tray/taskbar and a Finder list, where the old icon's three cyan tiles merged into a smear.</li>
<li><b>It carries the design system</b>: indigo = the console accent, amber = the one "lit" tile; the same mark sits in the top bar, on the student lock screen's colour family and on the document icons.</li>
<li><b>Distinct</b> in a Dock full of blue squares thanks to the single warm tile; no letter, so it reads the same in Ukrainian, Russian and English.</li>
<li>Runner-up: concept 3 (room plan) is the most original but needs its simplified art down to 4 desks below 48 px.</li></ul></section>
<section><h2>Document icons (concept 1 family)</h2><div class="docs">{docs}</div>
<p style="color:var(--muted)">One page shape, the app mark, and a type badge + colour band: <b>.lcbak</b> amber (sensitive: carries the key — the same amber as "attention"), <b>.lclab</b> teal, <b>.lcreq</b> violet (outgoing request), <b>.lcgrant</b> green (approval). The extension label is text here; convert to outlines when exporting <code>.ico</code>/<code>.icns</code> (tools/make-icon.py).</p></section>
</main></body></html>'''
open(os.path.join(OUT, 'index.html'), 'w').write(page)
print('ok')
