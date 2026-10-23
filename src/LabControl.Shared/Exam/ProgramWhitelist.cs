namespace LabControl.Shared.Exam;

/// <summary>One process as the service sees it (D-75): enough to judge it, nothing more.</summary>
/// <param name="ProcessId">The process id; reused by Windows, so <paramref name="StartedAt"/> is part of the identity.</param>
/// <param name="Name">The executable's file name as the snapshot reports it.</param>
/// <param name="SessionId">The Windows session the process runs in.</param>
/// <param name="UserSid">The SID of the process token's user, or <c>null</c> when the token could not be read — such a process is never judged.</param>
/// <param name="ImagePath">The full image path, or <c>null</c> when it could not be read.</param>
/// <param name="StartedAt">The process creation time (FILETIME ticks), or 0 when unknown.</param>
public sealed record ProcessInfo(int ProcessId, string Name, int SessionId, string? UserSid, string? ImagePath, long StartedAt)
{
    /// <summary>
    /// Windows runs it as a protected process (an antimalware session helper, for one): it may
    /// carry the student's token, but nobody can end it, so it is never judged (VM drill
    /// 2026-10-23: Defender's <c>DefenderSessionHelper.exe</c> was warned about and refused).
    /// </summary>
    public bool IsProtectedProcess { get; init; }

    /// <summary>Process id and creation time: a reused id is another process.</summary>
    public (int, long) Key => (ProcessId, StartedAt);
}

/// <summary>
/// Whose processes an exam judges (D-75): the student's own — the user of the interactive
/// session, in that session. A process of any other user (SYSTEM, the window manager's
/// <c>DWM-1</c>, an administrator who elevated) is never judged, whatever its name.
/// </summary>
/// <param name="SessionId">The active console session.</param>
/// <param name="UserSid">The SID of the user signed in there; <c>null</c> = nobody, and nothing is judged.</param>
/// <param name="ProtectedDirectories">Directories whose executables are never judged — LabControl's own.</param>
public sealed record SessionContext(int SessionId, string? UserSid, IReadOnlyList<string> ProtectedDirectories)
{
    /// <summary>
    /// Where a never-touch name must run from to be the Windows process it names (<c>%SystemRoot%</c>,
    /// <c>WindowsApps</c>, Edge WebView); empty = the name alone is trusted, for a context with no
    /// paths to compare (the simulator). A game copied to the desktop as <c>conhost.exe</c> is judged.
    /// </summary>
    public IReadOnlyList<string> SystemDirectories { get; init; } = [];

    public static SessionContext Nobody { get; } = new(-1, null, []);
}

/// <summary>
/// The allowed-programs judgement (M6 portion 4, D-75): <b>session and token before name</b>.
/// A process is closed only when it runs in the student's session, under the student's own
/// token, is not a protected process, is not LabControl's, is not in the hard-coded <see cref="NeverTouch"/> set of shell
/// and Windows processes, and is not on the teacher's list. Matching is by executable name
/// only (no process tree): a program the allowed IDE starts is judged by its own name, and a
/// renamed executable passes. Pure, so the whole judgement is tested on the Mac.
/// </summary>
public static class ProgramWhitelist
{
    /// <summary>
    /// What the whitelist never closes, whatever the teacher's list says: the shell and the
    /// Windows processes that run under the student's own token. Closing any of them would
    /// break the desktop, input, sign-out or the allowed programs themselves (<c>conhost.exe</c>
    /// hosts every console program, and on Windows 11 <c>WindowsTerminal.exe</c> /
    /// <c>OpenConsole.exe</c> host it instead; <c>svchost.exe</c> runs per-user services). A
    /// console host runs nothing by itself: what is typed into it starts programs that are judged
    /// by their own names.
    /// </summary>
    public static IReadOnlySet<string> NeverTouch { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "explorer.exe", "sihost.exe", "taskhostw.exe", "taskhost.exe", "ctfmon.exe", "runtimebroker.exe",
        "shellexperiencehost.exe", "startmenuexperiencehost.exe", "shellhost.exe", "searchhost.exe", "searchapp.exe",
        "searchui.exe", "searchprotocolhost.exe", "searchfilterhost.exe", "textinputhost.exe", "tabtip.exe",
        "dllhost.exe", "conhost.exe", "openconsole.exe", "windowsterminal.exe", "svchost.exe", "smartscreen.exe", "securityhealthsystray.exe",
        "applicationframehost.exe", "backgroundtaskhost.exe", "userinit.exe", "lockapp.exe", "logonui.exe",
        "dwm.exe", "csrss.exe", "winlogon.exe", "fontdrvhost.exe", "wudfhost.exe", "audiodg.exe", "consent.exe",
        "werfault.exe", "wermgr.exe", "msedgewebview2.exe", "useroobebroker.exe", "systemsettingsbroker.exe",
        "credentialuibroker.exe", "wmiprvse.exe", "shutdown.exe", "logoff.exe", "rundll32.exe", "dashost.exe",
        "smss.exe", "services.exe", "lsass.exe", "wininit.exe",
    };

    /// <summary>
    /// Background helpers that start with Windows or with another program, never by the student's
    /// hand: closed like anything else not on the list, but <b>quietly</b> — no warning and no
    /// line on the strip, which otherwise told a student that OneDrive "is not allowed" when they
    /// had never opened it (a lab PC, 2026-10-23). The teacher still sees them among the closed.
    /// </summary>
    public static IReadOnlySet<string> Quiet { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "onedrive.exe", "onedrivestandaloneupdater.exe", "filecoauth.exe", "filesynchelper.exe",
        "phoneexperiencehost.exe", "yourphone.exe", "crossdeviceresume.exe", "crossdeviceservice.exe",
        "widgets.exe", "widgetservice.exe", "gamebar.exe", "gamebarftserver.exe", "gamebarpresencewriter.exe",
        "skypebackgroundhost.exe", "skypeapp.exe", "hxtsr.exe", "hxoutlook.exe", "hxaccounts.exe",
        "microsoft.photos.exe", "photosapp.exe", "msteamsupdate.exe", "softlandingtask.exe", "slui.exe", "mobsync.exe", "m365copilot.exe", "m365copilot_autostarter.exe",
        "teams.exe", "ms-teams.exe", "msteams.exe", "adobearm.exe", "jusched.exe", "igfxem.exe", "igfxext.exe",
        "rtkauduservice64.exe", "ravbg64.exe", "nvcontainer.exe", "nvdisplay.container.exe", "amdrsserv.exe",
        "radeonsoftware.exe", "googledrivefs.exe", "dropbox.exe", "steamwebhelper.exe", "identity_helper.exe",
        "msedge_proxy.exe", "cookie_exporter.exe", "notification_helper.exe", "updater.exe", "googleupdate.exe",
        "microsoftedgeupdate.exe", "officeclicktorun.exe", "sdxhelper.exe", "msoia.exe",
    };

    /// <summary>True when <paramref name="name"/> (any form <see cref="ProgramNames"/> takes) is one of the <see cref="Quiet"/> background helpers.</summary>
    public static bool IsQuiet(string name) => ProgramNames.TryNormalize(name, out var normalized) && Quiet.Contains(normalized);

    /// <summary>True when the exam closes <paramref name="process"/>: the student's, not protected, and not allowed.</summary>
    public static bool ShouldClose(ProcessInfo process, SessionContext context, IReadOnlySet<string> allowed)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(allowed);

        // Session and token first: nothing of another user, or outside the student's session, is ever judged.
        if (context.UserSid is null || process.SessionId != context.SessionId || process.UserSid is null
            || !string.Equals(process.UserSid, context.UserSid, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (process.IsProtectedProcess)
        {
            return false;
        }

        if (process.ImagePath is { Length: > 0 } path && IsProtected(path, context.ProtectedDirectories))
        {
            return false;
        }

        if (!ProgramNames.TryNormalize(process.Name, out var name))
        {
            // No name to judge by: never closed on a guess.
            return false;
        }

        if (allowed.Contains(name))
        {
            return false;
        }

        // A never-touch name counts only where Windows keeps it; an unreadable path is not a guess to close on.
        return !NeverTouch.Contains(name)
            || (context.SystemDirectories.Count > 0 && process.ImagePath is { Length: > 0 } image && !IsProtected(image, context.SystemDirectories));
    }

    private static bool IsProtected(string path, IReadOnlyList<string> directories)
    {
        foreach (var directory in directories)
        {
            if (directory.Length == 0)
            {
                continue;
            }

            var prefix = directory.EndsWith('\\') || directory.EndsWith('/') ? directory : directory + "\\";
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || path.Replace('/', '\\').StartsWith(prefix.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
