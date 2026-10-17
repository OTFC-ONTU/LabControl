using System.Globalization;
using System.Resources;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Policy;

/// <summary>
/// The stable event codes the policy journal reports (M6, D-69). The codes are constants
/// — the console keys its tile badges and its log on them — and every message the teacher
/// reads comes from <c>PolicyStrings.resx</c>, never a literal.
/// </summary>
public static class PolicyEvents
{
    /// <summary>The unconditional restore, the refine or the read-back failed; the machine is as open as possible but not proven open.</summary>
    public const string RestoreFailed = "policy.restore_failed";

    /// <summary>The document was missing a field, torn, foreign or newer; nothing was re-applied.</summary>
    public const string Unreadable = "policy.unreadable";

    /// <summary>A session that was still valid is back in force after a restart.</summary>
    public const string Reapplied = "policy.reapplied";

    /// <summary>A session could not be applied (or re-applied); it is not held.</summary>
    public const string ApplyFailed = "policy.apply_failed";

    /// <summary>The policy mutex was held elsewhere for the whole wait; this pass did nothing.</summary>
    public const string Busy = "policy.busy";

    /// <summary>The lock reached its <c>until</c> or its hard limit and was lifted by the PC itself.</summary>
    public const string OverlayExpired = "overlay.expired";

    /// <summary>An exam or internet session reached its limit and was lifted by the PC itself.</summary>
    public const string Expired = "policy.expired";

    /// <summary>The document could not be written; the next restart comes back unrestricted.</summary>
    public const string SaveFailed = "policy.save_failed";

    /// <summary>The enforcer itself threw where nothing may throw (a bug, a disposed handle); logged, reported, and the pass abandoned — never the service loop.</summary>
    public const string EnforcerFailed = "policy.failed";

    /// <summary>A broadcast reached this PC's own <see cref="Defaults.BroadcastHardLimit"/> and was taken down (D-71).</summary>
    public const string BroadcastExpired = "broadcast.expired";

    /// <summary>A broadcast could not be shown: the console refused the stream, or the machine could not take the screen.</summary>
    public const string BroadcastFailed = "broadcast.failed";

    private static readonly ResourceManager Resources = new("LabControl.Shared.Policy.PolicyStrings", typeof(PolicyEvents).Assembly);

    /// <summary>A resource string, formatted with the invariant culture; the name itself if the resource is missing, never an exception.</summary>
    public static string Text(string name, params object?[] arguments)
    {
        string? template;
        try
        {
            template = Resources.GetString(name, CultureInfo.CurrentUICulture);
        }
        catch (MissingManifestResourceException)
        {
            template = null;
        }

        template ??= name;
        return arguments.Length == 0 ? template : string.Format(CultureInfo.InvariantCulture, template, arguments);
    }

    public static string RefusedUnbound => Text("RefusedUnbound");

    public static string RefusedRevoked => Text("RefusedRevoked");

    public static string RefusedWithdrawn => Text("RefusedWithdrawn");

    public static string RefusedAlreadyEnded => Text("RefusedAlreadyEnded");

    public static string RefusedUnknownKind => Text("RefusedUnknownKind");

    public static string RefusedStopped => Text("RefusedStopped");

    public static string RefusedNoSession => Text("RefusedNoSession");

    /// <summary>The teacher's word for a session kind, for the event messages.</summary>
    public static string KindName(PolicySession session) => session switch
    {
        OverlaySession { Kind: Overlay.Types.Kind.BroadcastStart } => Text("KindBroadcast"),
        OverlaySession => Text("KindLock"),
        ExamSession => Text("KindExam"),
        InternetSession => Text("KindInternet"),
        _ => session.GetType().Name,
    };
}

/// <summary>One thing the journal wants the teacher to see; the service hands it to <c>AgentLink.Report</c>.</summary>
public sealed record PolicyEvent(Event.Types.Severity Severity, string Code, string Message);

/// <summary>
/// The stable codes <c>OverlayState.reason</c> carries (M6 portion 1, D-70 item 7): why a
/// lock is weaker than the hooks would make it, or why it is not showing yet. The helper
/// and the service put codes on the wire — zero or more, space-separated — and the console
/// maps each to words from its resources, the way it does for capture reasons; nothing
/// teacher-facing is ever a literal on the agent side. The detail behind a code (the Win32
/// error text) goes to the helper's log.
/// </summary>
public static class OverlayReasons
{
    /// <summary>No session helper is connected to the service: nothing can be shown until one is.</summary>
    public const string HelperDown = "overlay.helper_down";

    /// <summary>Nobody is logged on: there is no interactive session to show the lock in.</summary>
    public const string NoSession = "overlay.no_session";

    /// <summary>The helper is connected but has not applied the lock yet; the service re-asserts it.</summary>
    public const string NotShownYet = "overlay.not_shown_yet";

    /// <summary>The helper could not create the lock window on the input desktop.</summary>
    public const string WindowFailed = "overlay.window_failed";

    /// <summary>A low-level hook could not be installed, or the last re-arm failed; the student's input may pass.</summary>
    public const string HookRefused = "overlay.hook_refused";

    /// <summary><c>BlockInput</c> is in use instead of the hooks: it blocks the teacher's remote control too, and any process on the desktop can undo it.</summary>
    public const string BlockInputFallback = "overlay.blockinput_fallback";

    /// <summary>The hooks and <c>BlockInput</c> both failed: the student's input is NOT blocked.</summary>
    public const string InputOpen = "overlay.input_open";

    private static readonly string[] Known = [HelperDown, NoSession, NotShownYet, WindowFailed, HookRefused, BlockInputFallback, InputOpen];

    public static bool IsKnown(string code) => Array.IndexOf(Known, code) >= 0;

    /// <summary>The codes in a <c>reason</c> field, in order, blanks dropped.</summary>
    public static string[] Split(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? [] : reason.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Codes as one <c>reason</c> field.</summary>
    public static string Join(IEnumerable<string> codes) => string.Join(' ', codes.Where(c => !string.IsNullOrWhiteSpace(c)));
}
