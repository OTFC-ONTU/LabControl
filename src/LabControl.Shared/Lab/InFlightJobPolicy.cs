using LabControl.Shared.Protocol;

namespace LabControl.Shared.Lab;

/// <summary>
/// What a returning console may do with the jobs it left running on the PCs (M5, D-57 item 4).
/// <para>
/// The saved rows exist so a script the teacher started before leaving still shows its result
/// when the console comes back: the row is sent again and the agent's ledger answers it from
/// the cache (D-32 item 7). But the ledger lives in the agent's memory only. If the PC has
/// rebooted, or the service was reinstalled, the re-sent job is not answered — it <b>runs</b>.
/// A <c>shutdown</c> restored the next morning would therefore power the class off, so the
/// rules here are deliberately narrow: only a kind whose second run is harmless <i>and</i>
/// whose payload the new session can serve again comes back live; everything else is kept as
/// a closed row whose outcome is honestly unknown.
/// </para>
/// </summary>
public static class InFlightJobPolicy
{
    /// <summary>
    /// The only kind a returning session sends again. A script is pulled and run in the
    /// student's session; the console can offer its text again from <c>scripts.json</c>, and
    /// the teacher who started it is the one who gets the result. Every other kind either acts
    /// on the machine (<c>shutdown</c>, <c>reboot</c>, <c>logoff</c>, <c>reset_profile</c>,
    /// <c>self_update</c>, <c>rekey</c>) or carries a payload the closed session owned
    /// (<c>send_file</c>, <c>install_package</c>, <c>collect_files</c>).
    /// </summary>
    public static bool IsResendable(Job.Types.Kind kind) => kind == Job.Types.Kind.RunScript;

    /// <summary>
    /// How long this row may wait for its console: the job's own timeout, or
    /// <see cref="Defaults.InFlightJobsMaxAge"/>, whichever is shorter. Past it the PC has long
    /// given up and the row is only noise.
    /// </summary>
    public static TimeSpan MaxAgeOf(InFlightJob job)
    {
        var timeout = job.TimeoutSeconds > 0 ? TimeSpan.FromSeconds(job.TimeoutSeconds) : Defaults.InFlightJobsMaxAge;
        return timeout < Defaults.InFlightJobsMaxAge ? timeout : Defaults.InFlightJobsMaxAge;
    }

    /// <summary>
    /// Why this row must not be sent again, or <c>null</c> when it may be. The caller adds its
    /// own reason for a payload it can no longer serve (D-57 item 4, S2).
    /// </summary>
    /// <param name="savedAt">When the file that holds this row was written.</param>
    public static string? RefuseReason(InFlightJob job, DateTimeOffset savedAt, DateTimeOffset now)
    {
        if (!IsResendable(job.Kind))
        {
            return $"a {job.Kind} job is never sent to a PC a second time";
        }

        var age = now - savedAt;
        var limit = MaxAgeOf(job);
        if (age > limit)
        {
            return $"it was left {Describe(age)} ago, longer than the {Describe(limit)} this job may wait";
        }

        return null;
    }

    /// <summary>
    /// The PC has rebooted since the job was handed to it, so the agent's ledger is provably
    /// gone and the re-sent copy would run instead of being answered.
    /// </summary>
    /// <remarks>
    /// <paramref name="bootTimeUnix"/> is the PC's own clock (<c>Hello.boot_time_unix</c>) and
    /// <paramref name="deliveredAtUnix"/> is the console's, so a badly set PC clock can make
    /// this true when no reboot happened. That way round is safe: the teacher gets an honest
    /// <i>outcome unknown</i> row instead of a job running twice.
    /// </remarks>
    public static bool RebootedSinceDelivery(long deliveredAtUnix, long bootTimeUnix) =>
        bootTimeUnix > 0 && deliveredAtUnix > 0 && deliveredAtUnix < bootTimeUnix;

    private static string Describe(TimeSpan span) =>
        span.TotalMinutes < 1 ? $"{span.TotalSeconds:0} s"
        : span.TotalHours < 1 ? $"{span.TotalMinutes:0} min"
        : $"{span.TotalHours:0.#} h";
}
