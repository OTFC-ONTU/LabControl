using LabControl.Shared.Protocol;

namespace LabControl.Shared.Link;

/// <summary>
/// What a PC says on its way out when another teacher machine has taken it over (D-58).
/// <para>
/// A console may credit another machine with a PC only on a <b>positive</b> observation, and
/// the end of a stream is not one: a student switching the PC off, a Wi-Fi blip, a crash and
/// an agent restart all look exactly the same from the console's side. The agent, though,
/// knows perfectly well why it is leaving — it has just honoured a <c>take</c> beacon — so it
/// says so before the stream ends, and that report is the only thing the console attributes
/// on.
/// </para>
/// <para>
/// It travels as an ordinary <c>Event</c>, which needs no field and no version negotiation
/// (the frozen subset is untouched): a fixed <see cref="Code"/> whose message carries only
/// the taker's instance id, exactly as <c>setup.readiness</c> carries a machine-readable
/// payload the console turns into words. An agent that predates this build simply says
/// nothing, and its departure is then credited to nobody — the honest answer.
/// </para>
/// </summary>
public static class DepartureNotice
{
    /// <summary>The fixed event code. Never parameterised: the payload lives in the message.</summary>
    public const string Code = "link.taken_over";

    /// <summary>The longest instance id this will carry; a datagram-sized id is a forgery, not a console.</summary>
    public const int MaxInstanceIdLength = 64;

    /// <summary>The event a PC sends before it drops the link for <paramref name="takerInstanceId"/>.</summary>
    public static Event Create(string takerInstanceId, DateTimeOffset at) => new()
    {
        Severity = Event.Types.Severity.Info,
        Code = Code,
        Message = takerInstanceId,
        AtUnix = at.ToUnixTimeSeconds(),
    };

    /// <summary>
    /// The taker's instance id out of a reported event, or <c>null</c> when this is not a
    /// departure notice or its payload is not an instance id at all. The message comes from
    /// the PC, so it is checked rather than believed; naming an instance is still only a
    /// claim, which is why the console also requires that instance to be one it has heard
    /// beaconing itself.
    /// </summary>
    public static string? TakerOf(Event reported) =>
        reported.Code == Code && IsInstanceId(reported.Message) ? reported.Message : null;

    private static bool IsInstanceId(string value)
    {
        if (value.Length is 0 or > MaxInstanceIdLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }
}
