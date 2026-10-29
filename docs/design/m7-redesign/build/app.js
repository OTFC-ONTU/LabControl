/* LabControl console redesign — clickable prototype (concept only; fake data). */
'use strict';
const $ = (s, r = document) => r.querySelector(s);
const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const LI = { uk: 0, ru: 1, en: 2 };
function t(k, ...a) {
  const e = T[k]; let s = e ? (e[LI[S.lang]] ?? e[2]) : '⟦' + k + '⟧';
  a.forEach((v, i) => { s = s.split('{' + i + '}').join(v); });
  return s;
}
function ts(k, ...a) { // setup-dialog language (independent switch)
  const e = T[k]; let s = e ? e[LI[S.setupLang]] : '⟦' + k + '⟧';
  a.forEach((v, i) => { s = s.split('{' + i + '}').join(v); });
  return s;
}
function ic(n, cls = '') { const d = ICONS[n]; return d ? `<svg class="i ${cls}" viewBox="0 0 24 24" aria-hidden="true"><path d="${d}"/></svg>` : `<b>?${n}</b>`; }
const pcName = n => 'PC-' + String(n).padStart(2, '0');
const mmss = s => { s = Math.max(0, Math.round(s)); const m = Math.floor(s / 60); return String(m).padStart(2, '0') + ':' + String(s % 60).padStart(2, '0'); };
const now = () => Date.now() / 1000;

/* ---------------------------------------------------------------- state */
const S = {
  variant: 'A', theme: 'light', lang: 'uk', count: 14, win: '1440', role: 'admin', scenario: 'showcase',
  window: 'console', screen: 'class', ribbon: 'lesson', grid: 'fit', zoom: 0, arrange: false,
  sel: new Set(), pcs: [], modal: null, menu: null, toasts: [], full: null, control: false, fullMode: 'text', quality: 'auto',
  exam: null, cast: null, castMode: 'text', keyOpenUntil: 0, keyRemembered: false, banners: true,
  adminTab: 'pcs', actTab: 'actions', pkg: 0, script: 0, batches: [], railForced: null,
  setupLang: 'uk', setupPage: 'ask', setupProg: 0, setupCreate: true, setupNum: 7, stu: 'lock',
  firstStep: 0, chooserMenu: null, legend: false, openBatch: 0, settingsTheme: 'system',
};
let toastId = 1;

/* ---------------------------------------------------------------- fake PCs */
const APPS = ['ide', 'browser', 'doc', 'ide', 'console', 'desktop', 'ide', 'browser'];
const SHOW = ['online', 'lock', 'cast', 'exam', 'net', 'problem', 'off', 'waking', 'outdated', 'renewal', 'nopic', 'elsewhere', 'online', 'examblocked'];
function makePcs() {
  const n = S.scenario === 'empty' ? 0 : S.count; const pcs = [];
  const cols = n <= 8 ? 4 : n <= 14 ? 5 : 6; // the saved room plan: rows of desks with an aisle
  for (let i = 1; i <= n; i++) {
    const p = { n: i, user: 'student', status: 'online', app: APPS[(i * 3) % APPS.length], lock: null, cast: false, exam: null, net: null, problem: null, renewal: false, nopic: false, heldBy: '', ver: '1.0.0+cc39651', host: 'PC-' + String(i).padStart(2, '0'), ip: '192.168.1.' + (100 + i), seen: '10:4' + (i % 10) };
    // plan: an aisle after the 3rd column for 14/30; positions saved in lab.json
    const c = (i - 1) % cols, r = Math.floor((i - 1) / cols); p.col = c + (cols >= 5 && c >= 3 ? 1 : 0); p.row = r;
    const st = S.scenario === 'showcase' ? SHOW[(i - 1) % SHOW.length] : 'online';
    if (n === 1) { pcs.push(p); continue; }
    switch (st) {
      case 'lock': p.lock = { until: null, by: 'this' }; break;
      case 'cast': p.cast = true; break;
      case 'exam': p.exam = { end: now() + 23 * 60 + 41 }; break;
      case 'net': p.net = 'list'; break;
      case 'examblocked': p.exam = { end: now() + 23 * 60 + 41 }; p.net = 'none'; break;
      case 'problem': p.problem = 'helper'; break;
      case 'off': p.status = 'off'; p.user = ''; p.seen = '08:12'; break;
      case 'waking': p.status = 'waking'; p.user = ''; break;
      case 'outdated': p.status = 'outdated'; p.ver = '1.0.0+68d0ae5'; break;
      case 'renewal': p.renewal = true; break;
      case 'nopic': p.nopic = true; break;
      case 'elsewhere': p.status = 'elsewhere'; p.heldBy = 'Ноутбук Олени'; break;
    }
    pcs.push(p);
  }
  S.pcs = pcs; S.sel.clear(); S.exam = null; S.cast = null;
  if (S.scenario === 'showcase' && n > 1) {
    S.exam = null; // partial exam is per-PC; class-wide exam starts from the dialog
    if (pcs.some(p => p.cast)) S.cast = { targets: pcs.filter(p => p.cast).map(p => p.n), mode: 'text', partial: true };
  }
}
const isOn = p => p.status === 'online' || p.status === 'outdated';
const targets = () => S.sel.size ? S.pcs.filter(p => S.sel.has(p.n)) : S.pcs;
const lockedPcs = () => S.pcs.filter(p => p.lock);
const examPcs = () => S.pcs.filter(p => p.exam);
const admin = () => S.role === 'admin';

/* ---------------------------------------------------------------- thumbnails */
const thCache = {};
function rnd(seed) { let x = seed * 9301 + 49297; return () => ((x = (x * 9301 + 49297) % 233280) / 233280); }
function thumb(kind, seed = 1, extra = '') {
  const key = kind + seed + extra; if (thCache[key]) return thCache[key];
  const r = rnd(seed + kind.length * 7); let s = '';
  const lines = (x, y, n, w, col, gap = 5) => { let o = ''; for (let i = 0; i < n; i++) o += `<rect x="${x + (r() < .3 ? 6 : 0)}" y="${y + i * gap}" width="${Math.max(8, w * (0.35 + r() * .65))}" height="2.2" rx="1" fill="${typeof col === 'function' ? col() : col}"/>`; return o; };
  const codeCol = () => ['#CF8E6D', '#6AAB73', '#BCBEC4', '#56A8F5', '#C77DBB', '#BCBEC4'][Math.floor(r() * 6)];
  switch (kind) {
    case 'ide': s = `<rect width="160" height="90" fill="#1E1F22"/><rect width="160" height="7" fill="#2B2D30"/><rect y="7" width="26" height="83" fill="#2B2D30"/>${lines(4, 12, 9, 18, '#6F737A', 6)}<rect x="30" y="9" width="30" height="5" rx="1" fill="#3C3F41"/>${lines(32, 19, 11, 110, codeCol, 6)}`; break;
    case 'browser': s = `<rect width="160" height="90" fill="#FFFFFF"/><rect width="160" height="12" fill="#E8EAED"/><rect x="24" y="3" width="100" height="6" rx="3" fill="#FFFFFF"/><rect x="0" y="12" width="160" height="16" fill="#1F6FEB"/><rect x="10" y="17" width="60" height="5" rx="1" fill="#FFF"/>${lines(10, 36, 8, 90, '#9AA3AE', 6)}<rect x="112" y="36" width="38" height="44" rx="2" fill="#E6EEF8"/>`; break;
    case 'doc': s = `<rect width="160" height="90" fill="#DADDE2"/><rect width="160" height="10" fill="#2B579A"/><rect x="42" y="14" width="76" height="76" fill="#FFF"/><rect x="50" y="20" width="40" height="4" fill="#2B579A"/>${lines(50, 29, 10, 60, '#A0A7B1', 5.5)}`; break;
    case 'console': s = `<rect width="160" height="90" fill="#0C0C0C"/><rect width="160" height="8" fill="#1F1F1F"/>${lines(6, 14, 12, 120, () => r() < .2 ? '#E5C07B' : '#3FB950', 6)}`; break;
    case 'desktop': s = `<defs><linearGradient id="g${seed}" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#2D6BC4"/><stop offset="1" stop-color="#7FB2E5"/></linearGradient></defs><rect width="160" height="90" fill="url(#g${seed})"/><rect y="82" width="160" height="8" fill="#1D2633" opacity=".85"/>${[0, 1, 2, 3].map(i => `<rect x="6" y="${6 + i * 15}" width="9" height="9" rx="2" fill="#FFF" opacity=".85"/>`).join('')}`; break;
    case 'teacher': s = `<rect width="160" height="90" fill="#F7F8FA"/><rect width="160" height="16" fill="#2F5BEA"/><rect x="10" y="5" width="70" height="6" rx="1" fill="#FFF"/><rect x="10" y="24" width="64" height="4" rx="1" fill="#15181C"/>${lines(10, 33, 5, 60, '#6B7380', 6)}<rect x="86" y="24" width="64" height="56" rx="3" fill="#1E1F22"/>${lines(90, 30, 8, 54, codeCol, 6)}`; break;
    case 'lock': s = `<rect width="160" height="90" fill="#101C2C"/><rect x="68" y="20" width="24" height="24" rx="6" fill="#F0B42C"/><path d="M75 33h10v7H75zM77 33v-3a3 3 0 0 1 6 0v3" stroke="#101C2C" stroke-width="1.6" fill="none"/><rect x="45" y="52" width="70" height="4" rx="2" fill="#F4F6F8"/><rect x="55" y="60" width="50" height="3" rx="1.5" fill="#A8B4C4"/>`; break;
    case 'timeup': s = `<rect width="160" height="90" fill="#101C2C"/><rect x="68" y="20" width="24" height="24" rx="6" fill="#2DD4BF"/><rect x="40" y="52" width="80" height="4" rx="2" fill="#F4F6F8"/>`; break;
  }
  if (extra === 'strip') s += `<rect x="52" y="0" width="56" height="7" rx="2" fill="#101C2C" opacity=".92"/><rect x="56" y="2.5" width="14" height="2" fill="#2DD4BF"/><rect x="73" y="2.5" width="30" height="2" fill="#F4F6F8"/>`;
  return (thCache[key] = `<svg class="th" viewBox="0 0 160 90" preserveAspectRatio="xMidYMid slice">${s}</svg>`);
}
function pcThumb(p) {
  if (p.lock) return thumb('lock', p.n);
  if (p.cast) return thumb('teacher', 1);
  if (p.timeUp) return thumb('timeup', 1);
  return thumb(p.app, p.n, p.exam ? 'strip' : '');
}

/* ---------------------------------------------------------------- grid algorithm (spec.md §4): always fits, never scrolls */
const AR = 16 / 9, WMAX = 560, DENSE_W = 150;
const NORMAL = { cap: 26, gap: 10 }, DENSE = { cap: 18, gap: 6 };
function bestFor(W, H, cols, rows, m) { // tile width for a fixed cols x rows block
  const wW = (W - (cols - 1) * m.gap) / cols, wH = ((H - (rows - 1) * m.gap) / rows - m.cap) * AR;
  return Math.max(1, Math.min(wW, wH, WMAX));
}
function fitGrid(W, H, N, m) {
  let best = null;
  for (let c = 1; c <= N; c++) {
    const r = Math.ceil(N / c), w = bestFor(W, H, c, r, m), empty = c * r - N;
    if (!best || w > best.w + 0.5 || (Math.abs(w - best.w) <= 0.5 && empty < best.empty)) best = { cols: c, rows: r, w, empty };
  }
  return best;
}
/** Picks the metrics: the normal caption, or the dense one (name + dot, 6 px gaps) when tiles would be narrower than 150 px. */
function fitAll(W, H, N, cols, rows) {
  const run = m => cols ? { cols, rows, w: bestFor(W, H, cols, rows, m) } : fitGrid(W, H, N, m);
  let m = NORMAL, L = run(m);
  if (L.w < DENSE_W) { m = DENSE; L = run(m); }
  return { ...L, m };
}
function layout() {
  const m = $('.mosaic'); if (!m) return; const cv = $('.canvas', m); if (!cv) return;
  const W = m.clientWidth - 24, H = m.clientHeight - 16; const N = S.pcs.length; let L, pos = {};
  if (!N) return;
  if (S.grid === 'fit') {
    L = fitAll(W, H, N);
    [...S.pcs].sort((a, b) => a.n - b.n).forEach((p, i) => pos[p.n] = [i % L.cols, Math.floor(i / L.cols)]);
  } else {
    const spare = S.arrange ? 1 : 0; // one spare row and column to drag into, still inside the window
    const cols = Math.max(...S.pcs.map(p => p.col)) + 1 + spare, rows = Math.max(...S.pcs.map(p => p.row)) + 1 + spare;
    L = fitAll(W, H, N, cols, rows);
    S.pcs.forEach(p => pos[p.n] = [p.col, p.row]);
  }
  const pct = S.zoom || 100;                       // the size control: 65–100 % of the fitted size, never more
  const gap = L.m.gap, cap = L.m.cap;
  const w = Math.max(1, Math.floor(L.w * pct / 100)), h = Math.floor(w / AR) + cap;
  const bw = L.cols * w + (L.cols - 1) * gap, bh = L.rows * h + (L.rows - 1) * gap;
  cv.style.width = bw + 'px'; cv.style.height = bh + 'px';
  cv.style.marginTop = Math.max(8, (H + 16 - bh) / 2) + 'px';
  cv.classList.toggle('dense', L.m === DENSE);
  cv.querySelectorAll('.cell').forEach(e => e.remove());
  if (S.grid === 'plan' && S.arrange) for (let c = 0; c < L.cols; c++) for (let r = 0; r < L.rows; r++) {
    const d = document.createElement('div'); d.className = 'cell'; Object.assign(d.style, { left: c * (w + gap) + 'px', top: r * (h + gap) + 'px', width: w + 'px', height: h + 'px' }); cv.prepend(d);
  }
  cv.querySelectorAll('.tile').forEach(el => {
    const n = +el.dataset.n; const [c, r] = pos[n];
    Object.assign(el.style, { left: c * (w + gap) + 'px', top: r * (h + gap) + 'px', width: w + 'px', height: h + 'px' });
    el.classList.toggle('small', w < 170);
    el.classList.toggle('tiny', w < 110);
  });
  S._cell = { w, h, gap }; S._L = L;
  const info = $('#gridinfo'); if (info) info.textContent = `${L.cols}×${L.rows} · ${w}px`;
  fitModeChips(); fitBanners();
}
/** One mode line: chips that do not fit go into a "+N" chip. */
function fitModeChips() {
  const strip = $('.mstrip'); if (!strip) return;
  const chips = [...strip.querySelectorAll('.mchip')], more = strip.querySelector('.mmore');
  chips.forEach(c => c.style.display = ''); more.style.display = 'none';
  let hidden = 0;
  for (let i = chips.length - 1; i >= 0 && strip.scrollWidth > strip.clientWidth + 1; i--) {
    chips[i].style.display = 'none'; hidden++; more.style.display = ''; more.querySelector('b').textContent = '+' + hidden;
  }
  S._hiddenModes = hidden;
}
function fitBanners() {}
/** A narrow window: ribbon / command-bar buttons that do not fit move, from the end, into "More". */
function fitBars() {
  S._ovf = [];
  const rb = $('.rbody');
  if (rb) {
    const more = rb.querySelector('.ovf'); const items = [...rb.querySelectorAll('.rb:not(.ovf)')];
    for (let i = items.length - 1; i >= 0 && rb.scrollWidth > rb.clientWidth + 1; i--) {
      more.style.display = ''; items[i].style.display = 'none';
      const sep = items[i].parentElement.previousElementSibling; if (items[i].parentElement.querySelectorAll('.rb:not([style*="none"])').length === 0 && sep?.classList.contains('rsep')) sep.style.display = 'none';
      S._ovf.unshift([items[i].dataset.act, items[i].dataset.arg, items[i].querySelector('span').textContent, items[i].disabled]);
    }
  }
  const cb = $('.cmdbar');
  if (cb) {
    const items = [...cb.querySelectorAll('.cb:not(.keep)')];
    for (let i = items.length - 1; i >= 0 && cb.scrollWidth > cb.clientWidth + 1; i--) {
      items[i].style.display = 'none'; S._ovf.unshift([items[i].dataset.act, items[i].dataset.arg, items[i].querySelector('span').textContent, items[i].disabled]);
    }
  }
}

/* ---------------------------------------------------------------- top-level render */
function render() {
  document.documentElement.dataset.theme = S.theme;
  document.documentElement.lang = S.lang;
  renderStrip();
  const win = $('#win');
  const sizes = { '1440': [1440, 900], '1280': [1280, 720], 'free': [1280, 800] };
  if (S.win !== 'free') { const [w, h] = sizes[S.win]; win.style.width = w + 'px'; win.style.height = h + 'px'; win.classList.remove('free'); }
  else if (!win.classList.contains('free')) { win.classList.add('free'); win.style.width = '1280px'; win.style.height = '800px'; }
  let title = 'LabControl';
  if (S.window === 'console') title = `444-2 — ${admin() ? t('role.admin') : t('role.teacher')} · LabControl`;
  if (S.window === 'chooser') title = t('chooser.title') + ' · LabControl';
  if (S.window === 'first') title = t('first.title');
  if (S.window === 'student') title = t('stu.title');
  if (S.window === 'setup') title = t('setupsec.title');
  let body = '';
  if (S.window === 'console') body = consoleView();
  else if (S.window === 'chooser') body = chooserView();
  else if (S.window === 'first') body = firstRunView();
  else if (S.window === 'student') body = studentView();
  else if (S.window === 'setup') body = setupSection();
  win.innerHTML = `<div class="titlebar"><div class="lights"><i></i><i></i><i></i></div>${esc(title)}</div><div class="app">${body}</div>${S.full ? fullView() : ''}${S.modal ? modalView() : ''}${S.menu ? menuView() : ''}${toastsView()}`;
  layout(); fitBars(); scale(); bindMosaic();
}
function rerenderKeepScroll() { const m = $('.mosaic'); const st = m ? m.scrollTop : 0; render(); const m2 = $('.mosaic'); if (m2) m2.scrollTop = st; }

function renderStrip() {
  const g = (lbl, key, opts) => `<span class="grp"><span class="lbl">${lbl}</span>${opts.map(([v, l]) => `<button data-s="${key}" data-v="${v}" class="${String(S[key]) === String(v) ? 'on' : ''}">${l}</button>`).join('')}</span>`;
  $('#strip').innerHTML = `<span class="title">LabControl · M7 redesign — claude-opus</span>` +
    g('Window', 'window', [['console', 'Console'], ['chooser', 'My labs'], ['first', 'First run'], ['student', 'Student PC'], ['setup', 'PC installer']]) +
    g('Variant', 'variant', [['A', 'A · Ribbon — owner’s choice'], ['B', 'B · Sidebar (reference)']]) +
    g('Theme', 'theme', [['light', 'Light'], ['dark', 'Dark']]) +
    g('Lang', 'lang', [['uk', 'UK'], ['ru', 'RU'], ['en', 'EN']]) +
    g('PCs', 'count', [[1, '1'], [8, '8'], [14, '14'], [30, '30']]) +
    g('State', 'scenario', [['showcase', 'All states'], ['lesson', 'Lesson'], ['empty', 'New lab']]) +
    g('Size', 'win', [['1440', '1440×900'], ['1280', '1280×720'], ['free', 'Resizable']]) +
    g('Role', 'role', [['admin', 'Admin'], ['teacher', 'Teacher']]);
}
function scale() {
  const strip = $('#strip'), stage = $('#stage'), win = $('#win'), sc = $('#scaler');
  stage.style.top = strip.offsetHeight + 'px';
  const aw = stage.clientWidth - 24, ah = stage.clientHeight - 24;
  const k = Math.min(1, aw / win.offsetWidth, ah / win.offsetHeight);
  sc.style.transform = `scale(${k})`; sc.style.marginTop = '12px'; S._scale = k;
}

/* ---------------------------------------------------------------- console (both variants) */
const ICON_OF = { cast: 'cast', lock: 'lock', unlock: 'unlock', send: 'send-files', collect: 'collect', view: 'view', scriptmenu: 'play', exam: 'exam', extend: 'timer-plus', endexam: 'flag', internet: 'globe', netopen: 'globe', wake: 'wake', reboot: 'reboot', logoff: 'logoff', shutdown: 'power', addpcs: 'monitor-plus', install: 'install', reset: 'reset-user', push: 'update', remove: 'trash', powermenu: 'power' };
const SECTIONS = [['class', 'class', 'nav.class'], ['programs', 'programs', 'nav.programs'], ['scripts', 'scripts', 'nav.scripts'], ['activity', 'activity', 'nav.activity'], ['admin', 'admin', 'nav.lab']];
function consoleView() {
  const winW = $('#win').offsetWidth || 1440, winH = $('#win').offsetHeight || 900;
  S._compact = winH < 800;
  const main = S.screen === 'class' ? classView() : S.screen === 'programs' ? programsView() : S.screen === 'scripts' ? scriptsView() : S.screen === 'activity' ? activityView() : S.screen === 'admin' ? adminView() : settingsView();
  const online = S.pcs.filter(isOn).length;
  const status = `<div class="status"><span>${ic('monitor', 's14')} ${t('st.online', online, S.pcs.length)}</span>${S.pcs.some(p => p.status === 'off') ? `<span>${ic('power', 's14')} ${t('st.off', S.pcs.filter(p => p.status === 'off').length)}</span>` : ''}${S.pcs.some(p => p.problem) ? `<span style="color:var(--bad)">${ic('alert', 's14')} ${t('st.problems', S.pcs.filter(p => p.problem).length)}</span>` : ''}<span class="sp"></span>${S.cast ? `<span style="color:var(--cast);font-weight:600">${ic('cast', 's14')} ${t('st.cast', S.cast.targets.length)}</span>` : ''}${admin() ? `<span>${ic("key", "s14")} ${keyText(true)}</span>` : `<span>${ic('info', 's14')} ${t('st.teacher')}</span>`}</div>`;
  if (S.variant === 'A') {
    const tabs = SECTIONS.map(([id, i, k]) => `<button class="${S.screen === id ? 'on' : ''}" data-act="screen" data-arg="${id}">${ic(i)}<span>${t(k)}</span></button>`).join('');
    const top = `<div class="topbar"><span class="brand"><img class="logo" src="${LOGO}" alt=""></span>${labSwitch()}<div class="sec">${tabs}</div><span class="sp"></span>${onlinePill(online)}${admin() ? keyPill() : ''}<button class="ib" data-act="screen" data-arg="activity" data-tip="${esc(t('tip.notif'))}">${ic('bell')}${S.toasts.some(x => x.kind === 'part') || S.pcs.some(p => p.problem) ? '<span class="cnt">1</span>' : ''}</button><button class="ib ${S.screen === 'settings' ? 'on' : ''}" data-act="screen" data-arg="settings" data-tip="${esc(t('nav.settings'))}">${ic('settings')}</button></div>`;
    return `<div class="col">${top}${bannersView()}${main}${status}</div>`;
  }
  const rail = S.railForced ?? (winW < 1360);
  const nav = [...SECTIONS, ['settings', 'settings', 'nav.settings']].map(([id, i, k]) => `<button class="${S.screen === id ? 'on' : ''}" data-act="screen" data-arg="${id}" ${rail ? `data-tip="${esc(t(k))}"` : ''}>${ic(i)}<span>${t(k)}</span></button>`).join('');
  const side = `<aside class="side ${rail ? 'rail' : ''}">${labSwitch(true)}<nav>${nav}</nav><div class="foot">${admin() ? `<button class="pill ${S.keyOpenUntil > now() || S.keyRemembered ? 'keyopen' : ''}" data-act="keymenu" style="justify-content:${rail ? 'center' : 'flex-start'}">${ic('key', 's16')}${rail ? '' : `<span>${keyText(true)}</span>`}</button>` : ''}<nav><button data-act="sidebar">${ic('sidebar')}<span>${rail ? t('nav.expand') : t('nav.collapse')}</span></button></nav></div></aside>`;
  return `${side}<div class="col">${bannersView()}${main}${status}</div>`;
}
function labSwitch(side) {
  return `<button class="labsw" data-act="labmenu">${ic('labs', 's16')}<span style="display:flex;flex-direction:column;align-items:flex-start;min-width:0"><span class="nm">${t('lab.name')}</span><span class="role">${admin() ? t('role.admin') : t('role.teacher')}</span></span>${side ? '<span class="sp"></span>' : ''}<span class="chev">${ic('chevron-down', 's14')}</span></button>`;
}
function onlinePill(online) { return `<button class="pill ok" data-act="screen" data-arg="class" data-tip="${esc(t('tip.online'))}">${ic('monitor', 's16')}<b>${online}</b>/${S.pcs.length} ${t('pill.online')}</button>`; }
function keyText(short) {
  if (S.keyRemembered) return short ? t('key.rem.s') : t('key.rem');
  if (S.keyOpenUntil > now()) return t('key.open', new Date(S.keyOpenUntil * 1000).toTimeString().slice(0, 5));
  return t('key.locked');
}
function keyPill() { const open = S.keyOpenUntil > now() || S.keyRemembered; return `<button class="pill ${open ? 'keyopen' : ''}" data-act="keymenu" data-tip="${esc(t('tip.key'))}">${ic('key', 's16')}${keyText(true)}</button>`; }
function bannersView() {
  if (!S.banners || S.scenario !== 'showcase' || !S.pcs.length) return '';
  if (($('#win')?.offsetHeight || 900) < 600) return ''; // a very low window: banners live only in the bell
  let b = '';
  const list = [];
  if (S.pcs.some(p => p.status === 'elsewhere')) list.push(`<div class="banner info">${ic('swap', 's16')}<span class="txt">${t('ban.other', 'Ноутбук Олени', 1, S.pcs.length, pcName(S.pcs.find(p => p.status === 'elsewhere').n))}</span><button class="btn" data-act="takeover">${ic('swap', 's16')}${t('ban.takeover')}</button><button class="ib" data-act="closebanners" data-tip="${esc(t('tip.hide'))}">${ic('close', 's16')}</button></div>`);
  if (admin()) list.push(`<div class="banner">${ic('backup', 's16')}<span class="txt">${t('ban.backup')}</span><button class="btn" data-act="backup">${ic('download', 's16')}${t('a.backup')}</button></div>`);
  if (!list.length) return '';
  // banners never stack: one compact line; the rest wait behind "+N" (and in the bell)
  const shown = S.bannersOpen ? list : [list[0]];
  b = shown.join('');
  if (list.length > 1) b = b.replace(/<\/div>$/, `<button class="btn ghost" data-act="bannermore">${S.bannersOpen ? t('ban.less') : '+' + (list.length - 1)}</button></div>`);
  return b;
}

/* ---------------------------------------------------------------- class screen */
function tgtLabel() { return S.sel.size ? t('tgt.sel', S.sel.size) : t('tgt.all', S.pcs.length); }
function classView() {
  const empty = !S.pcs.length;
  let actions = '';
  if (S.variant === 'A') actions = ribbonView(empty);
  else actions = cmdbarView(empty);
  const modes = empty ? '' : modeBars();
  const tool = empty ? '' : mosaicTool();
  const mosaic = empty ? emptyView() : `<div class="mosaic" id="mosaic"><div class="canvas" id="canvas">${S.pcs.map(tileView).join('')}</div></div>`;
  const panel = S.variant === 'B' && S.sel.size && !empty ? selPanel() : '';
  return `${actions}${modes}<div style="flex:1;display:flex;min-height:0"><div class="col mcol">${tool}${mosaic}</div>${panel}</div>`;
}
function rb(act, icon, label, opts = {}) {
  return `<button class="rb ${opts.cls || ''}" data-act="${act}" ${opts.arg ? `data-arg="${opts.arg}"` : ''} ${opts.dis ? 'disabled' : ''} data-tip="${esc(opts.tip || label)}">${ic(icon)}<span>${label}</span>${opts.caret ? `<span class="caret">${ic('chevron-down', 's14')}</span>` : ''}</button>`;
}
function ribbonView(empty) {
  const tabs = [['lesson', 'rt.lesson'], ['control', 'rt.control'], ['power', 'rt.power'], ['pcs', 'rt.pcs']];
  const dotFor = id => (id === 'control' && (examPcs().length || S.pcs.some(p => p.net))) ? `<span class="dot" style="background:var(--exam)"></span>` : (id === 'lesson' && (S.cast || lockedPcs().length)) ? `<span class="dot" style="background:var(--cast)"></span>` : '';
  const rt = `<div class="rtabs">${tabs.map(([id, k]) => `<button class="${S.ribbon === id ? 'on' : ''}" data-act="ribbon" data-arg="${id}">${t(k)}${dotFor(id)}</button>`).join('')}</div>`;
  const tg = `<div class="target"><span class="k">${t('tgt.for')}</span><span class="v">${ic(S.sel.size ? 'select-all' : 'class', 's16')}${tgtLabel()}${S.sel.size ? `<button data-act="clearsel" data-tip="${esc(t('a.clearsel'))}">${ic('close', 's14')}</button>` : ''}</span></div>`;
  const one = S.sel.size === 1; const anyOn = targets().some(isOn) && !empty;
  let b = '';
  if (S.ribbon === 'lesson') b = `<div class="rgrp">${rb('cast', S.cast ? 'cast-off' : 'cast', S.cast ? t('a.stopcast') : t('a.cast'), { cls: 'primary', dis: empty, caret: !S.cast })}${rb('lock', 'lock', t('a.lock'), { dis: empty })}${rb('unlock', 'unlock', t('a.unlock'), { dis: !targets().some(p => p.lock) })}</div><div class="rsep"></div><div class="rgrp">${rb('send', 'send-files', t('a.send'), { dis: empty })}${rb('collect', 'collect', t('a.collect'), { dis: empty })}</div><div class="rsep"></div><div class="rgrp">${rb('view', 'view', t('a.view'), { dis: !one, tip: one ? t('a.view') : t('tip.viewone') })}${rb('scriptmenu', 'play', t('a.runscript'), { dis: empty, caret: true })}</div>`;
  if (S.ribbon === 'control') b = `<div class="rgrp">${rb('exam', 'exam', t('a.exam'), { cls: 'primary', dis: empty })}${rb('extend', 'timer-plus', t('a.extend'), { dis: !examPcs().length })}${rb('endexam', 'flag', t('a.endexam'), { dis: !examPcs().length })}</div><div class="rsep"></div><div class="rgrp">${rb('internet', 'globe', t('a.internet'), { dis: empty })}${rb('netopen', 'globe', t('a.netopen'), { dis: !targets().some(p => p.net) })}</div><div class="rsep"></div><div class="rgrp">${rb('collect', 'collect', t('a.collect'), { dis: empty })}</div>`;
  if (S.ribbon === 'power') b = `<div class="rgrp">${rb('wake', 'wake', t('a.wake'), { cls: 'primary', dis: empty || !targets().some(p => !isOn(p)) })}${rb('reboot', 'reboot', t('a.reboot'), { dis: !anyOn })}${rb('logoff', 'logoff', t('a.logoff'), { dis: !anyOn })}</div><div class="rsep"></div><div class="rgrp">${rb('shutdown', 'power', t('a.shutdown'), { cls: 'danger', dis: !anyOn })}</div>`;
  if (S.ribbon === 'pcs') b = `<div class="rgrp">${admin() ? rb('addpcs', 'monitor-plus', t('a.addpcs'), { cls: 'primary' }) : ''}${rb('install', 'install', t('a.install'), { dis: empty })}</div><div class="rsep"></div><div class="rgrp">${rb('reset', 'reset-user', t('a.reset'), { cls: 'danger', dis: empty })}${admin() ? rb('push', 'update', t('a.push'), { dis: empty }) : ''}${admin() ? rb('remove', 'trash', t('a.remove'), { cls: 'danger', dis: !S.sel.size, tip: S.sel.size ? t('a.remove') : t('tip.removesel') }) : ''}</div>${admin() ? '' : `<div class="rsep"></div><div class="note" style="align-self:center;max-width:360px">${ic('info', 's16')}<span>${t('teacher.hint')}</span></div>`}`;
  return `<div class="ribbon ${S._compact ? 'compact' : ''}">${rt}<div class="rbody">${tg}${b}<button class="rb ovf" data-act="ovfmenu" style="display:none">${ic('more')}<span>${t('a.more')}</span></button></div></div>`;
}
function cmdbarView(empty) {
  const cb = (act, icon, label, opts = {}) => `<button class="cb ${opts.cls || ''}" data-act="${act}" ${opts.dis ? 'disabled' : ''} data-tip="${esc(opts.tip || label)}">${ic(icon)}<span>${label}</span>${opts.caret ? ic('chevron-down', 's14') : ''}</button>`;
  const anyLocked = targets().some(p => p.lock);
  return `<div class="cmdbar"><div class="target" style="min-width:0;flex-direction:row;align-items:center;gap:8px;border:0;margin:0 6px 0 0"><span class="v">${ic(S.sel.size ? 'select-all' : 'class', 's16')}${tgtLabel()}${S.sel.size ? `<button data-act="clearsel">${ic('close', 's14')}</button>` : ''}</span></div><span class="rsep" style="height:28px"></span>
  ${cb('cast', S.cast ? 'cast-off' : 'cast', S.cast ? t('a.stopcast') : t('a.cast'), { cls: S.cast ? '' : 'primary', dis: empty, caret: !S.cast })}
  ${anyLocked ? cb('unlock', 'unlock', t('a.unlock')) : cb('lock', 'lock', t('a.lock'), { dis: empty })}
  ${cb('send', 'send-files', t('a.send'), { dis: empty })}
  ${cb('exam', 'exam', t('a.exam'), { dis: empty })}
  ${cb('internet', 'globe', t('a.internet'), { dis: empty })}
  ${cb('powermenu', 'power', t('a.power'), { dis: empty, caret: true })}
  ${cb('moremenu', 'more', t('a.more'), { dis: empty && !admin(), caret: true, cls: 'keep' })}</div>`;
}
function modeBars() {
  const ex0 = examPcs(), lk0 = lockedPcs(), nt0 = S.pcs.filter(p => p.net && !p.exam);
  const n = (ex0.length ? 1 : 0) + (S.cast ? 1 : 0) + (lk0.length ? 1 : 0) + (nt0.length ? 1 : 0);
  if (n >= 2) { // several modes at once: ONE line of chips; chips that do not fit go into "+N"
    const chip = (cls, icon, txt, act, lbl) => `<span class="mchip ${cls}"><span class="ico">${ic(icon)}</span><span class="mt">${txt}</span><button class="btn" data-act="${act}">${lbl}</button></span>`;
    let c = '';
    if (ex0.length) c += chip('exam', 'exam', `${t('mb.exam', ex0.length)} · <b data-cd="exam">${mmss(Math.max(...ex0.map(p => p.exam.end - now())))}</b>`, 'endexam', t('a.endexam'));
    if (S.cast) c += chip('cast', 'cast', t('mb.cast', S.cast.targets.length), 'stopcast', t('a.stopcast'));
    if (lk0.length) c += chip('lock', 'lock', t('mb.lock', lk0.length), 'unlockall', t('a.unlockall'));
    if (nt0.length) c += chip('net', 'globe', t('mb.net.s', nt0.length), 'netopenall', t('a.netopenall'));
    return `<div class="mstrip">${c}<button class="mchip mmore" data-act="modesmenu" style="display:none" data-tip="${esc(t('tip.moremodes'))}"><b>+1</b></button></div>`;
  }
  let s = '';
  const ex = examPcs();
  if (ex.length) {
    const rem = Math.max(...ex.map(p => p.exam.end - now()));
    s += `<div class="modebar exam"><span class="ico">${ic('exam')}</span><span class="txt">${t('mb.exam', ex.length)} · ${t('mb.left')} <span class="big" data-cd="exam">${mmss(rem)}</span>${S.exam?.collect ? ' · ' + t('mb.collectatend') : ''}</span><button class="btn" data-act="extend10">${ic('timer-plus', 's16')}+10 ${t('u.min')}</button><button class="btn" data-act="extend">${t('a.extend')}</button><button class="btn danger" data-act="endexam">${ic('flag', 's16')}${t('a.endexam')}</button></div>`;
  }
  if (S.cast) s += `<div class="modebar cast"><span class="ico">${ic('cast')}</span><span class="txt">${t('mb.cast', S.cast.targets.length)} · <span class="dim">${S.cast.mode === 'video' ? t('mb.castvideo') : t('mb.casttext')}</span></span><span class="seg"><button class="${S.cast.mode === 'text' ? 'on' : ''}" data-act="castmode" data-arg="text">${ic('text-mode', 's16')}${t('vm.text')}</button><button class="${S.cast.mode === 'video' ? 'on' : ''}" data-act="castmode" data-arg="video">${ic('video-mode', 's16')}${t('vm.video')}</button></span><button class="btn danger" data-act="stopcast">${ic('cast-off', 's16')}${t('a.stopcast')}</button></div>`;
  const lk = lockedPcs();
  if (lk.length) s += `<div class="modebar lock"><span class="ico">${ic('lock')}</span><span class="txt">${t('mb.lock', lk.length)}${lk.length <= 4 ? ' · ' + lk.map(p => pcName(p.n)).join(', ') : ''}</span><button class="btn" data-act="unlockall">${ic('unlock', 's16')}${t('a.unlockall')}</button></div>`;
  const nt = S.pcs.filter(p => p.net && !p.exam);
  if (nt.length) s += `<div class="modebar net"><span class="ico">${ic('globe')}</span><span class="txt">${t('mb.net', nt.length)}</span><button class="btn" data-act="internet">${t('a.change')}</button><button class="btn" data-act="netopenall">${ic('globe', 's16')}${t('a.netopenall')}</button></div>`;
  return s;
}
function mosaicTool() {
  const all = S.sel.size === S.pcs.length && S.pcs.length; const some = S.sel.size && !all;
  return `<div class="mtool"><button class="chk" data-act="selall"><span class="box ${all ? 'on' : some ? 'mixed' : ''}">${all ? ic('check', 's14') : ''}</span><span class="lbl2">${t('a.selall')}</span></button>${S.sel.size ? `<span class="dim selcount">${t('sel.count', S.sel.size)}</span>` : `<span class="dim small sel-hint">${t('sel.hint')}</span>`}
  <span class="seg"><button class="${S.grid === 'fit' ? 'on' : ''}" data-act="grid" data-arg="fit" data-tip="${esc(t('tip.fit'))}">${ic('fit', 's16')}<span class="sl">${t('g.fit')}</span></button><button class="${S.grid === 'plan' ? 'on' : ''}" data-act="grid" data-arg="plan" data-tip="${esc(t('tip.plan'))}">${ic('room', 's16')}<span class="sl">${t('g.plan')}</span></button></span>
  ${S.grid === 'plan' ? `<button class="btn ${S.arrange ? 'primary' : ''}" data-act="arrange">${ic('grip', 's16')}<span class="lb">${S.arrange ? t('g.done') : t('g.arrange')}</span></button>${S.arrange ? `<button class="btn ghost" data-act="resetplan">${t('g.reset')}</button>` : ''}` : ''}
  <span class="range"><button class="ib" data-act="zoom" data-arg="-1" data-tip="${esc(t('tip.smaller'))}">${ic('zoom-out', 's16')}</button><input id="zoomr" type="range" min="65" max="100" step="5" value="${S.zoom || 100}" aria-label="${esc(t('tip.size'))}"><button class="ib" data-act="zoom" data-arg="1" data-tip="${esc(t('tip.larger'))}">${ic('zoom-in', 's16')}</button><button class="btn ghost ${S.zoom ? '' : 'on'}" data-act="zoomauto" style="${S.zoom ? '' : 'color:var(--accent);font-weight:700'}">${t('g.auto')}</button></span><span class="dim small gi" id="gridinfo" style="min-width:86px;font-variant-numeric:tabular-nums"></span>
  <button class="ib" data-act="legend" data-tip="${esc(t('tip.legend'))}">${ic('legend')}</button></div>`;
}
function emptyView() {
  if (!admin()) return `<div class="empty"><div class="ecard">${ic('monitor', 's48')}<h2>${t('empty.t.teacher')}</h2><p>${t('empty.p.teacher')}</p></div></div>`;
  return `<div class="empty"><div class="ecard"><div style="color:var(--accent)">${ic('monitor-plus', 's48')}</div><h2>${t('empty.t')}</h2><p>${t('empty.p')}</p><div class="steps3"><div><b><span class="num">1</span>${ic('usb', 's16')}${t('empty.s1')}</b>${t('empty.s1d')}</div><div><b><span class="num">2</span>${ic('monitor', 's16')}${t('empty.s2')}</b>${t('empty.s2d')}</div><div><b><span class="num">3</span>${ic('check', 's16')}${t('empty.s3')}</b>${t('empty.s3d')}</div></div><button class="btn primary lg" data-act="addpcs">${ic('monitor-plus')}${t('a.addpcs')}</button></div></div>`;
}
function tileView(p) {
  const sel = S.sel.has(p.n);
  const badges = [];
  if (p.lock) badges.push(`<span class="bdg lock">${ic('lock')}<span>${t('b.locked')}</span></span>`);
  if (p.cast) badges.push(`<span class="bdg cast">${ic('cast')}<span>${t('b.cast')}</span></span>`);
  if (p.exam) badges.push(`<span class="bdg exam">${ic('timer')}<span data-cd="pc" data-n="${p.n}">${mmss(p.exam.end - now())}</span></span>`);
  if (p.net) badges.push(`<span class="bdg net">${ic(p.net === 'none' ? 'globe-off' : 'globe')}<span>${p.net === 'none' ? t('b.nonet') : t('b.list')}</span></span>`);
  if (p.status === 'outdated') badges.push(`<span class="bdg warn">${ic('update')}<span>${t('b.outdated')}</span></span>`);
  if (p.renewal) badges.push(`<span class="bdg warn">${ic('clock')}<span>${t('b.renew')}</span></span>`);
  if (p.problem) badges.push(`<span class="bdg bad">${ic('alert')}<span>${t('b.problem')}</span></span>`);
  const frame = p.problem ? 'f-bad' : p.lock ? 'f-lock' : p.cast ? 'f-cast' : p.exam ? 'f-exam' : p.net ? 'f-net' : '';
  let cls = 'tile ' + frame + (sel ? ' sel' : ''); let mid = '', st = '';
  if (p.status === 'off') { cls += ' off'; mid = `<div class="mid">${ic('power', 's24')}<span class="t">${t('s.off')}</span><span>${t('s.lastseen', p.seen)}</span></div>`; st = `<span class="st"><span>${t('s.off')}</span></span>`; }
  else if (p.status === 'waking') { cls += ' off waking'; mid = `<div class="mid">${ic('wake', 's24')}<span class="t">${t('s.waking')}</span></div>`; st = `<span class="st warn"><span class="dotc wake"></span><span>${t('s.waking')}</span></span>`; }
  else if (p.status === 'elsewhere') { cls += ' off'; mid = `<div class="mid">${ic('elsewhere', 's24')}<span class="t">${t('s.elsewhere')}</span><span>${esc(p.heldBy)}</span></div>`; st = `<span class="st"><span>${t('s.elsewhere.s')}</span></span>`; }
  else if (p.nopic) { cls += ' nopic'; mid = `<div class="mid">${ic('image-off', 's24')}<span class="t">${t('s.nopic')}</span></div>`; st = `<span class="dotc on"></span>`; }
  else st = p.problem ? `<span class="st bad"><span class="dotc" style="background:var(--bad)"></span><span>${t('s.problem.s')}</span></span>` : `<span class="dotc on" title="${esc(t('s.online'))}"></span>`;
  const showPic = !['off', 'waking', 'elsewhere'].includes(p.status) && !p.nopic;
  return `<div class="${cls}" data-n="${p.n}" tabindex="0" role="button" aria-pressed="${sel}" aria-label="${pcName(p.n)}"><div class="shot">${showPic ? pcThumb(p) : ''}${mid}<div class="badges">${badges.join('')}</div><span class="check" data-act="toggle" data-arg="${p.n}">${ic('check', 's14')}</span></div>${S.grid === 'plan' && S.arrange ? `<span class="grip">${ic('grip', 's16')}</span>` : ''}<div class="cap"><span class="nm">${pcName(p.n)}</span><span class="us">${p.user ? ic('student', 's14').replace('class="i', 'style="vertical-align:-2px;opacity:.6" class="i') + ' ' + p.user : ''}</span>${st}</div></div>`;
}
function pcStates(p) { // the side panel / tooltip lines: words for what the PC reports
  const L = [];
  if (p.status === 'off') L.push(['off', 'power', t('s.off') + ' · ' + t('s.lastseen', p.seen)]);
  if (p.status === 'waking') L.push(['warn', 'wake', t('s.waking.l')]);
  if (p.status === 'elsewhere') L.push(['off', 'elsewhere', t('s.elsewhere.l', p.heldBy)]);
  if (isOn(p) && !p.problem) L.push(['ok', 'check', t('s.online.l', p.user || '—')]);
  if (p.lock) L.push(['lock', 'lock', t('s.lock.l')]);
  if (p.cast) L.push(['cast', 'cast', t('s.cast.l')]);
  if (p.exam) L.push(['exam', 'exam', t('s.exam.l', mmss(p.exam.end - now()))]);
  if (p.net) L.push(['net', p.net === 'none' ? 'globe-off' : 'globe', p.net === 'none' ? t('s.nonet.l') : t('s.list.l')]);
  if (p.problem) L.push(['bad', 'alert', t('s.problem.l')]);
  if (p.status === 'outdated') L.push(['warn', 'update', t('s.outdated.l')]);
  if (p.renewal) L.push(['warn', 'clock', t('s.renew.l')]);
  if (p.nopic) L.push(['warn', 'image-off', t('s.nopic.l')]);
  return L;
}
function selPanel() {
  const list = S.pcs.filter(p => S.sel.has(p.n));
  if (list.length > 1) {
    return `<aside class="panel"><div class="ph">${ic('select-all')}${t('sel.count', list.length)}<span class="sp"></span><button class="ib" data-act="clearsel">${ic('close', 's16')}</button></div><div class="pb"><div class="chips">${list.map(p => `<span class="chip" data-act="selone" data-arg="${p.n}">${pcName(p.n)}</span>`).join('')}</div><div class="grp-h">${t('pnl.actions')}</div><div class="alist">${panelActions(false)}</div></div></aside>`;
  }
  const p = list[0]; const showPic = !['off', 'waking', 'elsewhere'].includes(p.status) && !p.nopic;
  return `<aside class="panel"><div class="ph">${ic('monitor')}${pcName(p.n)}<span class="dim small" style="font-weight:500">${p.user ? '· ' + p.user : ''}</span><span class="sp"></span><button class="ib" data-act="clearsel" data-tip="${esc(t('a.clearsel'))}">${ic('close', 's16')}</button></div><div class="pb"><div class="big">${showPic ? pcThumb(p) : ''}</div>${pcStates(p).map(([c, i, s]) => `<div class="stline ${c}">${ic(i, 's16')}<span>${s}</span></div>`).join('')}<div class="alist">${panelActions(true, p)}</div><dl class="kv"><dt>${t('kv.host')}</dt><dd>${p.host}</dd><dt>${t('kv.ip')}</dt><dd>${p.ip}</dd><dt>${t('kv.agent')}</dt><dd>${p.ver}</dd><dt>${t('kv.seen')}</dt><dd>${p.seen}</dd></dl></div></aside>`;
}
function panelActions(one, p) {
  const a = (act, icon, label, cls = '', dis = false) => `<button class="${cls}" data-act="${act}" ${dis ? 'disabled style="opacity:.4"' : ''}>${ic(icon, 's16')}${label}</button>`;
  return (one ? a('view', 'view', t('a.view')) + a('viewctl', 'control', t('a.control')) : '') + a('lock', 'lock', t('a.lock')) + a('unlock', 'unlock', t('a.unlock')) + a('cast', 'cast', t('a.casthere')) + a('send', 'send-files', t('a.send')) + a('collect', 'collect', t('a.collect')) + '<hr style="border:0;border-top:1px solid var(--border);width:100%">' + a('wake', 'wake', t('a.wake')) + a('reboot', 'reboot', t('a.reboot')) + a('logoff', 'logoff', t('a.logoff')) + a('shutdown', 'power', t('a.shutdown'), 'danger') + a('reset', 'reset-user', t('a.reset'), 'danger') + (admin() ? a('remove', 'trash', t('a.remove'), 'danger') : '');
}

/* ---------------------------------------------------------------- menus */
function menuView() {
  const m = S.menu; const it = (act, icon, label, opts = {}) => `<button data-act="${act}" ${opts.arg !== undefined ? `data-arg="${opts.arg}"` : ''} class="${opts.cls || ''}" ${opts.dis ? 'disabled' : ''}>${ic(icon, 's16')}<span>${label}</span>${opts.kbd ? `<span class="kbd">${opts.kbd}</span>` : ''}</button>`;
  let h = '';
  if (m.kind === 'tile') {
    const p = S.pcs.find(x => x.n === m.n);
    h = `<div class="mh">${pcName(p.n)}</div>${it('view', 'view', t('a.view'), { kbd: '↵' })}${it('viewctl', 'control', t('a.control'))}<hr>${it('lock', 'lock', t('a.lock'), { kbd: '⌘L' })}${it('unlock', 'unlock', t('a.unlock'))}${it('cast', 'cast', t('a.casthere'))}${it('send', 'send-files', t('a.send'))}${it('collect', 'collect', t('a.collect'))}<hr>${it('wake', 'wake', t('a.wake'))}${it('reboot', 'reboot', t('a.reboot'))}${it('logoff', 'logoff', t('a.logoff'))}${it('shutdown', 'power', t('a.shutdown'), { cls: 'danger' })}<hr>${it('reset', 'reset-user', t('a.reset'), { cls: 'danger' })}${admin() ? it('push', 'update', t('a.push')) + it('remove', 'trash', t('a.remove'), { cls: 'danger' }) + it('revoke', 'ban', t('a.revoke'), { cls: 'danger' }) : ''}`;
  } else if (m.kind === 'lab') {
    h = `<div class="mh">${t('lab.name')} · ${admin() ? t('role.admin') : t('role.teacher')}</div>${it('gochooser', 'labs', t('m.mylabs'))}${it('leave', 'leave', t('m.leave'))}`;
  } else if (m.kind === 'key') {
    const open = S.keyOpenUntil > now();
    h = `<div class="mh">${t('key.title')}</div>${open ? it('keylock', 'lock', t('a.keylock')) : it('keyunlock', 'key', t('a.keyunlock'))}${S.keyRemembered ? it('keyforget', 'forget', t('a.keyforget')) : it('keyremember', 'remember', t('a.keyremember'))}<hr>${it('gokey', 'admin', t('m.keypage'))}`;
  } else if (m.kind === 'cast') {
    h = `<div class="mh">${t('m.castto', tgtLabel())}</div>${it('caststart', 'text-mode', t('vm.text') + ' — ' + t('vm.text.d'), { arg: 'text' })}${it('caststart', 'video-mode', t('vm.video') + ' — ' + t('vm.video.d'), { arg: 'video' })}`;
  } else if (m.kind === 'power') {
    h = `<div class="mh">${tgtLabel()}</div>${it('wake', 'wake', t('a.wake'))}${it('reboot', 'reboot', t('a.reboot'))}${it('logoff', 'logoff', t('a.logoff'))}<hr>${it('shutdown', 'power', t('a.shutdown'), { cls: 'danger' })}`;
  } else if (m.kind === 'more') {
    h = (S._ovf || []).map(([a, g, l, d]) => it(a, ICON_OF[a] || 'more', l, { arg: g, dis: d })).join('') + `<div class="mh">${t('rt.lesson')}</div>${it('collect', 'collect', t('a.collect'))}${it('scriptmenu2', 'play', t('a.runscript'))}${it('netopen', 'globe', t('a.netopen'))}${examPcs().length ? it('extend', 'timer-plus', t('a.extend')) + it('endexam', 'flag', t('a.endexam')) : ''}<div class="mh">${t('rt.pcs')}</div>${admin() ? it('addpcs', 'monitor-plus', t('a.addpcs')) : ''}${it('install', 'install', t('a.install'))}${it('reset', 'reset-user', t('a.reset'), { cls: 'danger' })}${admin() ? it('push', 'update', t('a.push')) + it('remove', 'trash', t('a.remove'), { cls: 'danger', dis: !S.sel.size }) : ''}`;
  } else if (m.kind === 'scripts') {
    h = `<div class="mh">${t('m.runon', tgtLabel())}</div>${SCRIPTS.slice(0, 6).map((s, i) => it('runscript', 'play', s[LI[S.lang]] || s[0], { arg: i })).join('')}<hr>${it('screen', 'scripts', t('m.allscripts'), { arg: 'scripts' })}`;
  } else if (m.kind === 'quality') {
    h = ['auto', 'high', 'medium', 'low'].map(q => it('quality', S.quality === q ? 'check' : 'quality', t('q.' + q), { arg: q })).join('');
  } else if (m.kind === 'labcard') {
    h = `${it('noop', 'certificate', t('ch.renewcert'))}${it('noop', 'trash', t('ch.remove'), { cls: 'danger' })}`;
  } else if (m.kind === 'ovf') {
    h = (S._ovf || []).map(([a, g, l, d]) => it(a, ICON_OF[a] || 'more', l, { arg: g, dis: d })).join('');
  } else if (m.kind === 'modes') {
    const ex = examPcs(), lk = lockedPcs(), nt = S.pcs.filter(p => p.net && !p.exam);
    h = `${ex.length ? it('extend', 'timer-plus', t('mb.exam', ex.length) + ' — ' + t('a.extend')) + it('endexam', 'flag', t('a.endexam')) : ''}${S.cast ? it('stopcast', 'cast-off', t('mb.cast', S.cast.targets.length) + ' — ' + t('a.stopcast')) : ''}${lk.length ? it('unlockall', 'unlock', t('mb.lock', lk.length) + ' — ' + t('a.unlockall')) : ''}${nt.length ? it('netopenall', 'globe', t('mb.net.s', nt.length) + ' — ' + t('a.netopenall')) : ''}`;
  } else if (m.kind === 'adminpc') {
    h = `${it('push', 'update', t('a.push'))}${it('remove', 'trash', t('a.remove'), { cls: 'danger' })}${it('revoke', 'ban', t('a.revoke'), { cls: 'danger' })}`;
  }
  return `<div class="menu" style="left:${m.x}px;top:${m.y}px">${h}</div>`;
}
function openMenu(kind, el, extra = {}) {
  const w = $('#win').getBoundingClientRect(), r = el.getBoundingClientRect(), k = S._scale || 1;
  S.menu = { kind, x: (r.left - w.left) / k, y: (r.bottom - w.top) / k + 4, ...extra };
  render();
  const m = $('.menu'); if (m) { const ww = $('#win').offsetWidth, wh = $('#win').offsetHeight; if (S.menu.x + m.offsetWidth > ww - 8) m.style.left = (ww - m.offsetWidth - 8) + 'px'; if (S.menu.y + m.offsetHeight > wh - 8) m.style.top = Math.max(8, wh - m.offsetHeight - 8) + 'px'; }
}

/* ---------------------------------------------------------------- toasts (feedback) */
function toastsView() {
  if (!S.toasts.length || S.window !== 'console') return '';
  return `<div class="toasts">${S.toasts.map(x => `<div class="toast ${x.kind}"><span class="ti">${x.kind === 'prog' ? ic('loader', 'spin') : x.kind === 'ok' ? ic('approve') : x.kind === 'bad' ? ic('x-circle') : ic('alert')}</span><div class="tb"><div class="tt">${x.title}</div>${x.sub ? `<div class="ts">${x.sub}</div>` : ''}${x.bar ? `<div class="bar">${x.bar.map(([c, v]) => `<i class="${c}" style="width:${v}%"></i>`).join('')}</div>` : ''}${x.list ? `<ul class="tl">${x.list.map(([n, r]) => `<li><b>${n}</b><span class="muted">${r}</span></li>`).join('')}</ul>` : ''}${x.acts ? `<div class="ta">${x.acts}</div>` : ''}</div><button class="ib" style="width:24px;height:24px" data-act="toastclose" data-arg="${x.id}">${ic('close', 's14')}</button></div>`).join('')}</div>`;
}
function toast(o) { const x = { id: toastId++, ...o }; S.toasts.unshift(x); S.toasts = S.toasts.slice(0, 4); if (o.kind === 'ok') setTimeout(() => { S.toasts = S.toasts.filter(y => y.id !== x.id); if (S.window === 'console') rerenderKeepScroll(); }, 6000); return x.id; }
function dropToast(id) { S.toasts = S.toasts.filter(y => y.id !== id); }
/** Runs a simulated action: a progress toast, then each PC answers; the result toast states who did not and why. */
function runAction(name, pcs, apply, reasonFor) {
  const id = toast({ kind: 'prog', title: t('fb.prog', name), sub: t('fb.asked', pcs.length), bar: [['', 8]] });
  rerenderKeepScroll();
  let k = 0; const step = () => {
    k++; const tst = S.toasts.find(y => y.id === id); if (!tst) return;
    tst.bar = [['', Math.min(100, k * 30)]]; tst.sub = t('fb.answered', Math.min(pcs.length, Math.round(pcs.length * k * .3)), pcs.length);
    if (k < 3) { rerenderKeepScroll(); setTimeout(step, 450); return; }
    dropToast(id);
    const fails = []; let ok = 0;
    pcs.forEach(p => { const r = reasonFor(p); if (r) fails.push([pcName(p.n), r]); else { apply(p); ok++; } });
    const batch = { id: S.batches.length + 1, title: name, time: new Date().toTimeString().slice(0, 5), ok, fails, total: pcs.length };
    S.batches.unshift(batch);
    if (!fails.length) toast({ kind: 'ok', title: t('fb.done', name), sub: t('fb.allok', ok) });
    else toast({ kind: fails.length === pcs.length ? 'bad' : 'part', title: t('fb.partial', name, ok, pcs.length), list: fails.slice(0, 4).concat(fails.length > 4 ? [['…', t('fb.more', fails.length - 4)]] : []), acts: `<button class="btn" data-act="details" data-arg="${batch.id}">${ic('external', 's16')}${t('fb.details')}</button>${fails.some(f => f[1] === t('r.off')) ? `<button class="btn" data-act="wakefailed">${ic('wake', 's16')}${t('fb.wakethem')}</button>` : ''}` });
    rerenderKeepScroll();
  };
  setTimeout(step, 450);
}
const offReason = p => p.status === 'off' ? t('r.off') : p.status === 'waking' ? t('r.waking') : p.status === 'elsewhere' ? t('r.elsewhere') : null;

/* ---------------------------------------------------------------- dialogs */
function dlg(o) {
  return `<div class="scrim" data-act="${o.noclose ? '' : 'scrim'}"><div class="dlg ${o.cls || ''}" role="dialog" aria-modal="true" aria-label="${esc(o.title)}"><div class="dh"><span class="hi ${o.tone || ''}">${ic(o.icon)}</span><div style="flex:1;min-width:0"><h3>${o.title}</h3>${o.sub ? `<div class="sub">${o.sub}</div>` : ''}</div>${o.noclose ? '' : `<button class="ib" data-act="close">${ic('close', 's16')}</button>`}</div><div class="db">${o.body}</div>${o.foot ? `<div class="df">${o.foot}</div>` : ''}</div></div>`;
}
const radio = (grp, val, cur, title, sub) => `<div class="radio ${cur === val ? 'on' : ''}" data-act="radio" data-grp="${grp}" data-arg="${val}"><span class="rd"></span><div><div class="rt">${title}</div>${sub ? `<div class="rs">${sub}</div>` : ''}</div></div>`;
const cancelBtn = () => `<span class="sp"></span><button class="btn" data-act="close">${t('c.cancel')}</button>`;
function targetSub() { const tg = targets(); const off = tg.filter(p => !isOn(p)).length; return t('tgt.sub', tgtLabel()) + (off ? ' · ' + t('tgt.offnote', off) : ''); }
function modalView() {
  const M = S.modal; const f = M.f || (M.f = {});
  switch (M.kind) {
    case 'lock': f.mode ??= 'until'; return dlg({ icon: 'lock', tone: 'lock', title: t('d.lock.t'), sub: targetSub(), body: `<div class="field"><label>${t('d.lock.msg')}</label><input class="tx" value="${esc(t('d.lock.msgv'))}"></div><div class="field"><span class="flbl">${t('d.lock.how')}</span>${radio('mode', 'until', f.mode, t('d.lock.until'), t('d.lock.until.d'))}${radio('mode', 'min', f.mode, t('d.lock.min'), t('d.lock.min.d'))}</div><div class="note">${ic('info', 's16')}<span>${t('d.lock.ceil')}</span></div><div class="note">${ic('keyboard', 's16')}<span>${t('d.lock.sas')}</span></div>`, foot: `<span class="dim small">${t('d.offskip')}</span>${cancelBtn()}<button class="btn primary" data-act="dolock">${ic('lock', 's16')}${t('d.lock.go', targets().length)}</button>` });
    case 'cast': f.mode ??= S.castMode; return dlg({ icon: 'cast', tone: 'cast', title: t('d.cast.t'), sub: targetSub(), body: `<div class="field"><span class="flbl">${t('d.cast.pic')}</span>${radio('mode', 'text', f.mode, ic('text-mode', 's16') + ' ' + t('vm.text'), t('d.cast.text.d'))}${radio('mode', 'video', f.mode, ic('video-mode', 's16') + ' ' + t('vm.video'), t('d.cast.video.d'))}</div><div class="note">${ic('info', 's16')}<span>${t('d.cast.note')}</span></div>`, foot: `${cancelBtn()}<button class="btn primary" data-act="docast">${ic('cast', 's16')}${t('d.cast.go', targets().length)}</button>` });
    case 'internet': f.mode ??= 'list'; return dlg({ icon: 'globe', tone: 'net', title: t('d.net.t'), sub: targetSub(), cls: 'wide', body: `<div style="display:grid;grid-template-columns:1fr 1fr;gap:14px"><div class="field"><span class="flbl">${t('d.net.mode')}</span>${radio('mode', 'open', f.mode, ic('globe', 's16') + ' ' + t('d.net.open'), t('d.net.open.d'))}${radio('mode', 'list', f.mode, ic('list-check', 's16') + ' ' + t('d.net.list'), t('d.net.list.d'))}${radio('mode', 'none', f.mode, ic('globe-off', 's16') + ' ' + t('d.net.none'), t('d.net.none.d'))}<span class="flbl" style="margin-top:6px">${t('d.net.dur')}</span><div class="row"><span class="chip on">${t('d.net.lesson', 90)}</span><span class="chip">${t('d.net.until')}</span></div><div class="note">${ic('info', 's16')}<span>${t('d.net.ceil')}</span></div></div><div class="field" style="${f.mode === 'list' ? '' : 'opacity:.45;pointer-events:none'}"><div class="row"><span class="flbl">${t('d.net.preset')}</span><select class="tx" style="flex:1"><option>Java docs</option><option>${t('d.net.unsaved')}</option></select><button class="ib" data-tip="${esc(t('d.net.save'))}">${ic('save', 's16')}</button><button class="ib" data-tip="${esc(t('d.net.del'))}">${ic('trash', 's16')}</button></div><textarea class="tx" rows="5">*.oracle.com\ndocs.oracle.com\n*.jetbrains.com\nstackoverflow.com</textarea><span class="dim small">${t('d.net.hostsonly')}</span><span class="flbl">${t('d.net.refused')}</span><div class="list">${[['fonts.gstatic.com', 6], ['cdn.jsdelivr.net', 3]].map(([h, n]) => `<div class="li"><span style="flex:1">${h}</span><span class="dim">${t('d.net.pcs', n)}</span><button class="btn">${ic('plus', 's16')}${t('d.net.allow')}</button></div>`).join('')}</div></div></div>${examPcs().length ? `<div class="note warn">${ic('exam', 's16')}<span>${t('d.net.examwins', examPcs().map(p => pcName(p.n)).join(', '))}</span></div>` : ''}`, foot: `<span class="dim small">${t('d.offskip')}</span>${cancelBtn()}<button class="btn primary" data-act="donet">${t('d.net.apply')}</button>` });
    case 'exam': {
      f.timer ??= true; f.progs ??= true; f.net ??= true; f.collect ??= true;
      const sw = (k, icon, title, inner) => `<div class="swc ${f[k] ? 'on' : ''}"><div class="swh">${ic(icon, 's16')}${title}<button class="toggle ${f[k] ? 'on' : ''}" data-act="sw" data-arg="${k}" aria-pressed="${f[k]}" aria-label="${esc(title)}"></button></div><div style="${f[k] ? '' : 'opacity:.45;pointer-events:none'};display:flex;flex-direction:column;gap:6px;font-size:12.5px">${inner}</div></div>`;
      return dlg({ icon: 'exam', tone: 'exam', title: t('d.exam.t'), sub: S.sel.size ? targetSub() : t('d.exam.all', S.pcs.length), cls: 'wide', body: `<div class="row"><span class="flbl">${t('d.exam.preset')}</span><select class="tx"><option>${t('d.exam.presetv')}</option><option>${t('d.exam.unsaved')}</option></select><button class="btn ghost">${ic('save', 's16')}${t('d.exam.savepreset')}</button></div><div class="field"><label>${t('d.exam.msg')}</label><input class="tx" value="${esc(t('d.exam.msgv'))}"></div><div class="sw4">${sw('timer', 'timer', t('d.exam.sw.timer'), `<div class="row"><input class="tx" style="width:80px" value="45"> ${t('u.min')}</div><span class="muted">${t('d.exam.timeup')}</span>`)}${sw('progs', 'apps', t('d.exam.sw.progs'), `<textarea class="tx" rows="3">idea64.exe\njava.exe\nmsedge.exe</textarea><span class="muted">${t('d.exam.sweep')}</span>`)}${sw('net', 'globe', t('d.exam.sw.net'), `<div class="row"><select class="tx"><option>${t('d.exam.net.list')}: Java docs</option><option>${t('d.net.none')}</option></select></div><span class="muted">${t('d.exam.net.d')}</span>`)}${sw('collect', 'collect', t('d.exam.sw.collect'), `<div class="row"><span class="muted">${t('d.exam.folder')}</span><input class="tx" style="flex:1" value="Desktop\\Exam"></div><span class="muted">${t('d.exam.collect.d')}</span>`)}</div><div class="note">${ic('info', 's16')}<span>${t('d.exam.ceil')}</span></div>`, foot: `<span class="dim small">${t('d.exam.late')}</span>${cancelBtn()}<button class="btn primary" data-act="doexam" style="background:var(--exam);border-color:var(--exam)">${ic('exam', 's16')}${t('d.exam.go')}</button>` });
    }
    case 'extend': return dlg({ icon: 'timer-plus', tone: 'exam', title: t('d.ext.t'), sub: t('d.ext.sub', examPcs().length), body: `<div class="row">${[5, 10, 15, 30].map(m => `<span class="chip ${m === 10 ? 'on' : ''}">+${m} ${t('u.min')}</span>`).join('')}</div><div class="note">${ic('info', 's16')}<span>${t('d.ext.note')}</span></div>`, foot: `${cancelBtn()}<button class="btn primary" data-act="doextend">${t('d.ext.go')}</button>` });
    case 'endexam': return dlg({ icon: 'flag', tone: 'exam', title: t('d.end.t'), sub: t('d.ext.sub', examPcs().length), body: `<p style="margin:0">${t('d.end.p')}</p>${S.exam?.collect ? `<div class="note ok">${ic('collect', 's16')}<span>${t('d.end.collect')}</span></div>` : ''}`, foot: `${cancelBtn()}<button class="btn danger" data-act="doendexam">${ic('flag', 's16')}${t('a.endexam')}</button>` });
    case 'send': return dlg({ icon: 'send-files', title: t('d.send.t'), sub: targetSub(), body: `<div class="drop" style="padding:18px">${ic('upload', 's24')}<div>${t('d.send.drop')}</div><button class="btn" style="margin-top:8px">${t('d.send.choose')}</button></div><div class="list"><div class="li">${ic('send-files', 's16')}<span style="flex:1">Лабораторна_5_масиви.pdf</span><span class="dim">1,2 МБ</span><button class="ib">${ic('close', 's14')}</button></div><div class="li">${ic('send-files', 's16')}<span style="flex:1">Arrays.java</span><span class="dim">4 КБ</span><button class="ib">${ic('close', 's14')}</button></div></div><label class="chk" style="padding:0">${'<span class="box on">' + ic('check', 's14') + '</span>'}${t('d.send.open')}</label><div class="note">${ic('folder', 's16')}<span>${t('d.send.where')}</span></div>`, foot: `${cancelBtn()}<button class="btn primary" data-act="dosend">${ic('send-files', 's16')}${t('d.send.go', targets().length)}</button>` });
    case 'collect': return dlg({ icon: 'collect', title: t('d.col.t'), sub: targetSub(), body: `<div class="field"><label>${t('d.col.name')}</label><input class="tx" value="${esc(t('d.col.namev'))}"></div><div class="field"><label>${t('d.col.folder')}</label><input class="tx" value="Desktop\\Exam"><span class="dim small">${t('d.col.folder.d')}</span></div><div class="field"><label>${t('d.col.dest')}</label><div class="row"><input class="tx" style="flex:1" value="~/Documents/LabControl/Collected"><button class="btn">${ic('folder', 's16')}${t('c.browse')}</button></div></div><div class="note">${ic('info', 's16')}<span>${t('d.col.caps')}</span></div>`, foot: `<span class="dim small">${t('d.col.off')}</span>${cancelBtn()}<button class="btn primary" data-act="docollect">${ic('collect', 's16')}${t('d.col.go', targets().length)}</button>` });
    case 'reset': f.mode ??= 'light'; return dlg({ icon: 'reset-user', tone: 'bad', title: t('d.reset.t'), sub: targetSub(), body: `${radio('mode', 'light', f.mode, t('d.reset.light'), t('d.reset.light.d'))}${radio('mode', 'full', f.mode, t('d.reset.full'), t('d.reset.full.d'))}<div class="note warn">${ic('alert', 's16')}<span>${t('d.reset.warn')}</span></div>`, foot: `${cancelBtn()}<button class="btn danger" data-act="doreset">${f.mode === 'light' ? t('d.reset.golight') : t('d.reset.gofull')}</button>` });
    case 'confirm': return dlg({ icon: M.icon, tone: 'bad', title: M.title, sub: M.sub, body: `<p style="margin:0">${M.text}</p>`, foot: `${cancelBtn()}<button class="btn danger" data-act="${M.go}">${M.goLabel}</button>` });
    case 'depart': return departView();
    case 'unlockkey': f.rem ??= true; return dlg({ icon: 'key', title: t('d.unlock.t'), sub: M.reason || t('d.unlock.reason'), body: `<div class="field"><label>${t('d.unlock.pass')}</label><input class="tx" type="password" value="••••••••••••" autofocus></div><label class="chk" style="padding:0" data-act="sw" data-arg="rem"><span class="box ${f.rem ? 'on' : ''}">${f.rem ? ic('check', 's14') : ''}</span>${t('d.unlock.rem')}</label><button class="btn ghost" style="align-self:flex-start">${ic('lifebuoy', 's16')}${t('d.unlock.rc')}</button>`, foot: `${cancelBtn()}<button class="btn primary" data-act="dounlock">${ic('key', 's16')}${t('a.keyunlock.s')}</button>` });
    case 'legend': return dlg({ icon: 'legend', title: t('lg.t'), sub: t('lg.sub'), cls: 'wide', body: legendBody(), foot: `<span class="sp"></span><button class="btn primary" data-act="close">${t('c.ok')}</button>` });
    case 'addpcs': return addPcsWizard();
    case 'addprog': return addProgWizard();
    case 'push': return dlg({ icon: 'update', title: t('d.push.t'), sub: targetSub(), body: `<div class="list"><div class="li">${ic('check', 's16')}<span style="flex:1">${t('d.push.found')}</span><b>1.0.0+fc7601b</b></div></div><div class="note">${ic('info', 's16')}<span>${t('d.push.note')}</span></div>`, foot: `${cancelBtn()}<button class="btn primary" data-act="dopush">${ic('update', 's16')}${t('d.push.go', targets().length)}</button>` });
    case 'movekey': return dlg({ icon: 'key-move', tone: 'bad', title: t('d.move.t'), sub: t('d.move.sub'), body: `<p style="margin:0" class="muted">${t('d.move.p')}</p><div class="field"><label>${t('d.move.name')}</label><input class="tx" value="В. Стогул"></div><div class="row"><div class="field" style="flex:1"><label>${t('d.move.pass')}</label><input class="tx" type="password" value="••••••••••"></div><div class="field" style="flex:1"><label>${t('d.move.pass2')}</label><input class="tx" type="password" value="••••••••••"></div></div>`, foot: `${cancelBtn()}<button class="btn danger" data-act="close">${ic('key-move', 's16')}${t('d.move.go')}</button>` });
  }
  return '';
}
function legendBody() {
  const row = (sw, txt) => `<span class="sw">${sw}</span><span>${txt}</span>`;
  return `<div class="legend">${row('<span class="dotc on"></span>', t('lg.on'))}${row(`<span class="bdg off">${ic('power')}</span>`, t('lg.off'))}${row(`<span class="dotc wake"></span>`, t('lg.wake'))}${row(`<span class="box on" style="border-radius:50%">${ic('check', 's14')}</span>`, t('lg.sel'))}${row(`<span class="bdg lock">${ic('lock')}<span>${t('b.locked')}</span></span>`, t('lg.lock'))}${row(`<span class="bdg cast">${ic('cast')}<span>${t('b.cast')}</span></span>`, t('lg.cast'))}${row(`<span class="bdg exam">${ic('timer')}<span>23:41</span></span>`, t('lg.exam'))}${row(`<span class="bdg net">${ic('globe')}<span>${t('b.list')}</span></span><span class="bdg net">${ic('globe-off')}</span>`, t('lg.net'))}${row(`<span class="bdg bad">${ic('alert')}<span>${t('b.problem')}</span></span>`, t('lg.bad'))}${row(`<span class="bdg warn">${ic('update')}</span>`, t('lg.outdated'))}${row(`<span class="bdg warn">${ic('clock')}</span>`, t('lg.renew'))}${row(ic('image-off'), t('lg.nopic'))}${row(ic('elsewhere'), t('lg.elsewhere'))}</div><div class="note">${ic('info', 's16')}<span>${t('lg.rule')}</span></div><div class="note">${ic('select-all', 's16')}<span>${t('lg.select')}</span></div>`;
}
function departView() {
  const ex = examPcs().length, lk = lockedPcs().length, nt = S.pcs.filter(p => p.net && !p.exam).length;
  const items = [];
  if (S.cast) items.push(['cast', 'cast', t('dep.cast', S.cast.targets.length)]);
  if (lk) items.push(['lock', 'lock', t('dep.lock', lk, '11:50')]);
  if (nt) items.push(['net', 'globe', t('dep.net', nt, '13:10')]);
  if (ex) items.push(['exam', 'exam', t('dep.exam', ex, '11:32')]);
  if (S.exam?.collect || ex) items.push(['warn', 'collect', t('dep.work', ex)]);
  items.push(['off', 'activity', t('dep.jobs')]);
  return dlg({ icon: 'leave', title: t('dep.t', t('lab.name')), sub: t('dep.sub'), cls: 'wide', body: items.map(([c, i, s]) => `<div class="stline ${c}">${ic(i, 's16')}<span>${s}</span></div>`).join(''), foot: `<button class="btn" data-act="close">${t('dep.stay')}</button><span class="sp"></span><button class="btn" data-act="dowait">${t('dep.wait', 1)}</button>${(lk || nt) ? `<button class="btn primary" data-act="doleave">${ic('unlock', 's16')}${t('dep.endpol')}</button>` : ''}<button class="btn ${(lk || nt) ? '' : 'primary'}" data-act="doleave">${t('dep.leave')}</button>` });
}

/* ---------------------------------------------------------------- Add computers wizard */
function addPcsWizard() {
  const f = S.modal.f; f.step ??= 0; f.count ??= Math.max(14 - S.pcs.length, 1); f.drive ??= 0; f.written ??= false; f.arrived ??= {};
  const first = S.pcs.length + 1, last = first + (+f.count) - 1;
  const steps = ['w.s1', 'w.s2', 'w.s3', 'w.s4', 'w.s5'];
  const side = `<div class="wsteps">${steps.map((k, i) => `<div class="ws ${i === f.step ? 'on' : i < f.step ? 'done' : ''}"><span class="num">${i < f.step ? '✓' : i + 1}</span>${t(k)}</div>`).join('')}<div style="margin-top:auto" class="note">${ic('info', 's16')}<span>${t('w.side')}</span></div></div>`;
  let main = '', next = '';
  if (f.step === 0) {
    main = `<h2>${t('w.s1.h')}</h2><p class="muted" style="margin:0">${t('w.s1.p')}</p><div class="row"><span class="flbl" style="min-width:200px">${t('w.s1.count')}</span><button class="ib" data-act="wcount" data-arg="-1">${ic('minus')}</button><input class="tx" style="width:70px;text-align:center;font-size:18px;font-weight:700" value="${f.count}" readonly><button class="ib" data-act="wcount" data-arg="1">${ic('plus')}</button></div><div class="note ok">${ic('monitor', 's16')}<span>${t('w.s1.nums', pcName(first), pcName(last), +f.count + 2)}</span></div>${S.pcs.length ? `<label class="chk" style="padding:0;align-items:flex-start;white-space:normal"><span class="box on">${ic('check', 's14')}</span><span><b>${t('w.s1.replace')}</b><br><span class="muted small">${t('w.s1.replace.d', 3)}</span></span></label>` : ''}`;
    next = `<button class="btn primary" data-act="wnext">${t('c.next')}${ic('chevron-right', 's16')}</button>`;
  } else if (f.step === 1) {
    main = `<h2>${t('w.s2.h')}</h2><p class="muted" style="margin:0">${t('w.s2.p')}</p>${[['KINGSTON', '16 ГБ', '/Volumes/KINGSTON'], ['SANDISK', '32 ГБ', '/Volumes/SANDISK']].map(([n, s, pth], i) => `<div class="drive ${f.drive === i ? 'on' : ''}" data-act="wdrive" data-arg="${i}">${ic('usb', 's24')}<div style="flex:1"><b>${n}</b> <span class="dim">· ${s} · ${pth}</span></div>${f.drive === i ? ic('check') : ''}</div>`).join('')}<div class="dim small">${t('w.s2.nodrive')}</div><div class="list"><div class="li">${ic('check', 's16')}<span style="flex:1">${t('w.s2.files')}</span><span class="dim">1.0.0+fc7601b</span><button class="btn ghost">${t('a.change')}</button></div></div>${f.writing ? `<div class="note">${ic('loader', 's16 spin')}<span>${t('w.s2.writing')}</span></div>` : f.written ? `<div class="note ok">${ic('approve', 's16')}<span>${t('w.s2.done', 'KINGSTON', +f.count + 2)}</span></div>` : ''}<div class="note">${ic('key', 's16')}<span>${t('w.s2.nosecret')}</span></div><details class="acc"><summary>${t('w.s2.adv')}</summary><div class="muted small" style="margin-top:6px">${t('w.s2.adv.d')}</div><button class="btn" style="margin-top:6px">${ic('folder', 's16')}${t('w.s2.payload')}</button></details>`;
    next = f.written ? `<button class="btn primary" data-act="wnext">${t('c.next')}${ic('chevron-right', 's16')}</button>` : `<button class="btn primary" data-act="wwrite" ${f.writing ? 'disabled' : ''}>${ic('usb', 's16')}${t('w.s2.go')}</button>`;
  } else if (f.step === 2) {
    const ill = [['usb', 'w.s3.1'], ['admin', 'w.s3.2'], ['monitor', 'w.s3.3'], ['reboot', 'w.s3.4']];
    main = `<h2>${t('w.s3.h')}</h2><p class="muted" style="margin:0">${t('w.s3.p')}</p><div class="illu">${ill.map(([i, k], n) => `<div><div class="pic">${ic(i, 's32')}</div><b><span class="num" style="width:18px;height:18px;font-size:11px">${n + 1}</span> ${t(k)}</b><span class="muted">${t(k + '.d')}</span></div>`).join('')}</div><div class="note">${ic('info', 's16')}<span>${t('w.s3.note')}</span></div><div class="row"><button class="btn">${ic('printer', 's16')}${t('w.s3.print')}</button><span class="dim small">${t('w.s3.print.d')}</span></div>`;
    next = `<button class="btn primary" data-act="wnext">${t('w.s3.go')}${ic('chevron-right', 's16')}</button>`;
  } else if (f.step === 3) {
    const open = S.keyOpenUntil > now() || S.keyRemembered;
    const nums = []; for (let i = first; i <= last; i++) nums.push(i);
    const done = nums.filter(n => f.arrived[n] === 'ok').length;
    main = `<h2>${t('w.s4.h')}</h2>${open ? `<div class="note ok">${ic('key', 's16')}<span>${t('w.s4.keyopen')}</span></div>` : `<div class="note warn" style="align-items:center">${ic('key', 's16')}<span style="flex:1">${t('w.s4.keylocked')}</span><button class="btn primary" data-act="wunlock">${ic('key', 's16')}${t('a.keyunlock.s')}</button></div>`}<div class="row"><b style="font-size:16px">${t('w.s4.count', done, nums.length)}</b><span class="sp" style="flex:1"></span><span class="tag">${ic('clock', 's14')} ${t('w.s4.wait')}</span><span class="tag acc">${ic('loader', 's14')} ${t('w.s4.conn')}</span><span class="tag ok">${ic('check', 's14')} ${t('w.s4.ok')}</span><span class="tag bad">${ic('alert', 's14')} ${t('w.s4.bad')}</span></div><div class="bar">${`<i class="ok" style="width:${done / nums.length * 100}%"></i>`}</div><div class="pcgrid">${nums.map(n => { const s = f.arrived[n]; return `<div class="pcc ${s === 'ok' ? 'ok' : s === 'go' ? 'go' : s === 'bad' ? 'bad' : 'wait'}">${ic(s === 'ok' ? 'check' : s === 'go' ? 'loader' : s === 'bad' ? 'alert' : 'clock', 's16' + (s === 'go' ? ' spin' : ''))}<b>${pcName(n)}</b></div>`; }).join('')}</div>${Object.values(f.arrived).includes('bad') ? `<div class="note bad">${ic('alert', 's16')}<span>${t('w.s4.badnote', pcName(first + 4))}</span></div>` : ''}<details class="acc" ${open ? '' : 'open'}><summary>${t('w.s4.trouble')}</summary><ol><li>${t('w.s4.t1')}</li><li>${t('w.s4.t2')} <span class="tag ok">${ic('check', 's14')} ${t('w.s4.t2ok')}</span></li><li>${t('w.s4.t3')}</li><li>${t('w.s4.t4')}</li><li>${t('w.s4.t5')}</li></ol></details>`;
    next = `<button class="btn" data-act="close">${t('w.s4.later')}</button><button class="btn primary" data-act="wnext" ${done ? '' : 'disabled'}>${t('c.next')}${ic('chevron-right', 's16')}</button>`;
  } else {
    const done = Object.values(f.arrived).filter(v => v === 'ok').length;
    main = `<div style="text-align:center;padding:20px 0 6px"><div style="color:var(--ok)">${ic('approve', 's48')}</div><h2 style="margin-top:8px">${t('w.s5.h', S.pcs.length + done, S.pcs.length + +f.count)}</h2><p class="muted">${t('w.s5.p')}</p></div><div class="cards2"><div class="card"><h3>${ic('room')}${t('w.s5.plan')}</h3><span class="muted small">${t('w.s5.plan.d')}</span><button class="btn" data-act="wplan" style="align-self:flex-start">${ic('grip', 's16')}${t('g.arrange')}</button></div><div class="card"><h3>${ic('backup')}${t('w.s5.backup')}</h3><span class="muted small">${t('w.s5.backup.d')}</span><button class="btn primary" data-act="backup" style="align-self:flex-start">${ic('download', 's16')}${t('a.backup')}</button></div></div>`;
    next = `<button class="btn primary" data-act="wfinish">${t('c.done')}</button>`;
  }
  return `<div class="scrim"><div class="dlg xwide" style="height:640px"><div class="dh"><span class="hi">${ic('monitor-plus')}</span><div style="flex:1"><h3>${t('a.addpcs')}</h3><div class="sub">${t('lab.name')}</div></div><button class="ib" data-act="close">${ic('close', 's16')}</button></div><div class="wiz" style="border-top:1px solid var(--border)">${side}<div class="wmain">${main}</div></div><div class="df">${f.step > 0 && f.step < 4 ? `<button class="btn" data-act="wback">${ic('chevron-left', 's16')}${t('c.back')}</button>` : ''}<span class="sp"></span>${next}</div></div></div>`;
}
function simulateArrivals() {
  const f = S.modal?.f; if (!f || S.modal.kind !== 'addpcs') return;
  const first = S.pcs.length + 1, last = first + (+f.count) - 1; let i = first;
  const tick = () => {
    if (!S.modal || S.modal.kind !== 'addpcs' || i > last) return;
    const n = i;
    f.arrived[n] = 'go'; if (i > first) { const p = i - 1; if (f.arrived[p] === 'go') f.arrived[p] = (p === first + 4 && f.count > 6) ? 'bad' : 'ok'; }
    i++; if (i > last) setTimeout(() => { if (f.arrived[last] === 'go') f.arrived[last] = 'ok'; render(); }, 500);
    render(); setTimeout(tick, 380);
  };
  tick();
}

/* ---------------------------------------------------------------- Add program wizard */
function addProgWizard() {
  const f = S.modal.f; f.step ??= 0; f.rule ??= 'name';
  const steps = ['ap.s1', 'ap.s2', 'ap.s3', 'ap.s4'];
  const side = `<div class="wsteps">${steps.map((k, i) => `<div class="ws ${i === f.step ? 'on' : i < f.step ? 'done' : ''}"><span class="num">${i < f.step ? '✓' : i + 1}</span>${t(k)}</div>`).join('')}</div>`;
  let main = '', next = `<button class="btn primary" data-act="apnext">${t('c.next')}${ic('chevron-right', 's16')}</button>`;
  if (f.step === 0) main = `<h2>${t('ap.s1.h')}</h2><p class="muted" style="margin:0">${t('ap.s1.p')}</p><div class="drop" style="padding:26px">${ic('upload', 's32')}<div>${t('ap.s1.drop')}</div><button class="btn" style="margin-top:8px">${t('d.send.choose')}</button></div><div class="list"><div class="li">${ic('programs', 's16')}<b style="flex:1">ideaIC-2026.2.exe</b><span class="dim">812 МБ</span></div></div>`;
  if (f.step === 1) main = `<h2>${t('ap.s2.h')}</h2><div class="note ok">${ic('approve', 's16')}<span>${t('ap.s2.found')}</span></div><dl class="kv" style="font-size:13.5px"><dt>${t('ap.name')}</dt><dd><input class="tx" value="IntelliJ IDEA Community Edition" style="width:100%"></dd><dt>${t('ap.ver')}</dt><dd>2026.2</dd><dt>${t('ap.family')}</dt><dd><span class="tag acc">NSIS</span></dd><dt>${t('ap.args')}</dt><dd><input class="tx" value="/S /CONFIG=silent.config" style="width:100%;font-family:ui-monospace,monospace"></dd><dt>${t('ap.limit')}</dt><dd><input class="tx" value="15" style="width:60px"> ${t('u.min')}</dd><dt>SHA-256</dt><dd class="dim small">9f2c…b41e</dd></dl>`;
  if (f.step === 2) main = `<h2>${t('ap.s3.h')}</h2><p class="muted" style="margin:0">${t('ap.s3.p')}</p>${radio('rule', 'name', f.rule, t('ap.rule.name'), 'IntelliJ IDEA Community Edition 2026.2')}${radio('rule', 'file', f.rule, t('ap.rule.file'), 'C:\\Program Files\\JetBrains\\IntelliJ IDEA Community Edition 2026.2\\bin\\idea64.exe')}`;
  if (f.step === 3) { main = `<h2>${t('ap.s4.h')}</h2><p class="muted" style="margin:0">${t('ap.s4.p')}</p><div class="row"><span class="flbl">${t('ap.s4.pc')}</span><select class="tx"><option>PC-01</option><option>PC-02</option></select><button class="btn">${ic('install', 's16')}${t('ap.s4.test')}</button></div><div class="note ok">${ic('approve', 's16')}<span>${t('ap.s4.ok')}</span></div>`; next = `<button class="btn" data-act="close">${t('ap.s4.skip')}</button><button class="btn primary" data-act="apdone">${ic('install', 's16')}${t('ap.s4.all')}</button>`; }
  return `<div class="scrim"><div class="dlg xwide" style="height:560px"><div class="dh"><span class="hi">${ic('programs')}</span><div style="flex:1"><h3>${t('pg.add')}</h3></div><button class="ib" data-act="close">${ic('close', 's16')}</button></div><div class="wiz" style="border-top:1px solid var(--border)">${side}<div class="wmain">${main}</div></div><div class="df">${f.step ? `<button class="btn" data-act="apback">${ic('chevron-left', 's16')}${t('c.back')}</button>` : ''}<span class="sp"></span>${next}</div></div></div>`;
}

/* ---------------------------------------------------------------- full view */
function fullView() {
  const p = S.pcs.find(x => x.n === S.full); if (!p) return '';
  return `<div class="full"><div class="fbar"><button class="btn" data-act="fullclose">${ic('chevron-left', 's16')}${t('fv.back')}</button><span class="ttl">${ic('monitor')}${pcName(p.n)} <span class="fstat">· ${p.user || '—'}</span></span><button class="btn ${S.control ? 'on' : ''}" data-act="ctl" aria-pressed="${S.control}">${ic('control', 's16')}${S.control ? t('fv.controlling') : t('a.control')}</button><button class="btn" data-act="noop" data-tip="${esc(t('fv.sas'))}">${ic('keyboard', 's16')}Ctrl+Alt+Del</button><button class="btn" data-act="noop" data-tip="${esc(t('fv.win'))}">${ic('win-key', 's16')}Win</button><span class="sp"></span><span class="seg"><button class="${S.fullMode === 'text' ? 'on' : ''}" data-act="fullmode" data-arg="text" data-tip="${esc(t('vm.text.d'))}">${ic('text-mode', 's16')}${t('vm.text')}</button><button class="${S.fullMode === 'video' ? 'on' : ''}" data-act="fullmode" data-arg="video" data-tip="${esc(t('vm.video.d'))}">${ic('video-mode', 's16')}${t('vm.video')}</button></span><button class="btn" data-act="qualmenu" ${S.fullMode === 'video' ? 'disabled' : ''}>${ic('quality', 's16')}${t('q.' + S.quality)}${ic('chevron-down', 's14')}</button><span class="fstat">1920×1080 · ${S.fullMode === 'video' ? '26' : '14'} ${t('u.fps')} · ${S.fullMode === 'video' ? '1 900' : '820'} ${t('u.kbit')}</span></div><div class="fpic ${S.control ? 'ctl' : ''}">${pcThumb(p).replace('slice', 'meet')}${S.control ? `<div class="chint">${ic('control', 's16')}${t('fv.hint', pcName(p.n))}</div>` : ''}</div></div>`;
}

/* ---------------------------------------------------------------- pages */
const PKGS = [['IntelliJ IDEA Community 2026.2', 'NSIS · 812 МБ', 'programs', [11, 1, 2]], ['Python 3.13.1', 'WiX bundle · 27 МБ', 'programs', [14, 0, 0]], ['Realtek RTL8168 driver', '.zip → setup.exe · 21 МБ', 'network', [14, 0, 0]], ['7-Zip 25.01', 'MSI · 2 МБ', 'programs', [9, 0, 5]]];
function programsView() {
  const pk = PKGS[S.pkg]; const [ok, bad] = pk[3];
  const cells = S.pcs.map((p, i) => { let c = 'ok', l = t('pg.st.ok'), icn = 'check'; if (p.status === 'off') { c = 'miss'; l = t('r.off'); icn = 'power'; } else if (S.pkg === 0 && i === 4) { c = 'bad'; l = t('pg.st.bad'); icn = 'alert'; } else if (S.pkg === 3 && i % 3 === 1) { c = 'miss'; l = t('pg.st.missing'); icn = 'minus'; } return `<div class="pcc ${c}" data-tip="${esc(l)}">${ic(icn, 's16')}<b>${pcName(p.n)}</b><span class="dim small" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap">${l}</span></div>`; }).join('');
  return `<div class="page"><div class="pside"><div class="psh"><button class="btn primary" style="flex:1" data-act="addprog">${ic('plus', 's16')}${t('pg.add')}</button></div><div class="psl">${PKGS.map((x, i) => `<button class="${S.pkg === i ? 'on' : ''}" data-act="pkg" data-arg="${i}">${ic(x[2])}<span><div class="t1">${x[0]}</div><div class="t2">${x[1]}</div></span></button>`).join('')}</div></div><div class="pmain"><h2>${ic(pk[2], 's24')}${pk[0]}</h2><div class="row"><button class="btn primary" data-act="pkginstall" ${S.sel.size ? '' : 'disabled'}>${ic('install', 's16')}${S.sel.size ? t('pg.inst.sel', S.sel.size) : t('pg.inst.none')}</button><button class="btn" data-act="pkgmissing">${ic('install', 's16')}${t('pg.inst.missing')}</button><button class="btn" data-act="pkgcheck">${ic('refresh', 's16')}${t('pg.check')}</button><span class="sp" style="flex:1"></span><button class="btn ghost" data-act="noop" style="color:var(--bad)">${ic('trash', 's16')}${t('pg.del')}</button></div><div class="card"><h3>${t('pg.status')} <span class="tag ok">${t('pg.st.ok')}: ${S.pcs.filter(p => p.status !== 'off').length - (S.pkg === 0 ? 1 : 0)}</span>${S.pkg === 0 ? `<span class="tag bad">${t('pg.st.bad')}: 1</span>` : ''}</h3><div class="pstat">${cells || `<span class="dim">${t('pg.nopcs')}</span>`}</div>${S.pkg === 0 ? `<div class="note bad">${ic('alert', 's16')}<span>${t('pg.fail', 'PC-05')}</span></div>` : ''}</div><div class="card"><h3>${t('pg.entry')}</h3><dl class="kv"><dt>${t('ap.family')}</dt><dd>NSIS</dd><dt>${t('ap.args')}</dt><dd><code>/S</code></dd><dt>${t('ap.rule')}</dt><dd>${t('ap.rule.name')}: IntelliJ IDEA Community Edition</dd><dt>${t('ap.limit')}</dt><dd>15 ${t('u.min')}</dd><dt>${t('pg.file')}</dt><dd>${t('pg.file.ok')}</dd></dl><div class="row"><button class="btn">${ic('save', 's16')}${t('c.save')}</button><button class="btn ghost">${ic('undo', 's16')}${t('c.revert')}</button></div></div></div></div>`;
}
const SCRIPTS = [['Відкрити IntelliJ IDEA', 'Открыть IntelliJ IDEA', 'Open IntelliJ IDEA'], ['Відкрити сайт у браузері', 'Открыть сайт в браузере', 'Open a site in the browser'], ['Закрити всі програми', 'Закрыть все программы', 'Close all programs'], ['Знайти Python', 'Найти Python', 'Find Python'], ['Очистити Завантаження', 'Очистить Загрузки', 'Empty Downloads'], ['Показати вільне місце', 'Показать свободное место', 'Show free disk space'], ['Перевірити JDK', 'Проверить JDK', 'Check the JDK']];
function scriptsView() {
  return `<div class="page"><div class="pside"><div class="psh"><input class="tx" style="flex:1;min-width:0" placeholder="${esc(t('sc.search'))}"><button class="btn" data-tip="${esc(t('sc.new'))}">${ic('plus', 's16')}</button></div><div class="psl">${SCRIPTS.map((s, i) => `<button class="${S.script === i ? 'on' : ''}" data-act="script" data-arg="${i}">${ic('scripts', 's16')}<span class="t1">${s[LI[S.lang]]}</span></button>`).join('')}</div><div style="padding:10px;border-top:1px solid var(--border)"><button class="btn ghost" style="width:100%">${ic('plus', 's16')}${t('sc.builtins')}</button></div></div><div class="pmain"><div class="row"><input class="tx" style="font-size:17px;font-weight:700;flex:1" value="${esc(SCRIPTS[S.script][LI[S.lang]])}"><select class="tx"><option>PowerShell</option><option>cmd</option></select><select class="tx"><option>${t('sc.asstudent')}</option><option>SYSTEM</option></select></div><input class="tx" value="${esc(t('sc.desc'))}"><div class="code"><span class="c"># ${t('sc.comment')}</span>\n<span class="v">$idea</span> = <span class="k">Get-ChildItem</span> <span class="s">'C:\\Program Files\\JetBrains'</span> -Filter <span class="s">idea64.exe</span> -Recurse |\n        <span class="k">Select-Object</span> -First 1\n<span class="k">if</span> (<span class="v">$idea</span>) { <span class="k">Start-Process</span> <span class="v">$idea</span>.FullName }\n<span class="k">else</span> { <span class="k">Write-Output</span> <span class="s">'IntelliJ IDEA not found'</span>; <span class="k">exit</span> 1 }</div><div class="note ok">${ic('check', 's16')}<span>${t('sc.parse')}</span></div><div class="row"><button class="btn primary" data-act="runscript" data-arg="${S.script}">${ic('play', 's16')}${t('sc.run', tgtLabel())}</button><button class="btn">${ic('save', 's16')}${t('c.save')}</button><button class="btn ghost">${ic('undo', 's16')}${t('c.revert')}</button><span class="sp" style="flex:1"></span><button class="btn ghost" style="color:var(--bad)">${ic('trash', 's16')}${t('c.delete')}</button></div></div></div>`;
}
function seedBatches() {
  if (S._seeded) return; S._seeded = true;
  S.batches.push({ id: 101, title: t('a.install') + ': IntelliJ IDEA 2026.2', time: '09:12', ok: 12, fails: [['PC-05', t('pg.fail.s')], ['PC-07', t('r.off')]], total: 14 });
  S.batches.push({ id: 102, title: t('a.send') + ': Лабораторна_5.pdf', time: '08:58', ok: 14, fails: [], total: 14 });
}
function activityView() {
  seedBatches();
  const tabs = `<div class="subnav"><button class="${S.actTab === 'actions' ? 'on' : ''}" data-act="acttab" data-arg="actions">${ic('activity', 's16')}${t('act.actions')}</button><button class="${S.actTab === 'events' ? 'on' : ''}" data-act="acttab" data-arg="events">${ic('bell', 's16')}${t('act.events')}</button></div>`;
  let body;
  if (S.actTab === 'actions') body = `<div class="row"><span class="chip on">${t('act.all')}</span><span class="chip">${ic('alert', 's14')}${t('act.problems')}</span><select class="tx"><option>${t('act.anypc')}</option><option>PC-05</option></select><span class="sp" style="flex:1"></span><button class="btn">${ic('download', 's16')}${t('act.export')}</button></div>${S.batches.map(b => { const open = S.openBatch === b.id; const off = b.fails.filter(f => f[1] === t('r.off')).length; const bad = b.fails.length - off; return `<div class="batch"><div class="bh" data-act="batch" data-arg="${b.id}"><span class="bi" style="color:${b.fails.length ? 'var(--warn)' : 'var(--ok)'}">${ic(b.fails.length ? 'alert' : 'approve')}</span><div style="flex:1;min-width:0"><b>${b.title}</b><div class="dim small">${b.time} · ${t('act.sum', b.ok, bad, off)}</div><div class="bar" style="max-width:420px"><i class="ok" style="width:${b.ok / b.total * 100}%"></i><i class="bad" style="width:${bad / b.total * 100}%"></i><i class="off" style="width:${off / b.total * 100}%"></i></div></div>${ic(open ? 'chevron-down' : 'chevron-right')}</div>${open ? `<div class="bb"><table class="tb"><tr><th>${t('act.pc')}</th><th>${t('act.state')}</th><th>${t('act.result')}</th></tr>${b.fails.map(([n, r]) => `<tr><td><b>${n}</b></td><td><span class="tag ${r === t('r.off') ? '' : 'bad'}">${r === t('r.off') ? t('act.notrun') : t('act.failed')}</span></td><td>${r}</td></tr>`).join('')}<tr><td colspan="3" class="dim">${t('act.okrest', b.ok)}</td></tr></table><div class="row" style="margin-top:8px"><button class="btn">${ic('download', 's16')}${t('act.exportone')}</button><button class="btn ghost">${ic('refresh', 's16')}${t('act.retry')}</button></div></div>` : ''}</div>`; }).join('')}`;
  else body = `<table class="tb card" style="padding:0"><tr><th>${t('ev.time')}</th><th>${t('ev.level')}</th><th>${t('act.pc')}</th><th>${t('ev.msg')}</th></tr>${[['10:41', 'warn', 'PC-06', t('ev.1')], ['10:39', 'info', 'PC-08', t('ev.2')], ['10:35', 'info', '—', t('ev.3')], ['10:20', 'bad', 'PC-05', t('ev.4')], ['09:58', 'info', 'PC-12', t('ev.5')]].map(([tm, l, pc, m]) => `<tr><td>${tm}</td><td><span class="tag ${l === 'warn' ? 'warn' : l === 'bad' ? 'bad' : ''}">${t('ev.l.' + l)}</span></td><td>${pc}</td><td>${m}</td></tr>`).join('')}</table>`;
  return `<div class="col">${tabs}<div class="pmain">${body}</div></div>`;
}
function adminView() {
  if (!admin()) return `<div class="col"><div class="pmain"><h2>${ic('admin', 's24')}${t('nav.lab')}</h2><div class="note">${ic('info', 's16')}<span>${t('teacher.lab')}</span></div>${aboutCards()}</div></div>`;
  const tabs = [['pcs', 'monitor', 'ad.pcs'], ['devices', 'laptop', 'ad.devices'], ['key', 'key', 'ad.key'], ['about', 'info', 'ad.about']];
  const nav = `<div class="subnav">${tabs.map(([id, i, k]) => `<button class="${S.adminTab === id ? 'on' : ''}" data-act="admintab" data-arg="${id}">${ic(i, 's16')}${t(k)}</button>`).join('')}</div>`;
  let b = '';
  if (S.adminTab === 'pcs') b = `<div class="row"><h2 style="flex:1">${t('ad.pcs')} <span class="tag">${S.pcs.length}</span></h2><button class="btn" data-act="push">${ic('update', 's16')}${t('a.push')}</button><button class="btn primary lg" data-act="addpcs">${ic('monitor-plus', 's16')}${t('a.addpcs')}</button></div><div class="card" style="padding:4px 8px"><table class="tb"><tr><th>${t('act.pc')}</th><th>${t('kv.host')}</th><th>${t('act.state')}</th><th>${t('kv.agent')}</th><th>${t('ad.ready')}</th><th>${t('kv.seen')}</th><th></th></tr>${S.pcs.slice(0, 10).map(p => `<tr><td><b>${pcName(p.n)}</b></td><td>${p.host}</td><td>${isOn(p) ? `<span class="tag ok">${t('s.online')}</span>` : `<span class="tag">${p.status === 'waking' ? t('s.waking') : p.status === 'elsewhere' ? t('s.elsewhere.s') : t('s.off')}</span>`}</td><td>${p.status === 'outdated' ? `<span class="tag warn">${p.ver}</span>` : p.ver}</td><td>${p.n === 3 ? `<span class="tag warn">${t('ad.av')}</span>` : p.renewal ? `<span class="tag warn">${t('b.renew')}</span>` : `<span class="dim">—</span>`}</td><td>${p.seen}</td><td><button class="ib" data-act="adminpcmenu" data-arg="${p.n}">${ic('more', 's16')}</button></td></tr>`).join('')}</table>${S.pcs.length > 10 ? `<div class="dim small" style="padding:6px 8px">${t('ad.more', S.pcs.length - 10)}</div>` : ''}</div><div class="card"><h3>${ic('ticket')}${t('ad.codes')}</h3><span class="muted">${t('ad.codes.d', 3)}</span><div class="row"><button class="btn ghost">${t('ad.codes.hist', 16)}</button><button class="btn ghost">${ic('upload', 's16')}${t('ad.codes.dormant', 2)}</button></div></div>`;
  if (S.adminTab === 'devices') b = `<div class="row"><h2 style="flex:1">${t('ad.devices')}</h2><button class="btn">${ic('lab-file', 's16')}${t('ad.labfile')}</button><button class="btn primary">${ic('approve', 's16')}${t('ad.authorize')}</button></div><p class="muted" style="margin:0">${t('ad.devices.d')}</p><div class="card" style="padding:4px 8px"><table class="tb"><tr><th>${t('ad.device')}</th><th>${t('ad.access')}</th><th>${t('ad.since')}</th><th></th></tr><tr><td>${ic('laptop', 's16')} MacBook Air <span class="dim">${t('ad.this')}</span></td><td><span class="tag acc">${t('role.admin')}</span></td><td>2026-09-04</td><td></td></tr><tr><td>${ic('monitor', 's16')} Windows — стіл викладача</td><td><span class="tag acc">${t('role.admin')}</span></td><td>2026-10-27</td><td><button class="btn ghost" style="color:var(--bad)">${ic('ban', 's16')}${t('ad.revokeconsole')}</button></td></tr><tr><td>${ic('laptop', 's16')} Ноутбук Олени</td><td><span class="tag">${t('role.teacher')}</span> <span class="dim small">${t('ad.until', '2027-01-15')}</span></td><td>2026-10-01</td><td><button class="btn ghost" style="color:var(--bad)">${ic('ban', 's16')}${t('ad.withdraw')}</button></td></tr></table></div>`;
  if (S.adminTab === 'key') {
    const open = S.keyOpenUntil > now();
    b = `<h2>${t('ad.key')}</h2><div class="cards2"><div class="card"><h3>${ic('key')}${t('key.title')}</h3><div class="stline ${open || S.keyRemembered ? 'ok' : 'off'}">${ic(open ? 'unlock' : 'lock', 's16')}<span>${keyText()}</span></div><span class="muted small">${t('key.hint')}</span><div class="row">${open ? `<button class="btn" data-act="keylock">${ic('lock', 's16')}${t('a.keylock')}</button>` : `<button class="btn primary" data-act="keyunlock">${ic('key', 's16')}${t('a.keyunlock.s')}</button>`}${S.keyRemembered ? `<button class="btn" data-act="keyforget">${ic('forget', 's16')}${t('a.keyforget')}</button>` : `<button class="btn" data-act="keyremember">${ic('remember', 's16')}${t('a.keyremember')}</button>`}</div></div><div class="card"><h3>${ic('backup')}${t('ad.backup')}</h3><div class="stline warn">${ic('alert', 's16')}<span>${t('ad.backup.stale')}</span></div><span class="muted small">${t('ad.backup.d')}</span><button class="btn primary" data-act="backup" style="align-self:flex-start">${ic('download', 's16')}${t('a.backup')}</button></div><div class="card"><h3>${ic('holders')}${t('ad.holders')}</h3><div class="list"><div class="li">${ic('student', 's16')}<span style="flex:1">В. Стогул</span><span class="dim small">${t('ad.since2', '2026-10-20')}</span></div><div class="li">${ic('laptop', 's16')}<span style="flex:1">${t('ad.holder.this')}</span><button class="btn ghost">${t('c.remove')}</button></div></div><span class="muted small">${t('ad.holders.d')}</span><button class="btn" style="align-self:flex-start">${ic('plus', 's16')}${t('ad.addholder')}</button></div><div class="card"><h3>${ic('lifebuoy')}${t('ad.rc')}</h3><span class="muted small">${t('ad.rc.d')}</span><button class="btn" style="align-self:flex-start">${ic('printer', 's16')}${t('ad.rc.print')}</button></div></div><div class="card danger"><h3 style="color:var(--bad)">${ic('key-move')}${t('ad.move')}</h3><span class="muted small">${t('ad.move.d')}</span><button class="btn" data-act="movekey" style="align-self:flex-start;color:var(--bad);border-color:var(--bad)">${ic('key-move', 's16')}${t('ad.move.go')}</button></div>`;
  }
  if (S.adminTab === 'about') b = `<h2>${t('ad.about')}</h2>${aboutCards()}`;
  return `<div class="col">${nav}<div class="pmain">${b}</div></div>`;
}
function aboutCards() {
  return `<div class="cards2"><div class="card"><h3>${ic('class')}${t('ab.lab')}</h3><dl class="kv"><dt>${t('ab.name')}</dt><dd>444-2</dd><dt>${t('ab.id')}</dt><dd class="small">lab-7f3c19e2</dd><dt>${t('ab.access')}</dt><dd>${admin() ? t('role.admin') : t('role.teacher')}</dd></dl></div><div class="card"><h3>${ic('laptop')}${t('ab.console')}</h3><dl class="kv"><dt>${t('ab.name')}</dt><dd>MacBook Air</dd><dt>${t('ab.instance')}</dt><dd class="small">inst-2b81d0</dd><dt>${t('ab.cert')}</dt><dd class="small">${t('ab.certv')}</dd><dt>${t('ab.listen')}</dt><dd>0.0.0.0:43257</dd><dt>${t('ab.data')}</dt><dd class="small">~/Library/Application Support/LabControl</dd><dt>${t('ab.ver')}</dt><dd>1.0.0+fc7601b</dd></dl></div><div class="card"><h3>${ic('network')}${t('ab.lan')}</h3><div class="stline ok">${ic('check', 's16')}<span>${t('ab.lan.ok')}</span></div><button class="btn" style="align-self:flex-start">${ic('refresh', 's16')}${t('ab.recheck')}</button></div><div class="card"><h3>${ic('swap')}${t('ab.others')}</h3><div class="list"><div class="li">${ic('laptop', 's16')}<span style="flex:1">Ноутбук Олени <span class="dim small">· ${t('ab.live')}</span></span>${admin() ? `<button class="btn ghost" style="color:var(--bad)">${ic('ban', 's16')}${t('ad.revokeconsole')}</button>` : ''}</div></div></div></div>`;
}
function settingsView() {
  const lg = ['uk', 'en', 'ru'].map(l => `<span class="chip ${S.lang === l ? 'on' : ''}" data-act="setlang" data-arg="${l}">${{ uk: 'Українська', en: 'English', ru: 'Русский' }[l]}</span>`).join('');
  return `<div class="col"><div class="pmain" style="max-width:860px"><h2>${ic('settings', 's24')}${t('set.t')}</h2><p class="muted" style="margin:0">${t('set.sub')}</p><div class="card"><h3>${ic('language')}${t('set.lang')}</h3><div class="chips">${lg}</div><div class="note">${ic('info', 's16')}<span style="flex:1">${t('set.lang.d')}</span><button class="btn">${ic('reboot', 's16')}${t('set.restart')}</button></div></div><div class="card"><h3>${ic('theme')}${t('set.theme')} <span class="tag warn">${t('tag.proposal')}</span></h3><div class="chips">${['system', 'light', 'dark'].map(v => `<span class="chip ${S.settingsTheme === v ? 'on' : ''}" data-act="settheme" data-arg="${v}">${t('set.theme.' + v)}</span>`).join('')}</div></div><div class="card"><h3>${ic('video-mode')}${t('set.video')}</h3><div class="row"><span style="min-width:220px">${t('set.video.cast')}</span><span class="seg"><button class="on">${ic('text-mode', 's16')}${t('vm.text')}</button><button>${ic('video-mode', 's16')}${t('vm.video')}</button></span></div><div class="row"><span style="min-width:220px">${t('set.video.full')}</span><span class="seg"><button>${ic('text-mode', 's16')}${t('vm.text')}</button><button class="on">${ic('video-mode', 's16')}${t('vm.video')}</button></span></div><span class="muted small">${t('set.video.d')}</span></div><div class="card"><h3>${ic('class')}${t('set.grid')}</h3><div class="row"><span class="seg"><button class="${S.grid === 'fit' ? 'on' : ''}" data-act="grid" data-arg="fit">${ic('fit', 's16')}${t('g.fit')}</button><button class="${S.grid === 'plan' ? 'on' : ''}" data-act="grid" data-arg="plan">${ic('room', 's16')}${t('g.plan')}</button></span><span class="muted small">${t('set.grid.d')}</span></div></div><div class="card"><h3>${ic('network')}${t('ab.lan')}</h3><div class="stline ok">${ic('check', 's16')}<span>${t('ab.lan.ok')}</span></div></div><div class="dim small">LabControl console 1.0.0+fc7601b</div></div></div>`;
}

/* ---------------------------------------------------------------- My labs chooser */
function chooserView() {
  const labs = [['444-2', 14, 'ready', t('ch.today')], ['312', 8, 'auth', t('ch.never')], ['215 · Мережі', 30, 'renew', t('ch.days', 6)]];
  const st = { ready: ['ok', t('ch.st.ready')], auth: ['warn', t('ch.st.auth')], renew: ['warn', t('ch.st.renew', '12.11')] };
  const btn = { ready: `<button class="btn primary" data-act="openlab">${t('ch.open')}</button>`, auth: `<button class="btn primary">${ic('approve', 's16')}${t('ch.authorize')}</button>`, renew: `<button class="btn primary" data-act="openlab">${t('ch.open')}</button><button class="btn">${t('ch.renew')}</button>` };
  return `<div class="center"><div class="cwrap"><div class="row"><div style="flex:1"><h1 style="margin:0;font-size:24px;display:flex;gap:10px;align-items:center">${ic('labs', 's24')}${t('chooser.title')}</h1><div class="muted small">${t('ch.hint')}</div></div><span class="chips">${['uk', 'en', 'ru'].map(l => `<span class="chip ${S.lang === l ? 'on' : ''}" data-act="setlang" data-arg="${l}">${{ uk: 'Українська', en: 'English', ru: 'Русский' }[l]}</span>`).join('')}</span></div><div class="row"><button class="btn primary">${ic('plus', 's16')}${t('ch.create')}</button><button class="btn">${ic('upload', 's16')}${t('ch.add')}</button><span class="sp" style="flex:1"></span><button class="btn ghost">${ic('approve', 's16')}${t('ch.authreq')}</button><button class="btn ghost">${t('ch.authall', 1)}</button></div>${labs.map(([n, c, s, last]) => `<div class="labcard"><span class="lc-ic">${ic('class', 's24')}</span><div style="flex:1;min-width:0"><div class="lc-t">${n}</div><div class="lc-s"><span>${t('ch.pcs', c)}</span><span>${last}</span><span class="tag ${st[s][0]}">${st[s][1]}</span></div></div>${btn[s]}<button class="ib" data-act="labcardmenu">${ic('more')}</button></div>`).join('')}<div class="drop">${ic('upload', 's16')} ${t('ch.drop')}</div><div class="row"><span class="sp" style="flex:1"></span><button class="btn ghost">${ic('power', 's16')}${t('ch.quit')}</button></div></div></div>`;
}

/* ---------------------------------------------------------------- First-run setup */
function firstRunView() {
  const s = S.firstStep; const names = ['fr.s0', 'fr.s1', 'fr.s2', 'fr.s3', 'fr.s4'];
  const stepper = `<div class="stepper">${names.map((k, i) => `<span class="s ${i === s ? 'on' : i < s ? 'done' : ''}"><span class="num">${i < s ? '✓' : i + 1}</span>${t(k)}</span>${i < 4 ? ic('chevron-right', 's14') : ''}`).join('')}</div>`;
  let b = '';
  if (s === 0) b = `<h1 style="margin:0">${t('fr.welcome')}</h1><p class="muted" style="margin:0">${t('fr.welcome.p')}</p><div class="bigchoice"><button data-act="frnext"><span class="bi">${ic('plus', 's24')}</span><b>${t('fr.create')}</b><span class="muted small">${t('fr.create.d')}</span></button><button><span class="bi">${ic('upload', 's24')}</span><b>${t('fr.import')}</b><span class="muted small">${t('fr.import.d')}</span></button></div>`;
  if (s === 1) b = `<h2 style="margin:0">${t('fr.create')}</h2><p class="muted" style="margin:0">${t('fr.create.p')}</p><div class="field"><label>${t('fr.labname')}</label><input class="tx" value="444-2"></div><div class="field"><label>${t('fr.holder')}</label><input class="tx" value="В. Стогул"></div><div class="row"><div class="field" style="flex:1"><label>${t('d.move.pass')}</label><input class="tx" type="password" value="••••••••••"></div><div class="field" style="flex:1"><label>${t('d.move.pass2')}</label><input class="tx" type="password" value="••••••••••"></div></div><div class="note">${ic('info', 's16')}<span>${t('fr.pass.d')}</span></div><div class="field"><label>${t('fr.console')}</label><input class="tx" value="MacBook Air"></div><label class="chk" style="padding:0"><span class="box on">${ic('check', 's14')}</span>${t('d.unlock.rem')}</label><div class="row"><span class="sp" style="flex:1"></span><button class="btn primary lg" data-act="frnext">${t('fr.createbtn')}</button></div>`;
  if (s === 2) b = `<h2 style="margin:0">${ic('lifebuoy', 's24')} ${t('fr.rc')}</h2><p class="muted" style="margin:0">${t('fr.rc.p')}</p><div class="rcode">7KQ4-M2XD-9PRT-H6WB-3FNC</div><div class="row"><button class="btn">${ic('printer', 's16')}${t('fr.print')}</button></div><label class="chk" style="padding:0"><span class="box on">${ic('check', 's14')}</span>${t('fr.rc.ack')}</label><div class="row"><span class="sp" style="flex:1"></span><button class="btn primary lg" data-act="frnext">${t('c.next')}</button></div>`;
  if (s === 3) b = `<h2 style="margin:0">${ic('backup', 's24')} ${t('fr.backup')}</h2><p class="muted" style="margin:0">${t('fr.backup.p')}</p><button class="btn primary lg" style="align-self:flex-start" data-act="frnext">${ic('download', 's16')}${t('a.backup')}</button>`;
  if (s === 4) b = `<div style="text-align:center"><div style="color:var(--ok)">${ic('approve', 's48')}</div><h1 style="margin:6px 0">${t('fr.done')}</h1><p class="muted">${t('fr.done.p')}</p></div><div class="ecard" style="max-width:none;text-align:left"><div class="steps3" style="margin:0 0 14px"><div><b><span class="num">1</span>${ic('usb', 's16')}${t('empty.s1')}</b>${t('empty.s1d')}</div><div><b><span class="num">2</span>${ic('monitor', 's16')}${t('empty.s2')}</b>${t('empty.s2d')}</div><div><b><span class="num">3</span>${ic('check', 's16')}${t('empty.s3')}</b>${t('empty.s3d')}</div></div><div class="row"><button class="btn primary lg" data-act="frtoadd">${ic('monitor-plus')}${t('a.addpcs')}</button><button class="btn lg" data-act="frtoconsole">${t('fr.open')}</button></div></div>`;
  return `<div class="center"><div class="cwrap" style="width:720px">${stepper}${b}</div></div>`;
}

/* ---------------------------------------------------------------- student-facing screens */
function studentView() {
  const tabs = [['lock', 'lock', 'stu.lock'], ['timeup', 'timer', 'stu.timeup'], ['strip', 'exam', 'stu.strip'], ['cast', 'cast', 'stu.cast']];
  let scr = '';
  if (S.stu === 'lock') scr = `<div class="lockscr"><div class="lk">${ic('lock')}</div><h1>${t('stu.lock.h')}</h1><div class="msg">${t('d.lock.msgv')}</div><div class="cd">${t('stu.lock.cd', '04:59')}</div><div class="pc">PC-07</div></div>`;
  if (S.stu === 'timeup') scr = `<div class="lockscr timeup"><div class="lk">${ic('timer')}</div><h1>${t('stu.timeup.h')}</h1><div class="msg">${t('stu.timeup.m')}</div><div class="cd">${t('stu.timeup.cd')}</div><div class="pc">PC-07</div></div>`;
  if (S.stu === 'strip') scr = `<div>${thumb('ide', 3).replace('slice', 'none')}<div class="stripdemo"><span class="ex">${ic('exam', 's16')}${t('stu.exam')}</span><span class="rem">${t('stu.rem', '23:41')}</span><span class="m">${t('d.exam.msgv')}</span></div><div class="notice"><b>${t('stu.na', 'chrome.exe')}</b><br><span style="opacity:.8">${t('stu.closing', 'Telegram.exe', 10)}</span></div></div>`;
  if (S.stu === 'cast') scr = `<div>${thumb('teacher', 1).replace('slice', 'none')}</div>`;
  return `<div class="col"><div class="setnav"><span class="lab">${t('stu.what')}</span>${tabs.map(([id, i, k]) => `<button class="btn ${S.stu === id ? 'primary' : ''}" data-act="stu" data-arg="${id}">${ic(i, 's16')}${t(k)}</button>`).join('')}<span class="sp" style="flex:1"></span><span class="dim small">${t('stu.note')}</span></div><div class="stuwrap"><div class="stuscreen" style="aspect-ratio:16/9;flex:none;max-height:100%;margin:auto;height:100%;width:auto">${scr}</div></div></div>`;
}

/* ---------------------------------------------------------------- Windows Setup / Uninstall (decision 6) */
const SETUP_STEPS = ['su.g1', 'su.g2', 'su.g3', 'su.g4', 'su.g5'];
const UN_STEPS = ['un.g1', 'un.g2', 'un.g3', 'un.g4'];
function setupSection() {
  const pages = [['ask', 'su.p.ask'], ['repair', 'su.p.repair'], ['prog', 'su.p.prog'], ['done', 'su.p.done'], ['warn', 'su.p.warn'], ['fail', 'su.p.fail'], ['un', 'su.p.un'], ['unprog', 'su.p.unprog'], ['undone', 'su.p.undone']];
  const nav = `<div class="setnav"><span class="lab">${t('setupsec.lang')}</span>${['uk', 'ru', 'en'].map(l => `<button class="btn ${S.setupLang === l ? 'primary' : ''}" data-act="setuplang" data-arg="${l}">${l.toUpperCase()}</button>`).join('')}<span style="width:14px"></span><span class="lab">${t('setupsec.page')}</span>${pages.map(([id, k]) => `<button class="btn ${S.setupPage === id ? 'primary' : ''}" data-act="setuppage" data-arg="${id}">${t(k)}</button>`).join('')}</div>`;
  return `<div class="col">${nav}<div class="winbg">${w11Page()}</div></div>`;
}
function w11(title, head, sub, content, foot, icon = 'setup') {
  return `<div class="w11" role="dialog"><div class="wt"><img src="${LOGO}" alt="">${ts('su.title')}<span class="x">${ic('close', 's16')}</span></div><div class="wh"><img src="${LOGO}" alt=""><div><h2>${head}</h2>${sub ? `<div class="s">${sub}</div>` : ''}</div></div><div class="wc">${content}</div><div class="wf">${foot}</div></div>`;
}
function w11Page() {
  const P = S.setupPage; const n = 'PC-' + String(S.setupNum).padStart(2, '0');
  const ib = (k, i, s) => `<div class="ib2 ${k}">${ic(i, 's16')}<span>${s}</span></div>`;
  const groups = (list, cur, fail) => `<div class="grp">${list.map((k, i) => { const c = fail !== undefined ? (i < fail ? 'done' : i === fail ? 'fail' : 'todo') : i < cur ? 'done' : i === cur ? 'run' : 'todo'; return `<div class="gs ${c}"><span class="gi">${c === 'done' ? ic('check', 's16') : c === 'run' ? ic('loader', 's16 spin') : c === 'fail' ? ic('x-circle', 's16') : ic('chevron-right', 's16')}</span>${ts(k)}</div>`; }).join('')}</div>`;
  if (P === 'ask' || P === 'repair') {
    const rep = P === 'repair';
    return w11('', rep ? ts('su.rep.h', n) : ts('su.ask.h'), rep ? ts('su.rep.s') : ts('su.ask.s'), `<div class="bignum"><span class="pre">PC-</span><input value="${String(S.setupNum).padStart(2, '0')}" aria-label="${esc(ts('su.num'))}" ${rep ? 'disabled' : ''}><span class="sp2"><button data-act="sunum" data-arg="1" ${rep ? 'disabled' : ''}>▲</button><button data-act="sunum" data-arg="-1" ${rep ? 'disabled' : ''}>▼</button></span><span style="font-size:13px;opacity:.75;margin-left:6px">${rep ? ts('su.rep.num') : ts('su.ask.hint')}</span></div><div class="cbx"><span class="b ${S.setupCreate ? '' : 'off'}" data-act="sucreate">${S.setupCreate ? ic('check', 's14') : ''}</span><div><b>${ts('su.create')}</b><div style="font-size:12.5px;opacity:.75">${S.setupCreate ? ts('su.create.on') : ts('su.create.off')}</div></div></div><details style="font-size:13px"><summary class="lnk">${ts('su.what')}</summary><ul style="margin:6px 0 0;padding-left:18px;opacity:.85;display:flex;flex-direction:column;gap:3px">${SETUP_STEPS.map(k => `<li>${ts(k + '.d')}</li>`).join('')}</ul></details>`, `<span style="margin-right:auto;font-size:12px;opacity:.7">${ts('su.ver')}</span><button class="wb acc" data-act="setuppage" data-arg="prog">${rep ? ts('su.repair') : ts('su.install')}</button><button class="wb">${ts('su.cancel')}</button>`);
  }
  if (P === 'prog') {
    const cur = Math.min(4, Math.floor(S.setupProg / 20)); const pct = Math.min(100, S.setupProg);
    return w11('', ts('su.prog.h', n), ts('su.prog.s'), `${groups(SETUP_STEPS, cur)}<div class="pbar"><i style="width:${pct}%"></i></div><div style="display:flex;font-size:12.5px;opacity:.75"><span>${ts(SETUP_STEPS[cur])}…</span><span style="margin-left:auto">${ts('su.elapsed', '0:' + String(12 + Math.floor(pct / 2)).padStart(2, '0'))}</span></div><details style="font-size:13px"><summary class="lnk">${ts('su.details')}</summary><div class="trace">[Setup] LabControl 1.0.0 build fc7601b2\n[AlreadyDone] Files\n[Applied] Installed apps registration\n[Applied] Student account\n[Applied] PC name: ${n}\n[Applied] Defender exclusion\n[Applied] Enrollment\n[Applied] Firewall Discovery\n[Applied] Hibernation\n[Applied] AC power SleepTimeout\n[Applied] Network Ethernet WakeOnMagicPacket\n…</div><div style="font-size:12px;opacity:.7;margin-top:4px">${ts('su.log')}</div></details>`, `<button class="wb" disabled>${ts('su.cancel')}</button>`);
  }
  if (P === 'done' || P === 'warn') {
    const w = P === 'warn';
    return w11('', ts('su.done.h'), ts('su.done.s', n), `${ib('ok', 'approve', ts('su.done.next', n))}${w ? ib('warn', 'alert', ts('su.warn.av')) + ib('warn', 'wake', ts('su.warn.wol')) : ''}<div style="font-size:14px">${ts('su.reboot', 27)}</div><div class="pbar"><i style="width:${(27 / 30) * 100}%"></i></div>`, `<span style="margin-right:auto;font-size:12px;opacity:.7">${ts('su.usbnext', 'PC-' + String(S.setupNum + 1).padStart(2, '0'))}</span><button class="wb acc">${ts('su.now')}</button><button class="wb">${ts('su.later')}</button>`);
  }
  if (P === 'fail') return w11('', ts('su.fail.h'), ts('su.fail.s'), `${groups(SETUP_STEPS, 3, 2)}${ib('bad', 'x-circle', ts('su.fail.why'))}<div style="font-size:13px">${ts('su.fail.kept')}</div><div class="lnk">${ic('folder', 's14')}C:\\ProgramData\\LabControl\\setup.log</div><details style="font-size:13px"><summary class="lnk">${ts('su.details')}</summary><div class="trace">[Applied] Firewall Discovery\n[Conflict] Network Ethernet WakeOnMagicPacket: the driver refused the value\n[Warning] Wake-on-LAN needs attention…</div></details>`, `<button class="wb">${ts('su.copy')}</button><span style="flex:1"></span><button class="wb acc" data-act="setuppage" data-arg="prog">${ts('su.retry')}</button><button class="wb">${ts('su.close')}</button>`);
  if (P === 'un') return w11('', ts('un.h'), ts('un.s'), `<div class="ib2 info">${ic('info', 's16')}<span>${ts('un.keep')}</span></div><div class="grp" style="gap:10px"><label class="cbx"><span class="b" style="border-radius:50%">${ic('check', 's14')}</span><div><b>${ts('un.opt.keep')}</b><div style="font-size:12.5px;opacity:.75">${ts('un.opt.keep.d')}</div></div></label><label class="cbx"><span class="b off" style="border-radius:50%"></span><div><b>${ts('un.opt.del')}</b><div style="font-size:12.5px;opacity:.75">${ts('un.opt.del.d')}</div></div></label></div>`, `<button class="wb acc" data-act="setuppage" data-arg="unprog">${ts('un.go')}</button><button class="wb">${ts('su.cancel')}</button>`);
  if (P === 'unprog') return w11('', ts('un.prog.h'), ts('su.prog.s'), `${groups(UN_STEPS, 2)}<div class="pbar"><i style="width:55%"></i></div><details style="font-size:13px"><summary class="lnk">${ts('su.details')}</summary><div class="trace">[plan] Stop the owned service…\n[Restored] Fast Startup\n[Restored] AC power SleepTimeout\n[Skipped] Student account (kept)\n…</div></details>`, `<button class="wb" disabled>${ts('su.cancel')}</button>`);
  if (P === 'undone') return w11('', ts('un.done.h'), '', `${ib('ok', 'approve', ts('un.done.ok'))}${ib('warn', 'alert', ts('un.done.pending'))}<div style="font-size:13px;opacity:.85">${ts('un.done.reboot')}</div>`, `<button class="wb acc">${ts('su.now')}</button><button class="wb">${ts('su.close')}</button>`);
  return '';
}

/* ---------------------------------------------------------------- actions */
function closeModal() { S.modal = null; }
function act(a, arg, el, ev) {
  const tg = targets();
  switch (a) {
    case 'noop': break;
    case 'scrim': if (ev.target === el) closeModal(); break;
    case 'close': closeModal(); break;
    case 'screen': S.screen = arg; S.menu = null; if (S.full) S.full = null; break;
    case 'ribbon': S.ribbon = arg; break;
    case 'sidebar': S.railForced = !($('.side')?.classList.contains('rail')); break;
    case 'labmenu': return openMenu('lab', el);
    case 'keymenu': return openMenu('key', el);
    case 'gochooser': S.menu = null; S.window = 'chooser'; break;
    case 'openlab': S.window = 'console'; break;
    case 'leave': S.menu = null; S.modal = { kind: 'depart' }; break;
    case 'dowait': case 'doleave': closeModal(); S.window = 'chooser'; S.pcs.forEach(p => { if (a === 'doleave' && (p.lock || (p.net && !p.exam))) { p.lock = null; if (!p.exam) p.net = null; } p.cast = false; }); S.cast = null; break;
    case 'gokey': S.menu = null; S.screen = 'admin'; S.adminTab = 'key'; break;
    case 'keyunlock': S.menu = null; S.modal = { kind: 'unlockkey' }; break;
    case 'dounlock': S.keyOpenUntil = now() + 15 * 60; if (S.modal.f.rem) S.keyRemembered = true; { const back = S.modal.back; closeModal(); if (back) { S.modal = back; if (back.kind === 'addpcs') setTimeout(simulateArrivals, 300); } } toast({ kind: 'ok', title: t('key.unlocked') }); break;
    case 'keylock': S.menu = null; S.keyOpenUntil = 0; break;
    case 'keyremember': S.menu = null; S.keyRemembered = true; toast({ kind: 'ok', title: t('key.rem') }); break;
    case 'keyforget': S.menu = null; S.keyRemembered = false; break;
    case 'takeover': S.pcs.forEach(p => { if (p.status === 'elsewhere') p.status = 'online'; }); toast({ kind: 'ok', title: t('ban.took') }); break;
    case 'closebanners': S.banners = false; break;
    case 'backup': closeModal(); toast({ kind: 'ok', title: t('fb.backup') }); break;
    case 'selall': if (S.sel.size === S.pcs.length) S.sel.clear(); else S.pcs.forEach(p => S.sel.add(p.n)); break;
    case 'clearsel': S.sel.clear(); break;
    case 'toggle': S.sel.has(+arg) ? S.sel.delete(+arg) : S.sel.add(+arg); break;
    case 'selone': S.sel.clear(); S.sel.add(+arg); break;
    case 'grid': S.grid = arg; S.arrange = false; S.zoom = 0; break;
    case 'arrange': S.arrange = !S.arrange; break;
    case 'resetplan': { const c = S._L?.cols || 5; [...S.pcs].sort((x, y) => x.n - y.n).forEach((p, i) => { p.col = i % c; p.row = Math.floor(i / c); }); } break;
    case 'zoom': S.zoom = Math.max(65, Math.min(100, (S.zoom || 100) + (+arg) * 5)); if (S.zoom === 100) S.zoom = 0; break;
    case 'zoomauto': S.zoom = 0; break;
    case 'legend': S.modal = { kind: 'legend' }; break;
    case 'lock': S.menu = null; S.modal = { kind: 'lock' }; break;
    case 'dolock': { closeModal(); runAction(t('a.lock'), tg, p => p.lock = { by: 'this' }, p => offReason(p)); } return;
    case 'unlock': S.menu = null; runAction(t('a.unlock'), tg.filter(p => p.lock), p => p.lock = null, () => null); return;
    case 'unlockall': runAction(t('a.unlockall'), lockedPcs(), p => p.lock = null, () => null); return;
    case 'cast': if (S.cast) { act('stopcast'); return; } S.menu = null; return openMenu('cast', el);
    case 'caststart': S.menu = null; { const on = tg.filter(isOn); tg.forEach(p => { if (isOn(p)) p.cast = true; }); S.cast = { targets: tg.map(p => p.n), mode: arg }; S.castMode = arg; const off = tg.length - on.length; toast({ kind: off ? 'part' : 'ok', title: t('fb.cast', on.length, tg.length), sub: off ? t('fb.castlater', off) : '' }); } break;
    case 'docast': break;
    case 'castmode': S.cast.mode = arg; S.castMode = arg; break;
    case 'stopcast': S.pcs.forEach(p => p.cast = false); S.cast = null; toast({ kind: 'ok', title: t('fb.caststop') }); break;
    case 'send': S.menu = null; S.modal = { kind: 'send' }; break;
    case 'dosend': closeModal(); runAction(t('a.send'), tg, () => { }, p => offReason(p) || (p.n === 5 ? t('r.noaccount') : null)); return;
    case 'collect': S.menu = null; S.modal = { kind: 'collect' }; break;
    case 'docollect': closeModal(); runAction(t('a.collect'), tg, () => { }, p => offReason(p) || (p.n === 9 ? t('r.toolarge') : null)); return;
    case 'reset': S.menu = null; S.modal = { kind: 'reset' }; break;
    case 'doreset': closeModal(); runAction(t('a.reset'), tg, () => { }, p => offReason(p) || (p.exam ? t('r.inexam') : null)); return;
    case 'internet': S.menu = null; S.modal = { kind: 'internet' }; break;
    case 'donet': { const m = S.modal.f.mode; closeModal(); runAction(t('a.internet'), tg, p => p.net = m === 'open' ? null : m, p => offReason(p) || (p.status === 'outdated' ? t('r.old') : null)); } return;
    case 'netopen': S.menu = null; runAction(t('a.netopen'), tg.filter(p => p.net), p => p.net = null, p => p.exam ? t('r.examnet') : null); return;
    case 'netopenall': runAction(t('a.netopenall'), S.pcs.filter(p => p.net && !p.exam), p => p.net = null, () => null); return;
    case 'exam': S.menu = null; S.modal = { kind: 'exam' }; break;
    case 'doexam': { const f = S.modal.f; closeModal(); S.exam = { collect: f.collect }; const end = now() + 45 * 60; runAction(t('a.exam'), tg, p => { p.exam = { end }; if (f.net) p.net = 'list'; }, p => offReason(p) ? (S.sel.size ? offReason(p) : t('r.latejoin')) : null); } return;
    case 'extend': S.menu = null; S.modal = { kind: 'extend' }; break;
    case 'extend10': examPcs().forEach(p => p.exam.end += 600); toast({ kind: 'ok', title: t('fb.extended', examPcs().length) }); break;
    case 'doextend': closeModal(); examPcs().forEach(p => p.exam.end += 600); toast({ kind: 'ok', title: t('fb.extended', examPcs().length) }); break;
    case 'endexam': S.menu = null; S.modal = { kind: 'endexam' }; break;
    case 'doendexam': { closeModal(); const ex = examPcs(); const coll = S.exam?.collect; ex.forEach(p => { p.exam = null; if (p.net) p.net = null; }); S.exam = null; toast({ kind: 'ok', title: t('fb.examended', ex.length) }); if (coll) toast({ kind: 'part', title: t('fb.collected', ex.length - 1, ex.length), list: [['PC-07', t('r.missing')]], acts: `<button class="btn">${ic('folder', 's16')}${t('fb.openfolder')}</button>` }); } break;
    case 'powermenu': return openMenu('power', el);
    case 'moremenu': return openMenu('more', el);
    case 'scriptmenu': case 'scriptmenu2': return openMenu('scripts', el);
    case 'runscript': S.menu = null; runAction(SCRIPTS[+arg][LI[S.lang]], tg, () => { }, p => offReason(p)); return;
    case 'wake': S.menu = null; { const off = tg.filter(p => p.status === 'off'); off.forEach(p => p.status = 'waking'); toast({ kind: 'ok', title: t('fb.wake', off.length) }); setTimeout(() => { off.forEach(p => { if (p.status === 'waking') { p.status = 'online'; p.user = 'student'; } }); rerenderKeepScroll(); }, 4000); } break;
    case 'wakefailed': S.pcs.filter(p => p.status === 'off').forEach(p => p.status = 'waking'); setTimeout(() => { S.pcs.forEach(p => { if (p.status === 'waking') { p.status = 'online'; p.user = 'student'; } }); rerenderKeepScroll(); }, 4000); break;
    case 'reboot': S.menu = null; runAction(t('a.reboot'), tg, () => { }, p => offReason(p)); return;
    case 'logoff': S.menu = null; runAction(t('a.logoff'), tg, p => p.user = '', p => offReason(p)); return;
    case 'shutdown': S.menu = null; S.modal = { kind: 'confirm', icon: 'power', title: t('d.sd.t', tgtLabel()), sub: t('tgt.sub', tgtLabel()), text: t('d.sd.p'), go: 'doshutdown', goLabel: t('a.shutdown') }; break;
    case 'doshutdown': closeModal(); runAction(t('a.shutdown'), tg, p => { p.status = 'off'; p.user = ''; p.lock = null; p.cast = false; }, p => offReason(p)); return;
    case 'remove': S.menu = null; S.modal = { kind: 'confirm', icon: 'trash', title: t('d.rm.t', S.sel.size), text: t('d.rm.p'), go: 'doremove', goLabel: t('a.remove') }; break;
    case 'doremove': closeModal(); S.pcs = S.pcs.filter(p => !S.sel.has(p.n)); toast({ kind: 'ok', title: t('fb.removed', S.sel.size) }); S.sel.clear(); break;
    case 'revoke': S.menu = null; S.modal = { kind: 'confirm', icon: 'ban', title: t('d.rv.t'), text: t('d.rv.p'), go: 'close', goLabel: t('a.revoke') }; break;
    case 'push': S.menu = null; S.modal = { kind: 'push' }; break;
    case 'dopush': closeModal(); runAction(t('a.push'), tg, p => { p.ver = '1.0.0+fc7601b'; if (p.status === 'outdated') p.status = 'online'; }, p => offReason(p) ? t('r.queued') : null); return;
    case 'install': S.menu = null; S.screen = 'programs'; break;
    case 'addpcs': S.menu = null; S.modal = { kind: 'addpcs' }; break;
    case 'wcount': S.modal.f.count = Math.max(1, Math.min(30 - S.pcs.length, +S.modal.f.count + (+arg))); break;
    case 'wdrive': S.modal.f.drive = +arg; break;
    case 'wwrite': S.modal.f.writing = true; setTimeout(() => { if (S.modal?.f) { S.modal.f.writing = false; S.modal.f.written = true; render(); } }, 1400); break;
    case 'wnext': S.modal.f.step++; if (S.modal.f.step === 3 && (S.keyOpenUntil > now() || S.keyRemembered)) setTimeout(simulateArrivals, 300); break;
    case 'wback': S.modal.f.step--; break;
    case 'wunlock': S.modal = { kind: 'unlockkey', reason: t('w.s4.reason'), back: S.modal }; break;
    case 'wfinish': case 'wplan': { const f = S.modal.f; const add = Object.values(f.arrived).filter(v => v === 'ok').length; const base = S.pcs.length; if (S.scenario === 'empty') S.scenario = 'lesson'; for (let i = 1; i <= add; i++) { const n = base + i; S.pcs.push({ n, user: 'student', status: 'online', app: APPS[n % APPS.length], col: (n - 1) % 5, row: Math.floor((n - 1) / 5), host: pcName(n), ip: '192.168.1.' + (100 + n), ver: '1.0.0+fc7601b', seen: '11:02' }); } S.count = S.pcs.length; closeModal(); S.screen = 'class'; if (a === 'wplan') { S.grid = 'plan'; S.arrange = true; } } break;
    case 'addprog': S.modal = { kind: 'addprog' }; break;
    case 'apnext': S.modal.f.step++; break;
    case 'apback': S.modal.f.step--; break;
    case 'apdone': closeModal(); S.screen = 'programs'; runAction(t('a.install') + ': IntelliJ IDEA', S.pcs, () => { }, p => offReason(p) ? t('r.queued') : null); return;
    case 'pkg': S.pkg = +arg; break;
    case 'pkginstall': case 'pkgmissing': runAction(t('a.install') + ': ' + PKGS[S.pkg][0], a === 'pkginstall' ? tg : S.pcs, () => { }, p => offReason(p) ? t('r.queued') : (p.n === 5 && S.pkg === 0 ? t('pg.fail.s') : null)); return;
    case 'pkgcheck': toast({ kind: 'ok', title: t('pg.checked', S.pcs.filter(isOn).length) }); break;
    case 'script': S.script = +arg; break;
    case 'acttab': S.actTab = arg; break;
    case 'batch': S.openBatch = S.openBatch === +arg ? 0 : +arg; break;
    case 'details': S.screen = 'activity'; S.actTab = 'actions'; S.openBatch = +arg; dropToast(+el.closest('.toast')?.querySelector('[data-act=toastclose]')?.dataset.arg); break;
    case 'toastclose': dropToast(+arg); break;
    case 'admintab': S.adminTab = arg; break;
    case 'adminpcmenu': return openMenu('adminpc', el, { n: +arg });
    case 'movekey': S.modal = { kind: 'movekey' }; break;
    case 'radio': S.modal.f[el.dataset.grp] = arg; break;
    case 'sw': S.modal.f[arg] = !S.modal.f[arg]; break;
    case 'view': { const n = S.menu?.n || [...S.sel][0] || S.pcs[0]?.n; S.menu = null; S.full = n; S.control = false; } break;
    case 'viewctl': { const n = S.menu?.n || [...S.sel][0] || S.pcs[0]?.n; S.menu = null; S.full = n; S.control = true; } break;
    case 'fullclose': S.full = null; break;
    case 'ctl': S.control = !S.control; break;
    case 'fullmode': S.fullMode = arg; break;
    case 'qualmenu': return openMenu('quality', el);
    case 'quality': S.menu = null; S.quality = arg; break;
    case 'setlang': S.lang = arg; break;
    case 'settheme': S.settingsTheme = arg; if (arg !== 'system') S.theme = arg; break;
    case 'labcardmenu': return openMenu('labcard', el);
    case 'modesmenu': return openMenu('modes', el);
    case 'ovfmenu': return openMenu('ovf', el);
    case 'bannermore': S.bannersOpen = !S.bannersOpen; break;
    case 'frnext': S.firstStep = Math.min(4, S.firstStep + 1); break;
    case 'frtoadd': S.window = 'console'; S.scenario = 'empty'; makePcs(); S.modal = { kind: 'addpcs' }; break;
    case 'frtoconsole': S.window = 'console'; break;
    case 'stu': S.stu = arg; break;
    case 'setuplang': S.setupLang = arg; break;
    case 'setuppage': S.setupPage = arg; if (arg === 'prog') { S.setupProg = 0; const tick = () => { if (S.setupPage !== 'prog' || S.window !== 'setup') return; S.setupProg += 4; if (S.setupProg >= 100) { S.setupPage = 'done'; render(); return; } render(); setTimeout(tick, 220); }; setTimeout(tick, 220); } break;
    case 'sunum': S.setupNum = Math.max(1, Math.min(30, S.setupNum + (+arg))); break;
    case 'sucreate': S.setupCreate = !S.setupCreate; break;
  }
  if (a !== 'scrim' || ev.target === el) { if (!['labmenu', 'keymenu'].includes(a)) S.menu = S.menu && el.closest('.menu') ? null : S.menu; }
  rerenderKeepScroll();
}

/* ---------------------------------------------------------------- mosaic pointer handling: select, rubber band, drag */
function bindMosaic() {
  const m = $('#mosaic'); if (!m) return;
  if (S._ro) S._ro.disconnect(); S._ro = new ResizeObserver(() => layout()); S._ro.observe(m);
  const rg = $('#zoomr'); if (rg) rg.oninput = () => { S.zoom = +rg.value >= 100 ? 0 : +rg.value; layout(); const b = $('[data-act=zoomauto]'); if (b) b.style.cssText = S.zoom ? '' : 'color:var(--accent);font-weight:700'; };
}
let press = null;
function localPt(e) { const r = $('#canvas').getBoundingClientRect(), k = S._scale || 1; return [(e.clientX - r.left) / k, (e.clientY - r.top) / k]; }
document.addEventListener('pointerdown', e => {
  if (e.button !== 0) return;
  const tile = e.target.closest('.tile'); const mos = e.target.closest('#mosaic');
  if (!mos || e.target.closest('[data-act]')) return;
  const [x, y] = localPt(e);
  if (tile && S.grid === 'plan' && S.arrange) { press = { kind: 'drag', el: tile, n: +tile.dataset.n, x, y, ox: parseFloat(tile.style.left), oy: parseFloat(tile.style.top) }; tile.classList.add('drag'); e.preventDefault(); return; }
  if (!tile) { press = { kind: 'band', x, y, add: e.metaKey || e.ctrlKey, base: new Set(S.sel), moved: false }; }
});
document.addEventListener('pointermove', e => {
  if (!press) return; const [x, y] = localPt(e);
  if (press.kind === 'drag') { press.el.style.left = (press.ox + x - press.x) + 'px'; press.el.style.top = (press.oy + y - press.y) + 'px'; return; }
  if (Math.abs(x - press.x) + Math.abs(y - press.y) < 5 && !press.moved) return; press.moved = true;
  let b = $('.rubber'); if (!b) { b = document.createElement('div'); b.className = 'rubber'; $('#canvas').appendChild(b); }
  const l = Math.min(x, press.x), tp = Math.min(y, press.y), w = Math.abs(x - press.x), h = Math.abs(y - press.y);
  Object.assign(b.style, { left: l + 'px', top: tp + 'px', width: w + 'px', height: h + 'px' });
  const sel = new Set(press.add ? press.base : []);
  $('#canvas').querySelectorAll('.tile').forEach(el => { const ex = parseFloat(el.style.left), ey = parseFloat(el.style.top), ew = el.offsetWidth, eh = el.offsetHeight; const hit = ex < l + w && ex + ew > l && ey < tp + h && ey + eh > tp; if (hit) sel.add(+el.dataset.n); el.classList.toggle('sel', sel.has(+el.dataset.n)); });
  press.sel = sel;
});
document.addEventListener('pointerup', e => {
  if (!press) return; const p = press; press = null;
  if (p.kind === 'drag') {
    const { w, h, gap } = S._cell; const left = parseFloat(p.el.style.left), top = parseFloat(p.el.style.top);
    const c = Math.max(0, Math.min(S._L.cols - 1, Math.round(left / (w + gap)))), r = Math.max(0, Math.min(S._L.rows - 1, Math.round(top / (h + gap))));
    const me = S.pcs.find(q => q.n === p.n); const other = S.pcs.find(q => q.col === c && q.row === r && q !== me);
    if (other) { other.col = me.col; other.row = me.row; } me.col = c; me.row = r; rerenderKeepScroll(); return;
  }
  if (p.moved) { S.sel = p.sel || new Set(); rerenderKeepScroll(); return; }
  if (!p.add) { S.sel.clear(); rerenderKeepScroll(); }
});
document.addEventListener('click', e => {
  const strip = e.target.closest('[data-s]');
  if (strip) { const k = strip.dataset.s; let v = strip.dataset.v; if (k === 'count') v = +v; S[k] = v; if (k === 'count' || k === 'scenario') makePcs(); if (k === 'window') { S.modal = null; S.menu = null; S.full = null; } if (k === 'win') S.railForced = null; if (k === 'role' && v === 'teacher' && S.screen === 'admin') S.adminTab = 'about'; render(); return; }
  const el = e.target.closest('[data-act]');
  if (S.menu && !e.target.closest('.menu') && !(el && ['labmenu', 'keymenu', 'cast', 'powermenu', 'moremenu', 'scriptmenu', 'qualmenu', 'adminpcmenu', 'labcardmenu', 'modesmenu', 'ovfmenu'].includes(el.dataset.act))) { S.menu = null; if (!el) { rerenderKeepScroll(); return; } }
  if (el) { if (el.disabled) return; act(el.dataset.act, el.dataset.arg, el, e); return; }
  const tile = e.target.closest('.tile');
  if (tile && !(S.grid === 'plan' && S.arrange)) {
    const n = +tile.dataset.n;
    if (e.metaKey || e.ctrlKey) S.sel.has(n) ? S.sel.delete(n) : S.sel.add(n);
    else if (e.shiftKey && S._last) { const [a, b] = [S._last, n].sort((x, y) => x - y); for (let i = a; i <= b; i++) S.sel.add(i); }
    else { const only = S.sel.size === 1 && S.sel.has(n); S.sel.clear(); if (!only) S.sel.add(n); }
    S._last = n; rerenderKeepScroll();
  }
});
document.addEventListener('dblclick', e => { const tile = e.target.closest('.tile'); if (tile && !S.arrange) { S.full = +tile.dataset.n; S.control = false; render(); } });
document.addEventListener('contextmenu', e => {
  const tile = e.target.closest('.tile'); if (!tile) return; e.preventDefault();
  const n = +tile.dataset.n; if (!S.sel.has(n)) { S.sel.clear(); S.sel.add(n); }
  const w = $('#win').getBoundingClientRect(), k = S._scale || 1; S.menu = { kind: 'tile', n, x: (e.clientX - w.left) / k, y: (e.clientY - w.top) / k }; render();
  const m = $('.menu'); const wh = $('#win').offsetHeight; if (m && S.menu.y + m.offsetHeight > wh - 8) m.style.top = Math.max(8, wh - m.offsetHeight - 8) + 'px';
});
document.addEventListener('keydown', e => {
  if (e.target.matches('input,textarea,select')) return;
  const mod = e.metaKey || e.ctrlKey;
  if (e.key === 'Escape') { if (S.menu) S.menu = null; else if (S.modal) S.modal = null; else if (S.full) S.full = null; else S.sel.clear(); render(); }
  if (mod && e.key.toLowerCase() === 'a' && S.window === 'console' && S.screen === 'class') { e.preventDefault(); S.pcs.forEach(p => S.sel.add(p.n)); render(); }
  if (mod && e.key.toLowerCase() === 'l' && S.window === 'console') { e.preventDefault(); S.modal = { kind: 'lock' }; render(); }
  if (e.key === 'Enter' && S.sel.size === 1 && !S.modal && S.window === 'console') { S.full = [...S.sel][0]; render(); }
});
// tooltips
let tipEl = null;
document.addEventListener('mouseover', e => {
  const el = e.target.closest('[data-tip]'); if (tipEl) { tipEl.remove(); tipEl = null; } if (!el) return;
  tipEl = document.createElement('div'); tipEl.className = 'tip'; tipEl.textContent = el.dataset.tip; document.body.appendChild(tipEl);
  const r = el.getBoundingClientRect(); tipEl.style.left = Math.min(window.innerWidth - tipEl.offsetWidth - 6, Math.max(6, r.left + r.width / 2 - tipEl.offsetWidth / 2)) + 'px'; tipEl.style.top = (r.bottom + 6) + 'px';
});
// countdowns
setInterval(() => {
  document.querySelectorAll('[data-cd=pc]').forEach(el => { const p = S.pcs.find(q => q.n === +el.dataset.n); if (p?.exam) el.textContent = mmss(p.exam.end - now()); });
  const ex = examPcs(); const m = document.querySelector('[data-cd=exam]'); if (m && ex.length) m.textContent = mmss(Math.max(...ex.map(p => p.exam.end - now())));
}, 1000);
window.addEventListener('resize', () => scale());
makePcs(); render();
