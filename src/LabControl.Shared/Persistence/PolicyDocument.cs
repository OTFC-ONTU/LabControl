using LabControl.Shared.Policy;

namespace LabControl.Shared.Persistence;

/// <summary>
/// <c>policy.json</c> in the agent's data directory (M6, D-69): the sessions this PC is
/// enforcing and the machine settings they displaced. It is written by every change and
/// every restore pass, and read <b>only after</b> the machine has been restored to Windows
/// defaults — the document decides what is re-applied, never what is restored, so a file
/// that is torn, foreign, from a newer build or full of nulls costs nothing but a
/// <c>policy.unreadable</c> event.
/// <para>
/// Every member the journal reads is nullable on purpose: a hand-edited or half-written
/// file must be refused by <see cref="PolicyJournal"/>, not thrown at the service loop.
/// System.Text.Json also assigns a JSON <c>null</c> into a non-nullable string, list or
/// dictionary property without complaint, so the nullability annotations alone prove
/// nothing: <see cref="MissingMember"/> walks every member the schema requires, nested
/// ones included, and names the first that is null. A document with any such member is
/// unusable as a whole — a half-read displaced-settings block would restore half the
/// originals and lose the rest (D-20, D-69 item 2).
/// </para>
/// </summary>
public sealed class PolicyDocument : ISchemaVersioned
{
    public static readonly SchemaMigrations Migrations = new(Defaults.PolicySchemaVersion);

    public int SchemaVersion { get; set; } = Defaults.PolicySchemaVersion;

    /// <summary>The lab whose console set the policy; a file naming another lab is not this PC's.</summary>
    public string? LabId { get; set; }

    public long SavedAtUnix { get; set; }

    public ClassroomPolicy? Policy { get; set; }

    /// <summary>
    /// The JSON path (<c>policy.displaced.dns[0].servers</c>) of the first member the schema
    /// requires that is <c>null</c> after deserialization, or <c>null</c> when every required
    /// member is present. Optional members (<c>policy.overlay</c>, <c>policy.exam.collect_folder</c>)
    /// may be null and are walked only when present.
    /// </summary>
    public string? MissingMember()
    {
        if (LabId is null)
        {
            return "lab_id";
        }

        if (Policy is null)
        {
            return "policy";
        }

        return Missing(Policy, "policy");
    }

    private static string? Missing(ClassroomPolicy policy, string path)
    {
        if (policy.Displaced is null)
        {
            return path + ".displaced";
        }

        return Missing(policy.Displaced, path + ".displaced")
            ?? (policy.Overlay is { } overlay ? Missing(overlay, path + ".overlay") : null)
            ?? (policy.Exam is { } exam ? Missing(exam, path + ".exam") : null)
            ?? (policy.Internet is { } internet ? Missing(internet, path + ".internet") : null)
            ?? (policy.ExamInternet is { } examInternet ? Missing(examInternet, path + ".exam_internet") : null);
    }

    private static string? Missing(DisplacedSettings displaced, string path)
    {
        if (displaced.ProfileDefaultOutbound is null)
        {
            return path + ".profile_default_outbound";
        }

        foreach (var (profile, action) in displaced.ProfileDefaultOutbound)
        {
            if (action is null)
            {
                return path + ".profile_default_outbound." + profile;
            }
        }

        if (displaced.Dns is null)
        {
            return path + ".dns";
        }

        for (var i = 0; i < displaced.Dns.Count; i++)
        {
            var dns = displaced.Dns[i];
            var at = $"{path}.dns[{i}]";
            if (dns is null)
            {
                return at;
            }

            if (dns.Interface is null)
            {
                return at + ".interface";
            }

            var servers = MissingElement(dns.Servers, at + ".servers");
            if (servers is not null)
            {
                return servers;
            }
        }

        return MissingElement(displaced.OwnedRules, path + ".owned_rules")
            ?? MissingElement(displaced.DisabledRules, path + ".disabled_rules");
    }

    private static string? Missing(PolicySession session, string path)
    {
        if (session.SessionId is null)
        {
            return path + ".session_id";
        }

        if (session.SetByInstance is null)
        {
            return path + ".set_by_instance";
        }

        return session switch
        {
            OverlaySession overlay => overlay.Message is null ? path + ".message" : null,
            ExamSession exam => exam.Message is null ? path + ".message" : MissingElement(exam.AllowedPrograms, path + ".allowed_programs"),
            InternetSession internet => internet.PresetName is null ? path + ".preset_name" : MissingElement(internet.AllowedHosts, path + ".allowed_hosts"),
            _ => null,
        };
    }

    /// <summary>A list that must exist and hold no null element.</summary>
    private static string? MissingElement(List<string>? list, string path)
    {
        if (list is null)
        {
            return path;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] is null)
            {
                return $"{path}[{i}]";
            }
        }

        return null;
    }
}
