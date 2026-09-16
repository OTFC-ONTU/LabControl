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
