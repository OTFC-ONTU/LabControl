#!/usr/bin/env python3
"""Generates ../icons/mapping.md: every console command and every Strings.resx key -> icon and new location."""
import os, re, xml.etree.ElementTree as ET
H = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(H, '..', '..', '..', '..'))
RESX = os.path.join(REPO, 'src', 'LabControl.Console', 'Localization', 'Strings.resx')
root = ET.parse(RESX).getroot()
KEYS = [(d.get('name'), (d.find('value').text or '').replace('\n', ' ').replace('|', '/')) for d in root.findall('data')]

# Location shorthands
CL = 'Class'; RL = 'Class › ribbon'; RLL = RL + ' › Lesson'; RLC = RL + ' › Exam & internet'; RLP = RL + ' › Power'; RLPC = RL + ' › Computers'
CTX = 'tile context menu + B side panel'; MB = 'mode bar'
ADM = 'Lab › '; KEY = ADM + 'Key and backup'; PCS = ADM + 'Computers'; DEV = ADM + 'Teacher devices'; ABOUT = ADM + 'About this lab'
TOAST = 'feedback toast + Activity'

# (command, old place, new place A, new place B, icon)
COMMANDS = [
    ('WakeCommand', 'toolbar', RLP + '; ' + CTX, 'Power ▾; side panel', 'wake'),
    ('ShutdownCommand', 'toolbar, tile menu', RLP + ' (danger, confirm); ' + CTX, 'Power ▾', 'power'),
    ('RebootCommand', 'toolbar, tile menu', RLP + '; ' + CTX, 'Power ▾', 'reboot'),
    ('LogoffCommand', 'toolbar, tile menu', RLP + '; ' + CTX, 'Power ▾', 'logoff'),
    ('SendFilesCommand', 'toolbar', RLL + '; ' + CTX, 'command bar', 'send-files'),
    ('CollectFilesCommand', 'toolbar', RLL + ' and ' + RLC + '; ' + CTX, 'More ▾', 'collect'),
    ('ResetProfileCommand', 'toolbar', RLPC + ' (danger); ' + CTX, 'More ▾', 'reset-user'),
    ('LockScreensCommand / LockAllScreensCommand', 'toolbar (2 buttons)', RLL + ' “Lock” — acts on selection or whole class; ⌘L', 'command bar (toggles with Unlock)', 'lock'),
    ('UnlockScreensCommand / UnlockAllScreensCommand', 'toolbar (2 buttons)', RLL + ' “Unlock”; lock ' + MB + ' “Unlock all”', 'command bar; mode bar', 'unlock'),
    ('InternetCommand / InternetForAllCommand', 'toolbar (2 buttons)', RLC + ' “Internet…”; internet ' + MB, 'command bar', 'globe'),
    ('(lift = Internet dialog “Open”)', 'Internet dialog', RLC + ' “Open the internet”; ' + MB + ' “Open for all”', 'More ▾; mode bar', 'globe'),
    ('ExamCommand / ExamForAllCommand', 'toolbar (2 buttons)', RLC + ' “Start exam” (selection or whole lab)', 'command bar', 'exam'),
    ('ExtendExamCommand', 'toolbar (while exam)', RLC + '; exam ' + MB + ' “+10 min” and “Extend…”', 'mode bar; More ▾', 'timer-plus'),
    ('EndExamCommand', 'toolbar (while exam)', RLC + '; exam ' + MB, 'mode bar', 'flag'),
    ('ShowScreenCommand / ShowScreenToAllCommand', 'toolbar (2 buttons)', RLL + ' split button (Text / Video menu); ' + CTX + ' “Show my screen here”', 'command bar split button', 'cast'),
    ('StopShowingScreenCommand', 'toolbar (while showing)', 'same ribbon button turns into “Stop showing”; cast ' + MB, 'command bar; mode bar', 'cast-off'),
    ('SelectedBroadcastMode (Text/Video combo)', 'toolbar combo', 'Show-my-screen menu; cast ' + MB + ' segmented Text|Video; Settings default', 'same', 'text-mode / video-mode'),
    ('PushBuildCommand', 'toolbar (admin)', RLPC + ' “Update agent” (admin); ' + PCS, 'More ▾ (admin)', 'update'),
    ('RemoveSelectedCommand / RemoveMachineCommand', 'toolbar, tile menu', RLPC + ' (admin, needs selection); ' + CTX + '; ' + PCS + ' row ⋯', 'More ▾; side panel', 'trash'),
    ('RevokeMachineCommand', 'tile menu', CTX + ' (admin); ' + PCS + ' row ⋯', 'same', 'ban'),
    ('SelectAllCommand', 'toolbar', 'mosaic toolbar checkbox; ⌘/Ctrl+A', 'same', 'select-all'),
    ('ClearSelectionCommand', 'toolbar', 'target chip ✕; Esc; click empty mosaic', 'same', 'select-none'),
    ('OpenScreenCommand', 'double-click, tile menu', 'double-click / Enter; ' + RLL + ' “Open screen”; ' + CTX, 'side panel', 'view'),
    ('UnlockCommand (“Enrol PCs…”)', 'toolbar (admin)', 'key chip in top bar; Add computers wizard step 4; ' + KEY, 'sidebar key chip', 'key'),
    ('LockCommand (key)', 'toolbar / Settings', 'key chip menu “Lock the key now”; ' + KEY, 'same', 'lock'),
    ('TakeOverCommand', 'banner', 'banner “Take over”', 'same', 'swap'),
    ('DisconnectCommand', '“⟵ Disconnect” button', 'lab switcher ▾ › “Leave the room…” (departure dialog)', 'same', 'leave'),
    ('ExportBatchLogsCommand', 'Jobs tab', 'Activity › Actions › “Export logs…” and per action “Logs of this action (ZIP)…”', 'same', 'download'),
    ('Scripts quick-runner (Run on N PCs)', 'above the mosaic', RLL + ' “Run script ▾”', 'More ▾', 'play'),
    ('ScriptsViewModel: New/Save/Revert/Delete/AddBuiltIns/Run', 'Scripts tab', 'Scripts section (unchanged functions)', 'same', 'plus/save/undo/trash/play'),
    ('PackagesViewModel: Add/Save/Revert/Delete/InstallSelected/InstallAllMissing/CheckPcs', 'Packages tab', 'Programs section; ' + RLPC + ' “Install program”', 'Programs', 'programs/install/refresh'),
    ('SettingsViewModel: Unlock/Lock/RememberKey/ForgetKey', 'Settings', KEY + '; key chip menu', 'same', 'key/remember/forget'),
    ('SettingsViewModel: MoveToNewKey', 'Settings', KEY + ' › danger card', 'same', 'key-move'),
    ('SettingsViewModel: AddHolder/RemoveHolder', 'Settings', KEY + ' › Key holders', 'same', 'holders'),
    ('SettingsViewModel: ReprintRecoveryCode', 'Settings', KEY + ' › Recovery code', 'same', 'lifebuoy'),
    ('SettingsViewModel: ExportBackup', 'Settings, banner', KEY + '; banner; wizard “Done”; first run', 'same', 'backup/download'),
    ('SettingsViewModel: AuthorizeRequests/WithdrawDevice', 'Settings', DEV, 'same', 'approve/ban'),
    ('SettingsViewModel: ExportLabFile', 'Settings', DEV + ' “Lab file for teachers…”', 'same', 'lab-file'),
    ('SettingsViewModel: ActivateDormantCodes', 'Settings', PCS + ' › Enrolment codes', 'same', 'ticket'),
    ('SettingsViewModel: BuildUsbInstaller / WritePayload', 'Settings › Enrolment (2 buttons)', 'Add computers wizard step 2 (“Write to the stick”; data-only under “For development”)', 'same', 'usb'),
    ('Settings: other teacher machines › Revoke instance', 'Settings', ABOUT + ' › Other consoles; ' + DEV, 'same', 'ban'),
    ('Settings: Language selector', 'Settings, chooser', 'Settings (this computer) › Language; My labs', 'same', 'language'),
    ('ScreenWindow: Control toggle', 'full view', 'full view “Control” toggle (also from ' + CTX + ')', 'same', 'control'),
    ('ScreenWindow: SendCtrlAltDel / SendWindowsKey', 'full view', 'full view toolbar', 'same', 'keyboard / win-key'),
    ('ScreenWindow: Quality / Text-Video', 'full view combos', 'full view segmented Text|Video + Quality ▾', 'same', 'quality / text-mode / video-mode'),
    ('LabChooser: Open/Retry/AddLabs/CreateLab/Authorize/AuthorizeAll/RenewCertificate/Remove/Quit', 'My labs (button row)', 'My labs: primary button per card status; ⋯ per card (renew cert, remove); top: Create, Add from file, Authorize requests, Request access for all; Quit at the bottom', 'same', 'labs/plus/upload/approve/certificate/trash/power'),
    ('SetupViewModel: ChooseCreate/ChooseImport/Create/AcknowledgeRecoveryCode/ExportBackup/PickBackup/Import/Back', 'first-run window', 'First run stepper (5 steps); last step → Add computers wizard', 'same', 'plus/upload/lifebuoy/backup'),
    ('Network banner: Allow…/Check again', 'banner', 'banner + ' + ABOUT + ' › LAN access; wizard step 4 checklist', 'same', 'network/refresh'),
]

RULES = [  # (regex on key, new location, icon or '')
    (r'^Action\.Wake$', RLP, 'wake'), (r'^Action\.Shutdown$', RLP, 'power'), (r'^Action\.Reboot$', RLP, 'reboot'), (r'^Action\.Logoff$', RLP, 'logoff'),
    (r'^Action\.PushBuild$', RLPC + ' (admin) — renamed “Update agent”', 'update'), (r'^Action\.OpenScreen$', RLL + ' “Open screen”; ' + CTX, 'view'),
    (r'^Action\.SelectAll$', 'mosaic toolbar', 'select-all'), (r'^Action\.ClearSelection$', 'target chip ✕', 'select-none'),
    (r'^Action\.EnrolPcs$', 'removed: key chip + Add computers wizard', 'key'), (r'^Action\.RemoveSelected$', RLPC + '; ' + CTX, 'trash'),
    (r'^Action\.SendFiles$', RLL, 'send-files'), (r'^Action\.(LockScreens|LockAll)$', RLL + ' “Lock” (pair merged)', 'lock'),
    (r'^Action\.(UnlockScreens|UnlockAll)$', RLL + ' “Unlock”; lock mode bar', 'unlock'), (r'^Action\.ShowScreen(ToAll)?$', RLL + ' split button (pair merged)', 'cast'),
    (r'^Action\.StopShowingScreen$', 'cast mode bar; ribbon button', 'cast-off'), (r'^Action\.Internet(ForAll)?$', RLC + ' (pair merged)', 'globe'),
    (r'^Action\.Exam(ForAll)?$', RLC + ' “Start exam” (pair merged)', 'exam'), (r'^Action\.ExtendExam$', 'exam mode bar; ' + RLC, 'timer-plus'),
    (r'^Action\.EndExam$', 'exam mode bar; ' + RLC, 'flag'), (r'^Action\.CollectFiles$', RLL + '; ' + RLC, 'collect'), (r'^Action\.ResetProfile$', RLPC + ' (danger)', 'reset-user'),
    (r'^Tab\.Lab$', 'nav “Class”', 'class'), (r'^Tab\.Scripts$', 'nav “Scripts”', 'scripts'), (r'^Tab\.Packages$', 'nav “Programs” (renamed)', 'programs'),
    (r'^Tab\.(Jobs|Events)$', 'nav “Activity” (merged)', 'activity'), (r'^Tab\.Settings$', 'gear “Settings” (this computer) + nav “Lab” (lab-wide admin)', 'settings'),
    (r'^Main\.Disconnect$', 'lab switcher ▾ › Leave the room…', 'leave'), (r'^Main\.(LabChip|LabChipConnecting)$', 'lab switcher in top bar / sidebar', 'labs'),
    (r'^Main\.Status$', 'status bar (online count) — port/instance/kbit/s move to Lab › About', 'monitor'), (r'^Main\.SelectedFormat$', 'mosaic toolbar + target chip', 'select-all'),
    (r'^Main\.Title$|^MainWindow\.', 'window title', ''), (r'^Lab\.SelectionHint$', 'mosaic toolbar hint (short) + legend', 'legend'), (r'^Lab\.Empty$', 'Class empty state card (rewritten)', 'monitor-plus'),
    (r'^Machine\.(Remove|RemoveConfirm|RemoveMany.*)$', 'confirm dialog from Remove', 'trash'), (r'^Machine\.Revoke$|^Revoke\.', 'confirm dialog from Revoke', 'ban'),
    (r'^Tile\.(LockedBadge)$', 'tile badge', 'lock'), (r'^Tile\.(BroadcastBadge|BroadcastTip)$', 'tile badge', 'cast'),
    (r'^Tile\.(Online|Outdated|Offline|Waking|NotSeen.*|HeldBy|Never)$', 'tile caption / centre state + tooltip + side panel', ''),
    (r'^Tile\.RenewalDue$', 'tile badge “Certificate”', 'clock'), (r'^Tile\.', 'tile tooltip + B side panel (one line per state)', ''),
    (r'^Key\.(Lock)$', 'key chip menu; Key and backup', 'lock'), (r'^Key\.(Remember|Remembered.*)$', 'key chip menu; Key and backup', 'remember'),
    (r'^Key\.(Forget.*|ForgottenEvent)$', 'key chip menu; Key and backup', 'forget'), (r'^Key\.(AddHolder.*|Holder.*|RemoveHolder.*|HoldersHint|SinceFormat)$', KEY + ' › Key holders', 'holders'),
    (r'^Key\.(Reprint.*|RecoveryReprinted)$', KEY + ' › Recovery code', 'lifebuoy'), (r'^Key\.', 'key chip (top bar) + ' + KEY, 'key'),
    (r'^Backup\.', KEY + ' › Backup; banner', 'backup'), (r'^Banner\.(TakeOver|OtherConsole.*|TookOver.*)$', 'banner (info)', 'swap'),
    (r'^Banner\.(Renewal|UnlockKey)$', 'banner (warning) + tile “Certificate” badge', 'clock'), (r'^Banner\.', 'banner (warning)', 'backup'),
    (r'^Network\.', 'banner + ' + ABOUT + ' › LAN access + wizard checklist', 'network'),
    (r'^Enroll\.(WritePayload|PayloadWritten|HintFormat)$', 'Add computers wizard step 2 › “For development: data only”', 'usb'),
    (r'^Enroll\.(BuildUsb|BuildFolder|InstallerWritten|PcCount)$', 'Add computers wizard steps 1–2', 'usb'),
    (r'^Enroll\.(VoidEarlier|VoidHint.*)$', 'wizard step 1 “This stick replaces earlier sticks”', 'usb'),
    (r'^Enroll\.(Activate.*|DormantHint)$|^Code\.', PCS + ' › Enrolment codes', 'ticket'), (r'^Enroll\.', PCS + ' › Enrolment codes', 'ticket'),
    (r'^Push\.', 'Update agent dialog (admin)', 'update'), (r'^Readiness\.', 'tile badge/tooltip + ' + PCS + ' “Warnings” column', 'alert'),
    (r'^Update\.', 'tile tooltip + Activity', 'update'), (r'^Access\.', 'role label in lab switcher; teacher hints; My labs card status', 'admin'),
    (r'^LabFile\.', DEV + ' “Lab file for teachers…”', 'lab-file'), (r'^Device\.(Authorize.*)$', DEV + ' “Authorize requests…”', 'approve'),
    (r'^Device\.', DEV, 'laptop'), (r'^Lock\.', 'Lock dialog; tile tooltip', 'lock'), (r'^Overlay\.', 'tile problem line (tooltip / side panel)', 'alert'),
    (r'^Broadcast\.', 'Show-my-screen menu/dialog; cast mode bar; status bar', 'cast'), (r'^VideoMode\.', 'Text|Video segmented (mode bar, full view, Settings)', 'text-mode'),
    (r'^Internet\.', 'Internet dialog; tile badge/tooltip', 'globe'), (r'^Exam\.Reason\.', 'tile problem line', 'alert'), (r'^Exam\.(Extend.*)$', 'Extend dialog / mode bar', 'timer-plus'),
    (r'^Exam\.Collect.*$', 'Exam dialog › “Collect work at the end” switch', 'collect'), (r'^Exam\.', 'Exam dialog (four switches); exam mode bar', 'exam'),
    (r'^Collect\.', 'Collect work dialog; result toast “Open folder”', 'collect'), (r'^Reset\.', 'Reset profile dialog', 'reset-user'),
    (r'^Files\.', 'Send files dialog; result toast', 'send-files'), (r'^Move\.', KEY + ' › danger card “Move to a new key…”', 'key-move'),
    (r'^Unlock\.', 'Unlock-key dialog (reason line kept per caller)', 'key'), (r'^Settings\.(Lab|LabName|LabId|ThisMachine|InstanceId|InstanceCertificate|Endpoint.*|DataDirectory|VersionFormat|TeacherMachines.*|NoOtherMachines|LiveNow|Revoked)$', ABOUT, 'info'),
    (r'^Settings\.(LabKey|Holders|RecoveryCode|Backup)$', KEY, 'key'), (r'^Settings\.(TeacherDevices.*|NoDevices|Access)$', DEV, 'laptop'), (r'^Settings\.Enrolment$', PCS, 'monitor-plus'),
    (r'^Settings\.', 'Lab › …', ''), (r'^Language\.', 'Settings › Language; My labs', 'language'), (r'^Screen\.(Control.*|CannotControl)$', 'full view Control toggle + hint', 'control'),
    (r'^Screen\.CtrlAltDel$', 'full view toolbar', 'keyboard'), (r'^Screen\.WindowsKey$', 'full view toolbar', 'win-key'), (r'^Screen\.Quality.*$', 'full view Quality ▾', 'quality'),
    (r'^Screen\.', 'full view title/status line', 'view'), (r'^Capture\.|^Input\.', 'full view / tile problem line', 'alert'),
    (r'^Jobs\.(ExportBatch.*|BatchExport.*)$', 'Activity › Actions export', 'download'), (r'^Jobs\.|^JobState\.|^Job\.', 'Activity › Actions (per-action cards, per-PC rows)', 'activity'),
    (r'^Events\.|^Event\.|^Severity\.', 'Activity › Event log', 'bell'), (r'^Departure\.', 'Leave-the-room dialog', 'leave'),
    (r'^Chooser\.(Remove.*)$', 'My labs card ⋯ › Remove from this device', 'trash'), (r'^Chooser\.(Authorize.*|Request.*|Renew.*|Status\.RenewalDue)$', 'My labs card primary button / ⋯', 'approve'),
    (r'^Chooser\.', 'My labs window', 'labs'), (r'^Import\.', 'My labs › Add from file (result dialog)', 'upload'), (r'^Setup\.', 'First-run stepper', 'plus'),
    (r'^Bootstrap\.|^Migration\.|^Store\.', 'error dialogs at start (unchanged)', 'alert'), (r'^Scripts\.', 'Scripts section; ribbon “Run script ▾”', 'scripts'),
    (r'^Packages\.(Add.*|InstallerFiles)$', 'Programs › Add program wizard', 'plus'), (r'^Packages\.Install.*$', 'Programs › install buttons', 'install'),
    (r'^Packages\.Check$', 'Programs › Check PCs', 'refresh'), (r'^Packages\.Delete.*$', 'Programs › Delete from catalog', 'trash'), (r'^Packages\.', 'Programs section / wizard', 'programs'),
    (r'^Common\.', 'shared buttons', ''), (r'^App\.|^Request\.|^Grant\.', 'file types / OS integration', 'lab-file'), (r'^Lab\.', 'Class', 'class'),
]

def place(k):
    for rx, loc, icon in RULES:
        if re.search(rx, k):
            return loc, icon
    return '— (unchanged)', ''

out = ['# Command and resource-key mapping', '',
       'Generated by `build/mapping.py` from `src/LabControl.Console/Localization/Strings.resx` '
       f'({len(KEYS)} keys). Icons are the names in this folder (`<name>.svg`). Location names: '
       '**Class › ribbon › Lesson / Exam & internet / Power / Computers** (variant A); variant B puts the same actions in the '
       'command bar, *Power ▾* and *More ▾* and the side panel. **Lab › Computers / Teacher devices / Key and backup / About this lab** '
       'is the admin section (teacher access sees *About* only). New and renamed keys with their three-language wording are in `../spec.md` §8.', '',
       '## 1. Commands (view models) → icon and new place', '',
       '| Command | Today | New place (variant A) | Variant B | Icon |', '|---|---|---|---|---|']
for c, old, a, b, i in COMMANDS:
    out.append(f'| `{c}` | {old} | {a} | {b} | `{i}` |')
out += ['', '## 2. Every resource key', '',
        'Grouped by prefix. Message-only keys (results, errors, hints) keep their text and move with their screen; '
        'keys marked *removed* are replaced by the new keys in spec §8.', '']
groups = {}
for k, v in KEYS:
    groups.setdefault(k.split('.')[0], []).append((k, v))
for g in sorted(groups):
    out += [f'### {g} ({len(groups[g])})', '', '| Key | English today | New location | Icon |', '|---|---|---|---|']
    for k, v in groups[g]:
        loc, icon = place(k)
        v = (v[:70] + '…') if len(v) > 70 else v
        out.append(f'| `{k}` | {v} | {loc} | {("`" + icon + "`") if icon else ""} |')
    out.append('')
open(os.path.join(H, '..', 'icons', 'mapping.md'), 'w').write('\n'.join(out) + '\n')
print(len(KEYS), 'keys mapped;', sum(1 for k, _ in KEYS if place(k)[0].startswith('—')), 'unmatched')
