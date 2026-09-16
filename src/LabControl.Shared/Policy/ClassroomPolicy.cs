using System.Text.Json.Serialization;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Policy;

/// <summary>
/// What every enforced session carries (M6, D-69): who set it, when, until when, and the
/// hard limit the <b>agent</b> clamped it to. Times are Unix seconds on the PC's clock;
/// <see cref="UntilUnix"/> of 0 means "until lifted", and <see cref="HardLimitUnix"/> is
/// always set — a session with no hard limit does not exist.
/// </summary>
public abstract class PolicySession
{
    /// <summary>Stable per application, so a re-sent message is the same session and a late PC joins it.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>The console instance whose message this is — the id in the leaf the TLS handshake validated, never a claim.</summary>
    public string SetByInstance { get; set; } = string.Empty;

    public long SinceUnix { get; set; }

    /// <summary>When the console wants it to end; 0 = until lifted.</summary>
    public long UntilUnix { get; set; }

    /// <summary>The agent's own ceiling, re-clamped on every re-apply (D-69 step 5).</summary>
    public long HardLimitUnix { get; set; }

    /// <summary>Still within its own limits: not past <see cref="UntilUnix"/> (if set) and not past <see cref="HardLimitUnix"/>.</summary>
    public bool IsStillValid(long nowUnix) => (UntilUnix <= 0 || nowUnix < UntilUnix) && nowUnix < HardLimitUnix;

    /// <summary>The moment the agent must lift it on its own: the earlier of <c>until</c> and the hard limit.</summary>
    [JsonIgnore]
    public long DeadlineUnix => UntilUnix > 0 ? Math.Min(UntilUnix, HardLimitUnix) : HardLimitUnix;

    public bool Ended(long nowUnix) => !IsStillValid(nowUnix);
}

/// <summary>A lock screen (M6 portion 1). A broadcast is not a session: it dies with its stream (D-71).</summary>
public sealed class OverlaySession : PolicySession
{
    public Overlay.Types.Kind Kind { get; set; } = Overlay.Types.Kind.Lock;

    public string Message { get; set; } = string.Empty;
}

/// <summary>An exam (M6 portion 4); the fields portion 1 needs to persist and re-apply it are here, the rest arrives with the portion.</summary>
public sealed class ExamSession : PolicySession
{
    public string Message { get; set; } = string.Empty;

    /// <summary>Executable names; empty = no whitelist.</summary>
    public List<string> AllowedPrograms { get; set; } = [];

    /// <summary>The folder collected at the end, or <c>null</c> for nothing.</summary>
    public string? CollectFolder { get; set; }
}

/// <summary>A standalone internet policy or the exam's internet switch (M6 portion 3).</summary>
public sealed class InternetSession : PolicySession
{
    public InternetPolicy.Types.Mode Mode { get; set; } = InternetPolicy.Types.Mode.Open;

    public List<string> AllowedHosts { get; set; } = [];

    public string PresetName { get; set; } = string.Empty;
}

/// <summary>One interface's DNS servers before a whitelist policy pointed them at the loopback resolver.</summary>
public sealed class DisplacedDns
{
    public string Interface { get; set; } = string.Empty;

    /// <summary>True when the servers came from DHCP — the restore is "back to DHCP", not the list.</summary>
    public bool Dhcp { get; set; }

    public List<string> Servers { get; set; } = [];
}

/// <summary>
/// The original machine state a policy displaced (D-69): what a refined restore puts back
/// <i>exactly</i> after the unconditional restore has already put Windows defaults in place.
/// Kept until a restore confirms — a failed refine never discards the teacher's original
/// values (step 3).
/// </summary>
public sealed class DisplacedSettings
{
    /// <summary>Per firewall profile (<c>domain</c>, <c>private</c>, <c>public</c>): the default outbound action before the policy, <c>allow</c> or <c>block</c>.</summary>
    public Dictionary<string, string> ProfileDefaultOutbound { get; set; } = [];

    public List<DisplacedDns> Dns { get; set; } = [];

    /// <summary>The firewall rules the agent owns in <see cref="Defaults.InternetFirewallRuleGroup"/>; deleted on restore, the <c>agent.exe</c> exemption last.</summary>
    public List<string> OwnedRules { get; set; } = [];

    /// <summary>Set only after native read-back proved the displacement took (the D-47 discipline).</summary>
    public bool Applied { get; set; }

    [JsonIgnore]
    public bool IsEmpty => ProfileDefaultOutbound.Count == 0 && Dns.Count == 0 && OwnedRules.Count == 0;
}

/// <summary>
/// Everything the agent enforces right now (M6, D-69): at most one of each session and the
/// settings they displaced. This is the <c>policy</c> member of <c>policy.json</c>; it is
/// also the in-memory truth the <see cref="PolicyJournal"/> keeps.
/// </summary>
public sealed class ClassroomPolicy
{
    public OverlaySession? Overlay { get; set; }

    public ExamSession? Exam { get; set; }

    /// <summary>The standalone internet policy, re-applied after an exam that started inside it ends.</summary>
    public InternetSession? Internet { get; set; }

    /// <summary>The exam's own internet switch; wins over <see cref="Internet"/> while the exam is active.</summary>
    public InternetSession? ExamInternet { get; set; }

    public DisplacedSettings? Displaced { get; set; }

    [JsonIgnore]
    public bool IsEmpty => Overlay is null && Exam is null && Internet is null && ExamInternet is null;

    /// <summary>Every session held, whichever kind, for the deadline scan.</summary>
    [JsonIgnore]
    public IEnumerable<PolicySession> Sessions
    {
        get
        {
            if (Overlay is not null) yield return Overlay;
            if (Exam is not null) yield return Exam;
            if (Internet is not null) yield return Internet;
            if (ExamInternet is not null) yield return ExamInternet;
        }
    }

    /// <summary>
    /// The agent's hard limit for a session (D-69): <c>min(proposed, now + ceiling)</c>; a
    /// proposal of 0 or less means "none given" and yields the ceiling. Never overflows — a
    /// <c>now</c> near <c>long.MaxValue</c> yields <c>long.MaxValue</c> rather than a negative
    /// limit that would end every session at once.
    /// </summary>
    public static long Clamp(long proposedUnix, TimeSpan ceiling, long nowUnix)
    {
        var ceilingSeconds = Math.Max(0, (long)ceiling.TotalSeconds);
        var ceilingUnix = Add(nowUnix, ceilingSeconds);
        return proposedUnix <= 0 ? ceilingUnix : Math.Min(proposedUnix, ceilingUnix);
    }

    /// <summary>
    /// A duration from the wire stamped onto this clock (D-70 item 6): <c>now + seconds</c>,
    /// saturating at <c>long.MaxValue</c> rather than wrapping into the past. A negative
    /// duration yields <c>now</c> — a session that has already ended.
    /// </summary>
    public static long Add(long nowUnix, long seconds)
    {
        if (seconds <= 0)
        {
            return nowUnix;
        }

        return nowUnix > long.MaxValue - seconds ? long.MaxValue : nowUnix + seconds;
    }
}
