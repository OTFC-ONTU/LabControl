# LabControl console redesign — analysis (M7 portion 7, claude-opus)

Concept: **“The class is the home”** — one always-visible mosaic, actions that say whom they act
on, one colour per classroom mode, and an answer on screen for every action.

## 1. Audit — what is wrong today

File references are to `src/LabControl.Console/` unless noted.

### Main window and mosaic
| # | Problem | Where |
|---|---|---|
| 1 | 27 equal-weight text buttons in one `WrapPanel`; 3–4 lines on a 13" laptop in Ukrainian. Danger (Shut down, Remove, Reset profile) sits beside Select all. | `Views/MainWindow.axaml` toolbar |
| 2 | Pairs *X* / *X for all* (Lock, Unlock, Internet, Exam, Show my screen) double the buttons although the selection model already exists. | same; `MainViewModel` `LockScreens`/`LockAllScreens` etc. |
| 3 | Tabs sit **below** the toolbar, so PC actions stay on *Scripts*, *Settings*, *Jobs* where they mean nothing; leaving *Lab* hides the class. | `MainWindow.axaml` `TabControl` |
| 4 | The script quick-runner (combo + run + four text lines) takes prime space above the mosaic. | `MainWindow.axaml`, Lab tab header |
| 5 | The status bar mixes the useful count with port, instance and kbit/s. | `Main.Status`, `MainViewModel.RefreshStatusLine` |
| 6 | `Reflow()` sizes tiles from the **saved layout’s column count** (at least 6) with width clamped 96–240 px: 14 PCs on a wide window stay small, 30 PCs scroll. No fit-to-window, no size control. | `MainViewModel.Reflow` (l. 525) |
| 7 | Tile state = up to **8 orange text lines** over the picture + 4 badges in two corners + a dot with 4 unexplained colours. Hard-coded `IndianRed`, `DarkOrange`, `#CC8A2B00`, `#CC1F5FAF`, `#1F5FAF`; no dark-mode check. | `Views/LabView.axaml`, `MainWindow.axaml` |
| 8 | Selection is a 2 px accent border — invisible among 14 busy pictures. | `Border.tile.selected` |
| 9 | Discoverability by a hint sentence (⌘-click, drag, double-click). | `Lab.SelectionHint` |
| 10 | The tile context menu has power and remove only; no *Control*, *Lock this PC*, *Show my screen here*, *Send files*. | `LabView.axaml.cs BuildMenu` |
| 11 | A failed or refused action answers nowhere the teacher looks: `Reset.Sent` says “the jobs panel shows each PC’s answer”. | `Reset.Sent`, `Files.Queued`, `Lock.Sent` |

### Adding PCs (the owner’s main concern)
| # | Problem | Where |
|---|---|---|
| 12 | The path is spread over three places in jargon: *Settings → Enrolment → “PCs on this stick” → “Write USB payload…” vs “Build USB installer…”* (difference unexplained) → *“Void the unused code(s) of earlier sticks”* → walk to the PCs → back to the toolbar **“Enrol PCs…”**, which actually unlocks the key and then turns into *Lock*. | `SettingsView.axaml` Enrolment; `Action.EnrolPcs`; `Lab.Empty`; `Setup.DoneBody` |
| 13 | “Build USB installer” asks for a *published Windows build folder* — a developer concept in the teacher’s main flow. | `Enroll.BuildFolder` |
| 14 | No progress: which numbers arrived, which codes are unused, what to do when a PC does not show up. | — |
| 15 | On the PC, Setup is a console-subsystem exe: a black window with a ~40-line step trace, a green line, and a freeze when the teacher clicks into it (QuickEdit) until Enter. Failures say “review the console message”. | `LabControl.Setup/Program.cs`, `SetupDialog.cs`, `SetupStrings.resx` `Failed`/`OperationFailed` |

### Other screens
| # | Problem | Where |
|---|---|---|
| 16 | *Settings* is one long scroll mixing lab identity, ids, data directory, language, key, holders, backup, teacher devices, enrolment and codes; admin parts vanish for a teacher with a hint line. | `Views/SettingsView.axaml` |
| 17 | *Jobs* and *Events* are two raw tables; the question “did my action work?” needs a per-action summary. | `JobsView.axaml`, `EventsView.axaml` |
| 18 | Full view: a text toggle *Control*, text *Ctrl+Alt+Del* / *Win*, two combos; no visible “you are controlling” state. | `ScreenWindow.axaml` |
| 19 | *My labs*: a wrapping row of 8–9 equal buttons whose meaning depends on the selected row. | `LabChooserWindow.axaml` |
| 20 | First-run *Done* says “Settings → Write USB payload … press Enrol PCs” — pointing into the confusing flow. | `Setup.DoneBody` |
| 21 | App icon (white L + cyan tiles) reads as a layout app; tiles merge at 16 px. No document-type icons. | `Assets/labcontrol.png`, `tools/make-icon.py` |
| 22 | Student lock screen and exam strip are good (dark navy, large type); only the lock glyph is missing and *Time is up* looks like an ordinary lock. | `Shared/Policy/OverlayText.cs` |

## 2. User scenarios

**Before a lesson (2 min).** Open the MacBook → the console opens the last lab (*My labs* only when
there are several). The pill shows `9/14 online`; ribbon *Power* → **Wake** (target chip “Whole class ·
14 PCs”; only off PCs get packets) → striped tiles turn into pulsing *Waking…* → live pictures.
*Lesson* → **Send files** (PDF, open after delivery) → toast “Send files: 13 of 14 · PC-07 off — not
sent · Details · Wake them”.

**During a lesson.** The mosaic is always there. PC-06 has a red frame; its tooltip / side panel says
“the session helper is not running… log off or a restart usually helps”. **Show my screen ▾ → Video**
for an animation; mode bar “Your screen is shown on 14 PCs · Text|Video · Stop showing”. **Lock** (⌘L)
for attention; amber frames, mode bar “14 PCs locked · Unlock all”. Double-click a tile → full view →
**Control** → help → *Back to class*.

**A test.** *Exam & internet* → **Start exam**: four switches (timer 45 min, allowed programs, internet
= preset sites, collect `Desktop\Exam` at the end). Teal frames with countdown; mode bar with `23:41`,
`+10 min`, *Extend…*, *End exam*. A PC that comes online later joins. *End exam* → “Exam ended on 14
PCs” + “Work collected: 13 of 14 · PC-07 was off · Open folder”.

**After a lesson.** *Computers* → **Reset profile** (light) → partial toast “9 of 14 · PC-04 refused:
an exam is running”. *Power* → **Shut down** (confirm). Lab switcher → **Leave the room…** → departure
dialog lists exactly what stays (locks until …, exam ends by itself at …, uncollected work) with
*Unlock, open the internet and leave*.

**First run (a new room).** Stepper: create the lab → recovery code → backup → *Done* card whose
primary button is **Add computers**.

**Adding PCs.** Wizard: how many (numbers PC-01…PC-14 + 2 spare codes; “This stick replaces earlier
sticks”) → pick the stick, **Write to the stick** (Windows files found next to the console; data-only
under *For development*; an honest note: no lab key or passphrase on the stick, but its unused
single-use codes let a PC join this lab until used or voided — if it is lost, write a new one with
“This stick replaces earlier sticks”) → four illustrated steps at each PC (run `Setup.exe`) +
*Print instructions* (INSTALL.txt) →
**live waiting room**: the key unlock right there with its reason, a chip per number (waiting /
connecting / in the class / problem with the reason), a five-point checklist when a PC does not
appear → *Done*: “13 of 14 in the class”, *Arrange like the room*, *Export a backup* (stale after
writing the stick).

**At each PC (Setup).** One window: “This computer becomes a classroom PC”, a large `PC-07` with the
stick’s next number, the account box ticked → progress in five plain groups with elapsed time and
*Details* → *Done: it appears in the console as PC-07* + 30 s restart countdown; or *Setup did not
finish* with the step, the reason in words, **Try again** (= Repair) and the log path. Warnings (other
antivirus, Wake-on-LAN driver) are amber notes on *Done*, never errors.

**Admin chores (a few times a year).** *Lab › Key and backup* (unlock/lock, remember/forget, holders,
recovery code, backup, *Move to a new key* in a danger card); *Lab › Teacher devices* (authorize
requests, withdraw, lab file); *Lab › Computers* (table with agent version and warnings, update agent,
remove/revoke, enrolment codes).

**Something failed.** Every action ends in a toast: progress (“sent to 14, 9 answered”) → result. A
partial failure stays until dismissed and lists up to four PCs with reasons; **Details** opens
*Activity* on that action’s card with per-PC rows and *Logs of this action (ZIP)*. The bell carries a
dot while a failure is unread.

## 3. Information architecture

| Section | Contains | Comes from |
|---|---|---|
| **Class** (home) | mosaic, actions (ribbon A / command bar B), mode bars, legend, full view, every lesson dialog | Lab tab + toolbar + script quick-runner |
| **Programs** | catalog, per-PC status grid, *Add program* wizard (with a test install), install buttons | Packages tab |
| **Scripts** | library, editor, run on the target | Scripts tab |
| **Activity** | *Actions* (one card per batch: done / failed / offline, per-PC rows, export) and *Event log* | Jobs + Events tabs |
| **Lab** (administration) | *Computers* (add, update agent, remove, revoke, codes), *Teacher devices*, *Key and backup*, *About this lab* (ids, endpoint, data directory, version, LAN access, other consoles) | Settings (lab parts), toolbar *Push agent build*, *Enrol PCs* |
| **Settings** (this computer, gear) | language, theme (proposal), default Text/Video, mosaic mode/size, LAN access | Settings (machine parts) |
| Lab switcher (top-left) | lab name + role, *My labs…*, *Leave the room…* | `⟵ Disconnect` + lab chip |
| Key chip (admin only) | locked / open until hh:mm / remembered; unlock, lock, remember, forget | toolbar *Enrol PCs* / *Lock* |

Teacher access sees *Lab › About this lab* with one line explaining what the administrator does; no
admin-only control is rendered at all (not disabled — absent), the key chip is absent, *Add computers*
in the empty state is replaced by “the administrator adds computers”.

## 4. Navigation variants

### A — Ribbon over the class (the owner’s idea), refined
Top bar: logo · lab switcher · sections (Class, Programs, Scripts, Activity, Lab) · online pill · key
chip · bell · gear. On *Class* a ribbon with four tabs — **Lesson · Exam & internet · Power ·
Computers** — of 3–7 large icon+label buttons, preceded by a **target chip** (“Whole class · 14 PCs” /
“Selected · 3 PCs ✕”). A dot on a ribbon tab marks a mode running there. Below 800 px window height
the ribbon goes **compact** (icon left of label, 46 px) by itself.

- \+ Every action is labelled and visible within one tab; the tabs teach the product (lesson / exam /
  power / computers) and become the chapters of the README manual (portion 8).
- \+ Danger is grouped and separated at the end of *Power* / *Computers*.
- \+ Full window width for the mosaic.
- − Switching ribbon tabs mid-lesson costs a click (mitigated: *Lesson* is the default; mode bars
  carry the running mode’s controls; the tile context menu has every per-PC action).
- − ~114 px of height (80 px compact).

### B — Sidebar + command bar + selection panel
Left sidebar (212 px; a 90 px labelled rail below 1360 px width); on *Class* a 54 px command bar —
**Show my screen ▾, Lock/Unlock (toggles), Send files, Start exam, Internet, Power ▾, More ▾** — and a
310 px **side panel** while PCs are selected: big picture, the PC’s states in words, every per-PC
action, details.

- \+ Hot actions one click away; per-PC diagnosis in words without hovering.
- \+ Height-efficient on tall windows.
- − *More ▾* and *Power ▾* hide a third of the functions; the panel steals 310 px exactly when the
  teacher has selected PCs to act on (14 PCs: 264 → 216 px tiles).
- − A sidebar is one more navigation surface.

Measured in the first round: 14 PCs — A 275 px / B 264 px at 1440×900; A 224 px / B 225 px at
1280×720. The ribbon costs nothing once compact.

### Recommendation: **A** — chosen by the owner on 2026-10-29
B stays in the prototype only as a reference.
It meets the owner’s stated goal — *obvious how to use* — because nothing hides behind *More*, every
button names what it does and to whom, and the ribbon tabs double as a map of the product. Its height
cost is absorbed by the compact ribbon and the chip strip that merges several mode bars. Borrow from B
the per-PC words (in A they are the tile tooltip and the full tile context menu) and keep B’s
selection panel as an optional later addition (proposal) if the owner misses it.

## 5. Dynamic grid (algorithm and numbers in spec §4)
Owner rule: **all PCs always fit, nothing scrolls**. *Fit all* (default): the column count that
maximises tile width so that all N tiles (16:9 picture + caption) fit, recomputed on every resize,
sorted by PC number; maximum 560 px and **no minimum** — below 150 px the tile switches to a dense
caption (number + dot, 6 px gaps) and the full view is one double-click away. *Room plan*: the saved
`lab.json` cells, their bounding box scaled to fit and centred; *Arrange* shows the cell grid (one
spare row and column inside the fitted area) and grips, a drop swaps (today’s behaviour), *By numbers*
resets. The size slider only shrinks tiles to 65–100 % of the fit, so it can never cause scrolling;
remembered per lab on this computer. To keep the height for the mosaic, several modes share **one**
chip line (overflow into “+N”), several banners share one line (“+N”, or only the bell below 600 px),
and ribbon buttons that do not fit go into *More*. 30 PCs at 1280×720 with every state shown: 8×4 at
132 px. Rubber-band selection on the empty mosaic.

## 6. What I changed from `ANALYSIS.md`, and why
1. **Recommend variant A**, not a sidebar: the measurements show the ribbon costs no tile size at
   1280×720 once compact, and it keeps every function labelled.
2. **Ribbon tabs** are *Lesson · Exam & internet · Power · Computers*; *Collect work* appears in both
   Lesson and Exam (both moments use it); *Open screen* and *Run script ▾* (the old quick-runner) are in
   Lesson.
3. **Several mode bars collapse into one chip line** (two or more modes, overflow “+N”), banners into
   one line, and the grid never scrolls (owner rule) — a busy lesson keeps the whole class visible.
4. **Tile language**: one coloured frame = the most important state (problem › lock › showing › exam ›
   internet) plus icon badges; *off* is a striped tile, not 60 % opacity, so it survives a projector
   and dark mode; selection = 3 px accent outline + a filled check circle.
5. **Activity keeps an Event log tab** instead of merging events into action cards, because machine
   events (helper down, rollback) are not answers to an action.
6. **Settings split**: everything lab-wide goes to *Lab › …*; *Settings* holds per-computer
   preferences only.
7. **No teacher’s-desk marker** in room plan: it needs a new `lab.json` field — listed as a proposal.
8. **Setup on the PC** redesigned (decision 6): five plain groups instead of ~40 trace lines, a large
   `PC-07`, *Try again* = Repair, warnings amber, and no console window (spec §10).
9. **App icon**: “lit screen under a pointer”, with a separate small master for 16–32 px.

## 7. Proposals beyond the roadmap
- **Theme choice** (system / light / dark) in *Settings* — in the prototype with a “proposal” tag.
- **Teacher’s desk marker** in room plan (optional desk cell in the `lab.json` layout).
- **Retry the failed ones** on an Activity card (re-queues the same job for failed PCs only).
- **Selection side panel** as an optional toggle in variant A.
- **Quick +10 min** on the exam mode bar (a preset of the existing *Extend*).
- **Copy details** on the Setup failure page (the trace to the clipboard).
