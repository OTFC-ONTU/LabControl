# LabControl console redesign — claude-opus concept

**Open by double-click** (no network, no server):

| File | What |
|---|---|
| `prototype/index.html` | the clickable prototype. The dark strip on top switches **Window** (Console · My labs · First run · Student PC · PC installer), **Variant** A (the owner’s choice, default) / B (reference), theme, language uk/ru/en, PC count 1/8/14/30, state (All states · Lesson · New lab), window size (1440×900 · 1280×720 · Resizable — drag the corner to see the grid refit live) and role (Admin · Teacher). |
| `icons/index.html` | the 98 action icons with meanings (`icons/*.svg`, 24×24, 2 px stroke) |
| `icons/mapping.md` | every console command and all 857 `Strings.resx` keys → icon and new location |
| `app-icon/index.html` | three app-icon concepts at 16–512 px, light/dark, Dock and taskbar; document icons |
| `analysis.md` | audit with file references, scenarios, information architecture, variants, changes from ANALYSIS.md, proposals |
| `spec.md` | Avalonia handoff: tokens with contrast, components, tile, grid algorithm + worked examples, shortcuts, old→new, keys in 3 languages, accessibility, Setup/Uninstall, risks, portions |
| `build/` | sources: `python3 build/build.py` regenerates the prototype, icons and app-icon sheet; `build/mapping.py` the mapping |

Things to try in the prototype: click / ⌘-click / drag a box over tiles; right-click a tile; double-click
a tile (full view → *Control*); ribbon *Lesson → Lock*, *Computers → Reset profile* (partial failure
toast → *Details*); *Exam & internet → Start exam* (four switches, mode bar countdown); *Fit all ↔ Room
plan → Arrange* (drag); State **New lab** → *Add computers* (unlock the key in step 4 and watch the PCs
arrive); **PC installer** → *Install* (progress → done), *Failure*, *Uninstall*.

## The concept in one paragraph

**The class is the home.** The mosaic of the room never leaves the screen; every action sits in a
labelled ribbon grouped by the moment of the lesson — *Lesson · Exam & internet · Power · Computers* —
and says whom it acts on (“Whole class · 14 PCs” until you select some). The X / “X for all” pairs are
gone. Each classroom mode has one colour used everywhere — amber lock, blue “my screen”, teal exam,
violet internet, red problem — as the tile frame, the badge, the mode bar with its countdown and
controls, and the dialog icon; tiles show only what the PC reports. Tiles fit the window by
themselves (or stand as in the room), and every action answers with a toast that names the PCs that
did not do it and why, linked to *Activity*. Adding computers is one wizard — how many → write the
stick → what to do at each PC → a live waiting room that unlocks the key in place and explains why a
PC does not appear → done — and the PC side is a real Windows installer with one question, plain
progress, *Done: it appears as PC-07* or *Try again*, and no console window. Administration lives in
one *Lab* section that a teacher-access console simply does not show.

## Recommendation
- **Variant A — ribbon over the class: the owner’s choice (2026-10-29).** It is the prototype’s default;
  variant B stays in the prototype only as a reference. Compact ribbon below 800 px height; several
  modes share one chip line, banners one line; every PC always fits the window with no scrolling
  (30 PCs at 1280×720 with all states: 8×4 at 132 px).
- **App icon concept 1 — “Lit screen”**: four screens of the class, one amber under the teacher’s
  pointer; a dedicated small master keeps it readable at 16 px; matching `.lcbak/.lclab/.lcreq/.lcgrant`
  document icons.
- **Setup**: `WinExe` that decides presentation before any output — an Explorer launch (Setup.exe,
  Uninstall.exe) never gets a console; explicit CLI keeps redirected handles or attaches to the parent
  console; exit codes unchanged; the console-subsystem + `FreeConsole` variant kept as the alternative,
  both to be spiked on the Windows VM (spec §10.2).
