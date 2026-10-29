# LabControl console redesign — implementation handoff (Avalonia 12)

Concept “The class is the home”. **Variant A (ribbon) is the owner’s choice (2026-10-29)** and the
one to implement; variant B stays in the prototype only as a reference and is not specified further. Prototype: `prototype/index.html`; icons: `icons/`; app icon: `app-icon/`; per-key mapping:
`icons/mapping.md`. Everything here is implementable with FluentTheme, standard controls and custom
templates; the only new asset is our own icon geometry (no new package).

## 1. Design tokens

Put them in one `Styles/Tokens.axaml` `ResourceDictionary` with `ThemeDictionaries` (`Light`, `Dark`);
reference with `{DynamicResource}`; no colour literal anywhere else (today’s `IndianRed`, `DarkOrange`,
`#CC8A2B00`, `#CC1F5FAF`, `#CC00695C`, `#CC6A1B9A`, `#1F5FAF` go).

### 1.1 Colours (contrast = WCAG 2.1 ratio, computed)

| Token | Light | Dark | Use / contrast |
|---|---|---|---|
| `Bg` | `#F4F5F7` | `#0F1114` | window background |
| `Surface` | `#FFFFFF` | `#181B20` | bars, cards, tiles, dialogs |
| `Surface2` | `#EEF0F3` | `#20242A` | hover, inputs, secondary panels |
| `Surface3` | `#E3E6EB` | `#2A2F36` | progress track, stripes |
| `Border` / `BorderStrong` | `#D5DAE1` / `#B7BEC8` | `#323841` / `#4A525D` | dividers / control outlines |
| `Fg` | `#15181C` | `#ECEEF1` | body text — 16.3 on Bg (both themes) |
| `Fg2` | `#49515B` | `#B3BAC4` | secondary text — 7.4 / 9.7 |
| `Fg3` | `#5F6774` | `#8E96A2` | tertiary, captions — 5.2 / 6.3 |
| `Accent` | `#2F5BEA` | `#7C9BFF` | primary buttons, selection, focus — 5.1 / 7.2 on Bg |
| `AccentFg` | `#FFFFFF` | `#0B1020` | text on Accent — 5.5 / 7.2 |
| `AccentSoft` | `#E6ECFD` | `#1E2A4D` | selected nav item, chips |

### 1.2 Semantic mode colours (one per classroom state; the same in tile frame, badge, mode bar, dialog icon, legend)

| Token | Light | Dark | Meaning | Badge text contrast (white / `#0B0D10`) |
|---|---|---|---|---|
| `Lock` (+`LockSoft` `#FEF3E2`/`#3A2A10`) | `#B45309` | `#F5A524` | screen locked | 5.0 / 9.5 |
| `Cast` (+`CastSoft` `#E5EFFC`/`#14294A`) | `#1D63C9` | `#5EA0FF` | shows teacher’s screen | 5.7 / 7.4 |
| `Exam` (+`ExamSoft` `#E1F4F1`/`#0E3330`) | `#0F766E` | `#2DD4BF` | exam | 5.5 / 10.5 |
| `Net` (+`NetSoft` `#F0EAFE`/`#2B2149`) | `#7C3AED` | `#B49CFF` | internet limited | 5.7 / 8.5 |
| `Bad` (+`BadSoft` `#FCE9E9`/`#3D1A1A`) | `#C62828` | `#FF7A7A` | problem / failed / danger | 5.6 / 7.7 |
| `Warn` (+`WarnSoft` `#FFF4DC`/`#3A2C0F`) | `#A15C00` | `#F2B84B` | attention (outdated, renewal, partial) | 5.2 / 10.9 |
| `Ok` (+`OkSoft` `#E5F5EA`/`#123222`) | `#137336` | `#4ADE80` | online, done | 5.9 / 11.2 |
| `Off` | `#6B7280` | `#8E96A2` | off / not linked | 4.8 / 6.5 |

`Fg` on every `*Soft` background ≥ 11.7. Mode colour used as text on `Surface` ≥ 4.8. Badges use white
text in light and `#0B0D10` in dark (`BadgeFg`). Student screens keep `OverlayText` colours
(`#101C2C` / `#F4F6F8` 15.8, muted `#A8B4C4` 8.2, accent amber `#F0B42C`, exam teal `#2DD4BF` 9.2).

### 1.3 Spacing, radii, elevation
- Spacing scale 4 · 8 · 12 · 16 · 24 · 32. Window gutters 12; card padding 14–16; tile gap **10**.
- Radii: `RadiusSm` 4 (inputs inner), `Radius` 6–7 (buttons), `RadiusMd` 10 (cards, tiles 9), `RadiusLg` 14 (dialogs).
- Elevation: tile `0 1 2 /6 % + 0 4 12 /8 %`; menus/toasts/dialogs `0 12 40 /22 %` (Avalonia `BoxShadow`).

### 1.4 Type scale (Inter, already shipped)
| Style | Size / line | Weight | Use |
|---|---|---|---|
| Display | 24 / 30 | 700 | wizard *Done*, first-run headings |
| Title | 20 / 26 | 700 | page and wizard step titles |
| Subtitle | 17 / 22 | 650 | dialog titles |
| Body | 14 / 20 | 400–550 | default |
| BodySm | 13 / 18 | 400–550 | tables, ribbon tabs, toasts |
| Caption | 12–12.5 / 16 | 400–600 | ribbon button labels, hints, status bar |
| Micro | 11–11.5 / 14 | 700 | badges, uppercase group labels |
Tabular numerals (`FontFeatures="+tnum"`) for countdowns, PC numbers, counts.

### 1.5 Icon sizes
16 (inline, menus, badges at 14), 20 (buttons, nav), 24 (ribbon buttons, dialog headers), 32–48
(empty states, wizard illustrations). Stroke 2 at 24 (scales with the geometry; at 14–16 use 2.4 to
keep weight).

## 2. Components and states

| Component | Avalonia | States / notes |
|---|---|---|
| **LcIcon** | `TemplatedControl` with a `Path` (`Data` = StreamGeometry, `Stroke`=`Foreground`, `StrokeThickness`, round caps/joins, `Stretch=Uniform`, 24×24 viewbox via a `Viewbox`) | `PathIcon` renders **fill only**; our icons are stroke geometry, so use LcIcon (or pre-outline the strokes with a build script). Geometries in `Assets/Icons.axaml` as `<StreamGeometry x:Key="Icon.Lock">…</StreamGeometry>` generated from `build/icons.json`. |
| **TopBar** | `Grid` | lab switcher (`Button` + `Flyout`), section `ListBox` styled as tabs (`Selected`), online pill, key chip (admin only), bell (badge dot), gear |
| **LabSwitcher** | `Button` + `MenuFlyout` | shows lab name + role; *My labs…*, *Leave the room…* |
| **KeyChip** | `Button` + `MenuFlyout` | Locked (neutral) / Open until hh:mm (Ok soft) / Remembered (Ok soft) |
| **Ribbon** | `TabControl` with custom `TabStripPlacement=Top` template; content = `StackPanel` of `RibbonButton` groups + separators | normal (80 px body) / **compact** when window height < 800 (`RibbonButton` switches to horizontal via a style class set from `Bounds`); buttons that do not fit the width move into a trailing **More** flyout; tab dot when a mode runs |
| **TargetChip** | `Border` + text + clear button | “Whole class · N PCs” / “Selected · N PCs ✕” — bound to `TargetText` |
| **RibbonButton** | `Button` (`Classes="ribbon"`) icon over label, 76–118 px wide, label wraps to 2 lines | normal, hover, pressed, disabled (38 %), `primary` (accent icon), `danger` (Bad), split (caret opens `MenuFlyout`), tooltip with shortcut |
| **CommandBar** (B) | `StackPanel` of `Button` icon+label | same states; *Power ▾*, *More ▾* as `MenuFlyout` |
| **ModeBar** | `Border` (`Classes` exam/cast/lock/net) | one mode: icon square in mode colour, text, big countdown, actions; ≥2 modes → **ModeChipStrip**: one non-wrapping row of chips, overflow into a “+N” chip with a flyout |
| **MosaicToolbar** | `WrapPanel` | select-all tri-state `CheckBox`, count/hint, `Fit all | Room plan` segmented (`RadioButton`s styled), *Arrange*/*Done*, *By numbers*, size `Slider` 65–100 % with −/+ and *Auto*; one row only — at narrow widths the hint, slider and segment labels drop away, the legend button never moves, grid info, legend button |
| **MosaicCanvas** | existing `Canvas` in `LabView` | positions from §4; rubber band = `Rectangle` overlay; drag only in *Arrange* |
| **Tile** | `ContentControl` template (§3) | see §3 |
| **SidePanel** (B) | `Border` 310 px | 1 PC: picture, state lines, actions, details; N PCs: chips + actions |
| **Toast** | `ItemsControl` bottom-right, 380 px | `prog` (spinner + bar), `ok` (auto-hide 6 s), `part` (Warn, persistent, PC list ≤4 + “N more”), `bad` (Bad, persistent); actions *Details*, *Wake them*, *Open folder* |
| **Banner** | `Border` | info (take over) / warning (backup, renewal, LAN access) with one action; dismissible info; several → one line + “+N”; below 600 px height only in the bell |
| **Dialog** | `Window` (existing `DialogWindow<T>`) re-templated | header (tinted icon square + title + target line), body, footer (left note, Cancel, primary/danger) |
| **Wizard** | `Window` 980×640 | left step list (done ✓ / current / next), content, footer Back/Next |
| **StateLine** | `Border` soft background + icon + text | lock/cast/exam/net/bad/warn/ok/off |
| **Card**, **Table**, **Chip**, **Tag**, **Toggle** (`ToggleSwitch`), **Radio card** | Fluent controls restyled | Radio card = `RadioButton` template with title + subtitle |

## 3. Tile (one visual language)

```
┌──────────────────────────────┐  frame 2 px in the top-priority mode colour
│[🔒 Locked][⏱ 23:41]      (✓)│  badges top-left (icon + short word; icon only < 170 px, exam keeps time)
│                              │  check circle top-right: on hover (toggle), filled when selected
│     picture (16:9) or        │  centre state for off / waking / elsewhere / no picture
│     centre icon + 1-2 lines  │
├──────────────────────────────┤
│ PC-07  👤 student      ● / Problem │  caption 26 px: number (bold, tabular), user (hidden < 150 px), status
└──────────────────────────────┘
```
- **Frame priority**: Bad › Lock › Cast › Exam › Net (only one frame; the rest are badges).
- **Selected**: 3 px `Accent` outline offset 1 px + filled check circle; independent of the frame.
- **Off**: diagonal stripes (`Surface2`/`Surface3`), power icon, “Off · last seen 08:12”; never opacity.
- **Waking**: stripes + pulsing sunrise icon; caption “Waking…” in Warn.
- **Held elsewhere**: stripes + link-off icon + console name.
- **No picture yet**: `Surface2` + image-off icon.
- **Problem**: red frame, badge “Problem”, caption “Problem”; the words (from `Overlay.*`, `Capture.*`,
  `Exam.Reason.*`, `Internet.*` problem codes, `Readiness.*`) in the tooltip and the B panel, one line each.
- **Outdated / Renewal**: Warn badges “Old agent” / “Certificate”.
- Tooltip = the B panel’s state lines + host, address, agent version, serial, last seen (today’s data).
- State is only what the PC reports (`IsLocked`, `IsBroadcasting`, `InExam`, `InternetMode`…) — unchanged
  view-model properties; only the template changes.

## 4. Grid algorithm — every PC always fits, nothing scrolls

Owner rule: **all PCs fit the window**, in every state, at every window size. There is no minimum tile
width and no scroll fallback; at small sizes the tile gets a denser caption, and the full view is one
double-click away. The bars above the mosaic are bounded so the mosaic always keeps most of the height:
- **one mode line**: two or more running modes (exam, my screen, lock, internet) become one row of
  chips; chips that do not fit go into a **“+N”** chip (menu with the hidden modes and their actions);
- **one banner line**: several banners show as one compact line with **“+N”** to expand; below 600 px
  window height banners live only in the bell;
- the ribbon is compact below 800 px height; ribbon buttons that do not fit the width move, from the
  end, into a **More** button (the same happens in a narrow window, never a second row).

```
AR = 16/9, WMAX = 560
NORMAL = { cap: 26, gap: 10 }          # caption: number + user + status word
DENSE  = { cap: 18, gap: 6 }           # caption: number + status dot only, used when tiles < 150 px

tileWidth(W, H, cols, rows, m) = min( (W - (cols-1)*m.gap) / cols,
                                      ((H - (rows-1)*m.gap) / rows - m.cap) * AR,
                                      WMAX )

fitGrid(W, H, N, m):                   # W, H = mosaic viewport minus 12 px sides / 8 px top-bottom
    best = none
    for c in 1..N:
        r = ceil(N / c); w = tileWidth(W, H, c, r, m); empty = c*r - N
        if best is none or w > best.w + 0.5 or (|w - best.w| ≤ 0.5 and empty < best.empty):
            best = (c, r, w, empty)
    return best

fitAll(W, H, N, [cols, rows]):         # cols/rows given = room plan
    m = NORMAL; L = cols ? (cols, rows, tileWidth(W,H,cols,rows,m)) : fitGrid(W,H,N,m)
    if L.w < 150: m = DENSE; L = same with DENSE
    return L, m

layout:
    Fit all:   L = fitAll(W, H, N); order by PC number; cell i → (i mod c, i div c)
    Room plan: c = max(col)+1, r = max(row)+1 from lab.json (+1 spare column and row while Arranging);
               L = fitAll(W, H, N, c, r)          # the plan is scaled to fit too
    size:      w = floor(L.w * size%), size% ∈ [65 %, 100 %] (slider, −/+ in 5 % steps, “Auto” = 100 %)
               → the size control only makes tiles smaller than the fit; it can never cause scrolling
    h = floor(w / AR) + m.cap; x = col*(w+gap); y = row*(h+gap); centre the block both ways
    recompute on mosaic Bounds change (1-frame debounce), N change, mode/size change, bar changes
    w < 170 → badges icon-only (exam keeps its time); w < 110 → smaller badges/check (“tiny”)
```

Worked examples (measured in the prototype, variant A, *Fit all*, size Auto; tile = width × height incl. caption):

| Window | State | Mosaic W×H | 8 PCs | 14 PCs | 30 PCs |
|---|---|---|---|---|---|
| 1440×900 | Lesson (no bars) | 1414×623 | 4×2 · 346×220 | 5×3 · 274×180 | 8×4 · 168×120 |
| 1440×900 | All states (1 banner line + 1 mode line) | 1414×535 | 4×2 · 346×220 | 5×3 · 258×171 | 8×4 · 168×120 |
| 1280×720 (compact ribbon) | Lesson | 1254×477 | 4×2 · 306×198 | 5×3 · 224×152 | 8×4 · 151×102 dense |
| 1280×720 (compact ribbon) | All states | 1254×389 | 4×2 · 297×193 | 5×3 · 172×122 | 8×4 · **132×92 dense** |
| 1920×1080 | All states | 1894×715 | 4×2 · 466×288 | 5×3 · 365×231 | 8×4 · 228×154 |
| 720×480 (minimum) | All states (banners in the bell) | 694×149–157 | 4×2 · 102×75 dense | 7×2 · 94×70 dense | 10×3 · 49×45 dense |
| today (`Reflow`), 1440 wide | — | — | 6 cols · 233 | 6 cols · 233, 3 rows | 6 cols · 233, **5 rows, scrolls** |

The prototype’s overflow check (17 280 combinations: variants A/B × uk/ru/en × 1440×900, 1280×720,
720×480, 1000×640, 1920×1080 × All states/Lesson/New lab × 1/8/14/30 PCs × admin/teacher × fit/plan ×
four ribbon tabs × with/without selection and size 65 %) finds no tile outside the mosaic, no
scrolling, no horizontal overflow in any bar, never more than one mode line or one banner line, and the
legend “?” always on the grid-toolbar row. One PC → 560 px centred.

**Room plan.** Saved `(Column, Row)` per PC (existing `LayoutTile`, `SetLayout`, `MoveTile` swap).
*Arrange* toggles drag (grips, dashed cells including one spare row and column inside the fitted
area); a drop is clamped to the grid; *By numbers* writes `col = i mod c, row = i div c`.
**Persistence** (new, per lab, this computer only, e.g. `labs/<id>/view.json`): `{ mode: fit|plan,
sizePercent: 65–100 }`; never in a backup.

## 5. Keyboard shortcuts

| Keys (⌘ on macOS, Ctrl elsewhere) | Action |
|---|---|
| ⌘A / Esc | select all / clear selection (Esc closes a menu, dialog, full view first) |
| Click, ⌘-click, Shift-click, drag on empty mosaic | select one / toggle / range / rubber band |
| Arrow keys, Space | move focus between tiles / toggle the focused tile |
| Enter or double-click | open full view of the one selected PC |
| ⌘L / ⌘⇧L | Lock… / Unlock |
| ⌘⇧S | Show my screen (last Text/Video choice) / Stop showing |
| ⌘E | Start exam… |
| ⌘I | Internet… |
| ⌘⇧F | Send files… |
| ⌘1…⌘5 | Class, Programs, Scripts, Activity, Lab; ⌘, Settings |
| Alt+1…4 (A) | ribbon tab Lesson / Exam & internet / Power / Computers |
| Full view: ⌘⇧C | toggle Control; Ctrl+Alt+Del and Win only by buttons (never forwarded from the Mac) |
No shortcut for Shut down, Reset profile, Remove (danger stays mouse + confirm).

## 6. Old → new (screens; commands per key in `icons/mapping.md`)

| Today | New |
|---|---|
| Toolbar (27 buttons) | Ribbon tabs (A) / command bar + menus (B); pairs merged (selection or whole class) |
| `⟵ Disconnect` + lab chip | Lab switcher ▾ (*My labs…*, *Leave the room…*) |
| *Enrol PCs…* / *Lock* (key) | Key chip (admin) + wizard step 4 + Lab › Key and backup |
| Lab tab: script quick-runner | Ribbon Lesson › *Run script ▾* |
| Lab tab: mosaic + hint | Class: mosaic toolbar (select all, Fit/Plan, size, legend) + mosaic |
| Scripts tab | Scripts section (same functions) |
| Packages tab | Programs section (catalog, status grid, Add program wizard incl. test install) |
| Jobs + Events tabs | Activity › Actions (per-batch cards, export) + Event log |
| Settings tab | Lab › Computers (enrolment codes, dormant codes) · Teacher devices (authorize, withdraw, lab file) · Key and backup (key, remember/forget, holders, recovery code, backup, move to new key) · About this lab (ids, endpoint, data dir, version, other consoles + revoke, LAN access); Settings (this computer): language + restart, theme*, default Text/Video, mosaic |
| Settings › Enrolment (payload / installer / void earlier) | **Add computers** wizard (empty state, Lab › Computers, first-run Done, ribbon Computers) |
| Status bar (port, instance, kbit/s) | online/off/problem counts + key; technical data in Lab › About |
| Banners | same banners, restyled, one action each |
| ScreenWindow | Full view (in-window overlay in the prototype; keep a separate window in Avalonia): Back, PC + user, **Control** toggle, Ctrl+Alt+Del, Win, Text|Video, Quality ▾, stats |
| Dialogs (Lock, Internet, Exam, Extend, Send files, Collect, Reset, Push, Unlock, Departure, Move, Add holder, recovery code, Add package) | same content, new template: tinted icon, target line, consequences in notes, primary button says the count (“Lock 14 PCs”) |
| LabChooserWindow | My labs: lab cards with status tag + one primary button per status + ⋯; top actions; drop zone; language |
| SetupWindow (first run) | 5-step stepper; Done → Add computers |
| Student lock / Time is up / exam strip | lock glyph tile added (amber; teal for *Time is up*), strip gets an exam glyph; colours, sizes, text unchanged (`OverlayText`) |
| Windows Setup / Uninstall | §10 |

## 7. Feedback for actions (roadmap: “a visible answer to every failed action”)

- Every command that reaches PCs creates a **toast** bound to its job batch (`JobBatchLogs` /
  `JobHistory` already group by batch; policies — lock, internet, exam, broadcast — answer through
  `OverlayState`/`InternetState`/`ExamState`, so the toast watches those for the target PCs with a
  10 s budget, like `DeparturePolicyEndBudget`).
- States: **progress** (“Sent to 14 PCs · 9 answered”, bar) → **ok** (auto-hide 6 s) | **partial**
  (Warn) | **failed** (Bad); partial/failed persist, list ≤4 PCs with the PC’s own reason (refusal
  `problem` / `JobResult.message` / “off — not sent” / “queued until it links”), then “N more —
  see details”.
- **Details** → Activity › Actions, that card expanded. **Wake them** when the failures are off PCs.
  **Open folder** after collect.
- The bell in the top bar shows a dot while a partial/failed toast is unread; Activity lists all.
- Offline PCs for policies are *skipped* (today’s behaviour) and the toast says so; for jobs they are
  *queued* where the job queues today.

## 8. New and renamed resource keys (key wording; full list in the prototype’s `build/i18n.js`)

All go into `Strings.resx` + `.uk.resx` + `.ru.resx` together (`ResourceTranslationTests`). Removed keys: `Action.*ForAll`, `Action.LockAll/UnlockAll` (replaced by target text), `Action.EnrolPcs`,
`Main.Disconnect`, `Lab.SelectionHint` (shortened), `Enroll.WritePayload/BuildUsb` labels (wizard).

| New key | Українська | Русский | English |
|---|---|---|---|
| `Nav.Class` / `Nav.Programs` / `Nav.Activity` / `Nav.Lab` | Клас / Програми / Активність / Лабораторія | Класс / Программы / Активность / Лаборатория | Class / Programs / Activity / Lab |
| `Ribbon.Lesson` / `.Control` / `.Power` / `.Computers` | Урок / Іспит і інтернет / Живлення / Комп’ютери | Урок / Экзамен и интернет / Питание / Компьютеры | Lesson / Exam & internet / Power / Computers |
| `Target.All` | Увесь клас · {0} ПК | Весь класс · {0} ПК | Whole class · {0} PCs |
| `Target.Selected` | Вибрані · {0} ПК | Выбранные · {0} ПК | Selected · {0} PCs |
| `Action.Lock` (renamed from `LockScreens`) | Заблокувати | Заблокировать | Lock |
| `Action.Exam` (renamed) | Почати іспит | Начать экзамен | Start exam |
| `Action.PushBuild` → `Action.UpdateAgent` | Оновити агент | Обновить агент | Update agent |
| `Action.AddComputers` | Додати комп’ютери | Добавить компьютеры | Add computers |
| `Action.RunScript` | Запустити скрипт | Запустить скрипт | Run script |
| `Action.OpenInternet` | Відкрити інтернет | Открыть интернет | Open the internet |
| `Action.LeaveRoom` | Вийти з кімнати… | Выйти из комнаты… | Leave the room… |
| `Grid.Fit` / `Grid.Plan` / `Grid.Arrange` / `Grid.ByNumbers` / `Grid.Auto` | Вмістити всі / План кімнати / Розставити / За номерами / Авто | Вместить все / План комнаты / Расставить / По номерам / Авто | Fit all / Room plan / Arrange / By numbers / Auto |
| `Mode.Exam` | Іспит на {0} ПК · залишилося {1} | Экзамен на {0} ПК · осталось {1} | Exam on {0} PCs · {1} left |
| `Mode.Cast` | Ваш екран показується на {0} ПК | Ваш экран показывается на {0} ПК | Your screen is shown on {0} PCs |
| `Mode.Lock` | Заблоковано {0} ПК | Заблокировано {0} ПК | {0} PCs locked |
| `Feedback.Progress` | Надіслано на {0} ПК, відповіли {1} | Отправлено на {0} ПК, ответили {1} | Sent to {0} PCs, {1} answered |
| `Feedback.Partial` | {0}: {1} з {2} ПК | {0}: {1} из {2} ПК | {0}: {1} of {2} PCs |
| `Feedback.Details` / `.WakeThem` | Деталі / Увімкнути їх | Подробности / Включить их | Details / Wake them |
| `Reason.OffNotSent` / `.Queued` | вимкнений — не надіслано / не в мережі — у черзі | выключен — не отправлено / не в сети — в очереди | off — not sent / offline — queued until it links |
| `Wizard.Add.Title` + 5 step names | Додати комп’ютери: Скільки ПК · Флешка · Біля кожного ПК · Прийом ПК · Готово | … Сколько ПК · Флешка · У каждого ПК · Приём ПК · Готово | … How many · USB stick · At each PC · Accept PCs · Done |
| `Wizard.Add.Replace` | Ця флешка замінює попередні | Эта флешка заменяет предыдущие | This stick replaces earlier sticks |
| `Wizard.Add.StickNote` | На флешці немає ключа лабораторії й паролів — лише відкритий сертифікат і одноразові коди. Але поки код не використано чи не анульовано, з ним будь-який ПК може приєднатися до цієї лабораторії: тримайте флешку при собі. | На флешке нет ключа лаборатории и паролей — только открытый сертификат и одноразовые коды. Но пока код не использован или не аннулирован, с ним любой ПК может присоединиться к этой лаборатории: держите флешку при себе. | The stick carries no lab key and no passphrase — only the public certificate and single-use codes. But until a code is used or voided, it lets any PC join this lab: keep the stick with you. |
| `Wizard.Add.StickLost` | …Якщо флешку загублено, запишіть нову з позначкою «Ця флешка замінює попередні» — коди старої перестануть діяти. | …Если флешка потеряна, запишите новую с отметкой «Эта флешка заменяет предыдущие» — коды старой перестанут действовать. | …If the stick is lost, write a new one with “This stick replaces earlier sticks” ticked — the old codes stop working. |
| `Wizard.Add.RunSetup` | Запустіть Setup.exe | Запустите Setup.exe | Run Setup.exe |
| `Wizard.Add.Write` | Записати на флешку | Записать на флешку | Write to the stick |
| `Wizard.Add.KeyNeeded` | Щоб прийняти нові ПК, потрібен ключ лабораторії — один раз. | Чтобы принять новые ПК, нужен ключ лаборатории — один раз. | Accepting new PCs needs the lab key — once. |
| `Wizard.Add.Count` | У класі: {0} з {1} | В классе: {0} из {1} | In the class: {0} of {1} |
| `Legend.Title` / `Legend.Rule` | Що означають позначки / Колір рамки — найважливіший стан… | Что означают значки / Цвет рамки — самое важное состояние… | What the marks mean / The frame colour is the most important state… |
| `Tile.Badge.Sites` / `.NoNet` / `.OldAgent` / `.Certificate` / `.Problem` | Сайти / Без мережі / Старий агент / Сертифікат / Проблема | Сайты / Без сети / Старый агент / Сертификат / Проблема | Sites / No net / Old agent / Certificate / Problem |
| `Activity.Actions` / `.EventLog` / `.Summary` | Дії / Журнал подій / виконано {0} · помилок {1} · не в мережі {2} | Действия / Журнал событий / выполнено {0} · ошибок {1} · не в сети {2} | Actions / Event log / {0} done · {1} failed · {2} offline |
| `Lab.Tab.Computers` / `.Devices` / `.Key` / `.About` | Комп’ютери / Пристрої викладачів / Ключ і резервна копія / Про лабораторію | Компьютеры / Устройства преподавателей / Ключ и резервная копия / О лаборатории | Computers / Teacher devices / Key and backup / About this lab |
| `Settings.Title` | Налаштування цього комп’ютера | Настройки этого компьютера | Settings for this computer |
| Setup: `Ask.Heading` | Цей комп’ютер стане ПК класу | Этот компьютер станет ПК класса | This computer becomes a classroom PC |
| Setup: `Done.Next` | Після перезапуску ПК з’явиться в консолі викладача як {0} | После перезапуска ПК появится в консоли преподавателя как {0} | After the restart it appears in the teacher console as {0} |
| Setup: `TryAgain` | Спробувати ще | Попробовать ещё | Try again |
Terminology kept: лабораторія / лаборатория (`D-86`), ПК invariant (no plural forms needed), “ключ
лабораторії”, “резервна копія”. Ukrainian is the longest: every layout was checked in uk/ru/en at
1440×900 and 1280×720 in the prototype (no horizontal overflow in bars, dialogs, wizard steps, pages).

## 9. Accessibility
- Every icon-only button has `AutomationProperties.Name` and a `ToolTip` (with shortcut). Primary
  actions always have a visible label.
- Tiles: `AutomationProperties.Name` = “PC-07, locked, exam 23 minutes left, selected”; focusable;
  arrows/Space/Enter as in §5; focus ring 2 px `Accent` distinct from the 3 px selection outline.
- State never by colour alone: frame colour + badge icon + word (badge word hidden only below 170 px,
  then the tooltip/automation name carries it).
- Contrast ≥ 4.5 for text, ≥ 3 for UI glyphs in both themes (§1). Projector: mode colours are
  saturated and frames 2 px; the striped *off* tile survives washed-out projection.
- Motion: only the waking pulse and spinners; respect reduced motion (Avalonia `PlatformSettings`).
- Toasts are announced (`LiveSetting=Polite`; failures `Assertive`).
- Countdowns update text once per second, not per frame.

## 10. Student-PC installer and uninstaller (decision 6)

### 10.1 Pages (WinForms, one resizable-off `Form` 600×~460 DIP, `AutoScaleMode.Dpi`, Segoe UI 9/14/20)
One `SetupForm` with a header (48 px app icon, title 20 pt semibold, subtitle), a content `Panel` that
swaps page `UserControl`s, and a footer `FlowLayoutPanel` (right-to-left buttons, `AcceptButton`/
`CancelButton` set per page). No new dependency (Windows Forms only).

| Page | Content | Buttons |
|---|---|---|
| **Ask** | “This computer becomes a classroom PC” · lab name · `PC-` + `NumericUpDown` (28 pt) with the stick’s next number · ☑ *Create the student account with automatic sign-in* + one-line consequence that changes with the box · *What Setup does* (`LinkLabel` expanding 5 lines) · footer: stick version | **Install** (accept) · Cancel |
| **Repair** (existing owned installation) | “PC-07 is already installed” · number shown, read-only · same checkbox (kept from the journal) | **Repair** · Cancel |
| **Progress** | 5 groups with state glyphs (✓ done, ◌ running, › next, ✗ failed): *LabControl agent and service* (Files, Installed apps registration, Defender exclusion, Service, Service recovery, USB version upgrade, Service start) · *PC name and network* (PC name, Firewall *, Machine policy *) · *Wake-on-LAN and power* (Hibernation, Network * NIC settings, AC power *, Windows Update active hours, Wake-on-LAN address, Network readiness report) · *Student account and sign-in* (Student account, Default profile documents, Student automatic sign-in, Session policy *, Student activation, Administrator sign-in visibility; shown as skipped when the box is off) · *Lab enrolment and verification* (Enrollment, Verify installation) — a 4 px `ProgressBar`, current step in words, elapsed time; ▸ *Details* expands a read-only monospace `TextBox` with the English trace (same lines as `setup.log`) | Cancel disabled while steps run |
| **Done** | “Done — this computer is PC-07 of lab 444-2” · green note “After the restart it appears in the teacher console as PC-07 — move on to the next PC” · warnings as amber notes (other antivirus, Wake-on-LAN driver, `UsbNumberNotSaved`) · “Windows restarts in 27 s…” + countdown bar · footer left: “Next number on the stick: PC-08” | **Restart now** · Later |
| **Failure** | “Setup did not finish — nothing is broken: the finished steps are kept” · the group list with ✗ on the failing group · red note: the step and the reason in words (from the conflict detail / known messages: `PendingHostnameChange`, `UsbOlder`, `NotEnoughDisk`, `EnrollmentDiffers`, `StudentNotOwned`, …) · “Try again continues from this step” · log path link (opens Explorer) · *Details* | Copy details · **Try again** (re-runs the same idempotent pipeline = repair) · Close |
| **Uninstall: confirm** | “Remove LabControl from this PC — no console or stick needed” · info note (settings restored unless edited later; personal accounts and files stay) · ◉ *Keep the student account* (default) / ○ *Delete student and its whole profile* (shown only when the journal proves ownership; second confirm stays) | **Remove** · Cancel |
| **Uninstall: progress** | 4 groups: Stop the agent service · Restore Windows settings · Account and sign-in · Remove LabControl files; details trace | — |
| **Uninstall: result** | Removed (green) / `RestorationPending` / `RestoreConflict` lines as amber notes (“1 setting was edited later and left as it is”) / `CleanupIncomplete` as red with “Retry from Installed apps” · “Restart to remove the temporary cleanup file” | Restart now · Close |
| *Rekey* | no page (runs as a SYSTEM task from the console, output to console/log only) | — |

The group of a step is a static map from `ISetupStep.Name` prefixes (the names above), so the
pipeline (`SetupCoordinator`, `SetupPipeline.Run`) does not change; its `Report` callback feeds both
`setup.log`/stdout and the form (marshalled with `Control.BeginInvoke`).

### 10.2 No console window, while the command line still prints

**Primary approach: `OutputType WinExe`, presentation decided before any output.**
1. `Main` first classifies the launch — before the version line or anything else is written:
   **CLI** when any of `--number`, `--dry-run`, `--no-reboot`, `--rekey`, `--payload`, `--help`,
   `--remove-student`/`--confirm-remove-student`, `--create-student`/`--no-student` is present, or
   stdout/stderr are redirected (`GetFileType` of the inherited handles is a file or pipe), or the
   process runs in session 0 (scheduled task, SYSTEM `run_script`); **background** (no window, no
   console, log only) for `--finish-uninstall`; otherwise **GUI**. A bare `--uninstall` is GUI, because
   Installed apps launches `Uninstall.exe --uninstall` (`WindowsUninstallEntry.UninstallString`);
   `--uninstall` from a prompt with redirected output, or together with a CLI switch, is CLI.
2. **GUI** (an Explorer double-click of `Setup.exe` from the stick, or of the installed `Uninstall.exe`,
   including after UAC elevation): no console is ever created; `SetupForm` runs on the STA main thread,
   the pipeline on a worker **STA** thread (COM: `HNetCfg.FwPolicy2`, WMI) and reports through
   `Control.BeginInvoke`; `setup.log` is written as today. `Uninstall.exe` from Explorer shows the
   uninstall pages.
3. **CLI**: if the standard handles were inherited redirected (pipe/file), use them as they are; else
   `AttachConsole(ATTACH_PARENT_PROCESS)` and reopen `Console.Out/Error` on it; only when there is no
   parent console (e.g. a CLI flag from a shortcut) `AllocConsole()`. Output text and the trace are
   unchanged (English, as today).
4. **Exit codes stay exactly as today** (0 / 1 / 2, `UninstallCleanup.Finish`), returned from `Main`.
5. Caveat: `cmd.exe` does not wait for a WinExe — interactive use from a prompt needs
   `start /wait Setup.exe --number 7` (and `echo %ERRORLEVEL%`); PowerShell `& .\Setup.exe` /
   `Start-Process -Wait`, scheduled tasks and the console’s SYSTEM `--rekey` task wait normally and get
   the exit code. Document this in `docs/INSTALLER.md` and the `--help` text; `scripts/dev-install.*`
   must use the waiting forms.
6. Attached-console output can interleave with the parent prompt; print a leading newline and finish
   with the result line so the prompt reappears cleanly.

**Alternative (kept): console subsystem + `FreeConsole`.** Keep `OutputType Exe`; in GUI conditions,
where `GetConsoleProcessList` returns 1 (the console belongs to this process only), hide the window and
`FreeConsole()` before showing the form. CLI behaviour, including `cmd` waiting, stays exactly as
today, but a brief console flash on double-click cannot be avoided.

**In both:** clear `ENABLE_QUICK_EDIT_MODE` with `SetConsoleMode` whenever a console is used, so a click
can never freeze Setup again (the reported “press Enter” bug).

### 10.3 Languages
Setup already resolves the Windows display language (`ProductLanguages.Resolve`) — the form uses the
same `SetupStrings` (+ new keys for headings, groups, Try again, Copy details); the trace and
`setup.log` stay English (`D-87`).

## 11. Avalonia implementation notes
- **Icons**: `build/icons.json` → generate `Assets/Icons.axaml` (`StreamGeometry` per name) with a
  small script in `tools/` (Python, like `make-icon.py`); student overlay draws the lock/timer/exam
  glyphs with `SKPath.ParseSvgPathData` on the same strings. Licence: drawn for this project (MIT, the
  repository licence); Lucide-compatible style, so Lucide (ISC) additions stay consistent.
- **Ribbon**: a `TabControl` with `ItemsPanel` of `RibbonGroup` (`StackPanel`) — no third-party ribbon.
- **Compact mode** via a `Classes.compact` binding on `Bounds.Height < 800`.
- **Container-width rules** (the prototype uses CSS container queries) → `Bounds.Width` thresholds in
  code-behind of the mosaic toolbar.
- **Mode bars** bind to existing aggregates (`AnyInExam`, `IsBroadcasting`, locked count from tiles).
- **Toasts**: `ActionFeedbackService` in `Services/` subscribing to `JobHistory.Updated` and the tiles’
  policy state for a batch id; view model `ToastViewModel`.
- **Full view** stays a separate `ScreenWindow`; new toolbar template only.
- Screenshots for the README: extend `LABCONTROL_UI_SHOTS` renders to the new screens (both themes,
  three languages).

## 12. Risks
1. **Ribbon height on 1280×800 with the Dock** — mitigated by compact mode; verify on the real MacBook.
2. **Feedback for policies** needs a “who answered” watcher; a PC that never answers must time out
   into “no answer from PC-07” rather than spin forever.
3. **Avalonia stroke icons**: `PathIcon` is fill-only — use LcIcon; check crispness at 16 px on Windows
   125 %/150 % scaling.
4. **Setup GUI threading**: the pipeline today runs on the main thread with console output; moving
   it to a worker must keep COM apartment and exception handling; the failure page must never hide a
   non-zero exit code.
5. **Setup presentation**: **spike both 10.2 approaches on the Windows VM** — Explorer double-click
   with UAC, `Uninstall.exe` from Installed apps, `cmd` / PowerShell / scheduled-task (`--rekey`) /
   redirected runs, exit codes, session 0 — before choosing; WinExe changes `cmd` waiting, FreeConsole
   flashes a window.
6. **Very small windows**: at the 720×480 minimum, 30 PCs are 49 px tiles — they fit, but are only
   good for state colours; full view and the tooltip carry the rest.
7. **Room-plan persistence** of mode/size per lab is a new small file; keep it out of backups.
8. **Scope**: many screens; split as below so each portion is reviewable and renderable.
9. **Translations**: ~180 new keys × 3 languages; owner read-through needed.

## 13. Implementation portions

| # | Portion | Touches |
|---|---|---|
| 7a | Tokens, LcIcon + generated geometries, restyled Button/Dialog base, light/dark check | `Styles/Tokens.axaml`, `Assets/Icons.axaml`, `tools/icons.py`, `App.axaml`, `Dialogs.cs` |
| 7b | Main window shell: top bar, lab switcher, key chip, sections, status bar, banners; Settings split (Lab › … / Settings) | `MainWindow.axaml`, `MainViewModel`, `SettingsView*` → `Lab*View`, `SettingsViewModel` |
| 7c | Class: ribbon + target chip + merged pair commands, mode bars/chip strip, tile template, legend, context menu, selection (rubber band, keyboard) | `LabView.axaml(.cs)`, `MachineTileViewModel` (automation name, frame priority), `MainViewModel` |
| 7d | Dynamic grid: fit/plan/size, persistence per lab | `MainViewModel.Reflow`, `LabView`, new `ViewPreferences` |
| 7e | Feedback: toasts, Activity (Actions + Event log), batch export | `Services/ActionFeedbackService`, `JobsView`/`EventsView` → `ActivityView` |
| 7f | Add computers wizard (+ first-run Done → wizard; empty state) | new `AddComputersWizard`, `SettingsViewModel` enrolment commands, `SetupViewModel` |
| 7g | Dialogs restyled (lock, internet, exam four switches, extend, send, collect, reset, departure, unlock, move key, push), full view toolbar | `Views/*Dialog*.cs`, `ScreenWindow.axaml` |
| 7h | Programs, Scripts, My labs, first run | `PackagesView`, `AddPackageDialog`, `ScriptsView`, `LabChooserWindow`, `SetupWindow` |
| 7i | Windows Setup/Uninstall GUI (§10) — VM drill | `LabControl.Setup/Program.cs`, `SetupDialog.cs` → `SetupForm`, `SetupStrings*.resx`, `docs/INSTALLER.md` |
| 7j | App icon + document icons (`make-icon.py`), student overlay glyphs, three-language renders, docs (ROADMAP, DECISIONS `D-NN` for ribbon/grid/feedback/Setup GUI, html rebuild) | `tools/make-icon.py`, `Assets/`, `Shared/Policy/OverlayText.cs`, docs |
