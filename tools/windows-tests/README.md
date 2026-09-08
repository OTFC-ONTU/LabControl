# Disposable Windows acceptance fixtures

These tools are development fixtures, excluded from the USB installer. They exercise
production adapters and transport. They do not make M4 complete merely by compiling.

Use a disposable Windows VM clone. Never point the fixtures at a classroom PC or the
original development VM. The UTM scripts deliberately bind operations to the isolated
clone identity and verify its network/share configuration.

## Native settings

`publish-smoke.sh win-arm64` (or `win-x64`) publishes a self-contained probe. Run
`LabControl.Setup.NativeSmoke.exe --read-only` first. The mutation modes in `Program.cs`
use a separate, protected journal and compare original native values after restoration.
An existing probe journal must be recovered explicitly, never overwritten.

For repeated iterations, an executable-only ISO is much faster than QEMU guest-agent
file transfer. `utm-attach-iso.applescript` changes only a verified removable drive while
the clone is stopped; it preserves its system disk. The host-network wrapper additionally
requires the expected fresh test-lab marker on the ISO.

The opt-out baseline helper stores keyed fingerprints and a protected random key. It
compares account and profile identities plus the exact sign-in tuple without printing
credentials or persisting their plaintext. It does not assert that a live Windows
profile's every file remains byte-identical during ordinary OS activity.
Use `--capture-baseline <GUID>` and `--verify-baseline <GUID>` for independent
later cycles; preserve the original baseline and its historical results. The separate
`utm-exec-sid-test.applescript` binds the fresh sign-in clone to its exact UUID,
unique host-only MAC and test-lab marker; it does not relax the original wrappers.

`InteractiveSetup` starts the actual installer as the already active local administrator
through an InteractiveToken task. It accepts only a fresh fixture installation, keeps
the actual initial dialog visible, verifies the default checkbox through MSAA (WinForms
owner-drawn controls do not reliably answer `BM_GETCHECK`), enters the number and captures
output privately. A failed helper terminates only its own spawned installer before closing
redirected streams. `InteractiveUninstall`
drives the real standalone removal confirmation in that same session. Neither tool
stores personal credentials or introduces a production confirmation bypass.

For an already installed disposable VM logged into standard student,
`InteractiveUninstall --system-desktop <installation-id>` duplicates the authorized SYSTEM
fixture token into the verified active console session and drives the actual confirmation.
The optional pair `--remove-student --confirm-remove-student` exercises owned removal. A
loaded profile must refuse deletion and retain pending intent. After logging that student
out, `--pending-student-retry <installation-id>` passes ordinary uninstall arguments: it
requires durable pending removal, `ERROR_NO_TOKEN`, and a successful empty WTS user query.
It cannot be used as a general unattended uninstall. These fixture modes verify installer
behavior, not authentication as the teacher's original administrator. Run the executable
from a protected local copy; ISO or student-writable sources are refused.

NativeSmoke `--uac-disposable-vm <installation-id>` schedules only an inert elevation
target in the recorded standard student's existing session. Inspect the ordinary Windows
credential prompt with empty fields, cancel it normally, then use `--uac-cleanup` with the
returned fixture ID. This checks prompt reachability, not successful authentication as a
personal administrator; it never reads or supplies that administrator's password.

The NativeSmoke `--partial-create`, `--partial-verify-partial`,
`--partial-verify-removed` and `--partial-cleanup` modes take one new fixture GUID.
They create a disabled, uniquely marked foreign discovery rule to stop Setup before
service creation; removal must preserve that rule and the separate `Baseline.<GUID>`
account/sign-in evidence. Cleanup deletes only the unchanged fixture rule. Preserve
earlier baselines and always verify removal before fixture cleanup.

`qmp-capture.py` is a read-only screenshot helper for blank UAC/logon credential fields.
It verifies the exact disposable VM UUID through a private secondary QMP socket and
refuses other VMs. It sends no input; never capture entered credentials. The socket
directory and output image remain private. Its docstring describes the explicit setup.

`template-editability.ps1` (when supplied with the recorded installation fixture)
runs file checks in a LIMITED task under the exact managed student SID. Actual copied files
are opened for write, appended and restored to their original length; independent hashes
confirm restoration. The Default source files must reject write access. Temporary copies
are edited, renamed and deleted. Do not substitute a SYSTEM run for this check.

`observe-student-pointer.ps1` requires the exact standard student SID and reports only
cursor coordinates and screen size. Coordinate it with `pointer-smoke`; avoid local mouse
movement while verifying the network-delivered position.

`executable-handout-observer.ps1` first proves process-start observation with a private,
version-only control executable. After READY, send that same executable name through real
`handouts` with `open=true`. Only after the job succeeds, write a private atomic UTF-8
`delivery-complete.json` with `ok: true` and its actual `job_id`; use structured JSON or
exact bytes, not shell `echo` escaping. Require `positive_control`, `delivery_confirmed`
and zero matching launches, including ten seconds after the handshake. A timeout or
unparseable handshake is a failed fixture, never evidence of non-execution.

## Disposable console

Build `TestLabHost/TestLabHost.csproj` on macOS. Start its executable with an explicit
VM-facing IPv4 address and a nonproduction port (or port zero). Each run creates a fresh
private directory, CA and lab; it never accepts an existing console directory. Its JSON
output identifies the public USB payload, command inbox and status file.

Copy only the reported `usb/LabControl` directory into the fixture media. Never copy its
`console` directory. When using read-only ISO media, copy the USB payload to a writable
fixture directory in the guest before running Setup: enrollment consumes a one-use code.

Submit commands by writing a complete JSON file named with a new 32-digit GUID into the
reported command directory. Results use the same name in the sibling `results` directory.
The `push` command requires `agent_id`, `build_directory` and `base_version`; it uses the
real console signing and file transport. `status` and `stop` require only `action`.
`unlock` explicitly reopens the disposable lab's key after the ordinary idle timeout;
its generated passphrase stays only in the fixture process. Unlock before new enrollment
or rekey, and check `lab_key_unlocked` in status. Existing links do not require the key.
`push-wrong-key` has the same arguments but signs the valid manifest with a separate
disposable authority; require a refusal without a version switch and a visible event.
`session-script` runs a fixed script that rejects SYSTEM/session 0 and returns a marker.
`handouts` accepts `agent_id`, a bounded `paths` array and `open`; it uses the production
send-files batch path. Verify files and opening in the real student session separately.
`pointer-smoke` sends only one normalized mouse move to (0.25, 0.75), without clicks or
keys; verify the actual cursor position in the interactive guest independently. New fixture
commands require a newly built host and do not appear in an already running process.
Status includes decoded thumbnail dimensions/frame counts and cached readiness codes.
Do not change the active host while a probation trial is supposed to prove continuous
connectivity. Restarting this fixture creates a different lab and requires explicit rekey.

## Broken releases

`BrokenAgent` publishes two independent, test-only Windows service executables. Both
answer `--version`; one exits after SCM startup, the other starts but never connects.
They contain no production fault switch. Combine each executable with the published
production `session.exe`, then sign/push it through the disposable console.

`UpdateRecoveryDrill/publish-drill.sh win-arm64` builds an observer. Supply the verified
disposable installation GUID and choose `broken-crash`, `no-link-deadline` or
`good-ten-minutes` after a real update has started. These modes preserve production
deadlines and require the actual agent/recovery process to reach and finalize its state.
`crash-start` is a separate deliberate process-termination drill, not proof that the
broken service fixture crashed on its own.

The `task-probe` mode registers and deletes the production recovery-shaped task, then
repeats missing-task cleanup. It does not change an active trial or the service. Native
Task Scheduler requires the XML encoding to match its declaration; the production probe
is the acceptance check, not successful XML parsing on macOS.

The native acceptance results and remaining physical-lab checks are recorded in
`docs/ROADMAP.md`. Generated executables, ISOs and local result JSON are ignored.
