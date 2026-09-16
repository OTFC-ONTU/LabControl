using Xunit;

using Google.Protobuf;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Tests;

/// <summary>
/// Proves the .proto actually generates usable types on both sides, and that the
/// envelope shape documented in docs/PROTOCOL.md survives a round trip.
/// </summary>
public class ProtocolTests
{
    [Fact]
    public void Agent_message_round_trips_through_bytes()
    {
        var original = new AgentMessage
        {
            Hello = new Hello
            {
                AgentId = "8f14e45f-ceea-467a-9575-28263f0c9b41",
                LabId = "1c383cd3-0b7c-4d69-9a37-4bfd08bb6c2e",
                Number = 7,
                AgentVersion = "0.1.0",
                ProtocolVersion = Defaults.ProtocolVersion,
                Mac = "02:00:5E:00:00:07",
            },
        };

        var decoded = AgentMessage.Parser.ParseFrom(original.ToByteArray());

        Assert.Equal(AgentMessage.PayloadOneofCase.Hello, decoded.PayloadCase);
        Assert.Equal(7, decoded.Hello.Number);
        Assert.Equal(Defaults.ProtocolVersion, decoded.Hello.ProtocolVersion);
    }

    [Fact]
    public void Exam_mode_carries_all_four_switches_independently()
    {
        // D-16: the four restrictions must be expressible one at a time.
        var timerOnly = new ExamMode { SessionId = "e1", Active = true, EndsAtUnix = 1_800 };

        Assert.Null(timerOnly.Internet);
        Assert.Empty(timerOnly.AllowedPrograms);
        Assert.Null(timerOnly.Collect);
    }

    [Fact]
    public void Internet_policy_is_the_same_message_standalone_and_inside_an_exam()
    {
        // D-22: one mechanism, two lifetimes. A whitelist preset built for a lesson must be
        // embeddable in an exam unchanged.
        var policy = new InternetPolicy
        {
            SessionId = "i1",
            Mode = InternetPolicy.Types.Mode.Whitelist,
            AllowedHosts = { "*.jetbrains.com", "docs.oracle.com" },
            HardLimitUnix = 28_800,
            PresetName = "Java docs",
        };

        var standalone = new ConsoleMessage { InternetPolicy = policy };
        var exam = new ConsoleMessage { ExamMode = new ExamMode { SessionId = "e1", Active = true, Internet = policy } };

        Assert.Equal(policy, ConsoleMessage.Parser.ParseFrom(standalone.ToByteArray()).InternetPolicy);
        Assert.Equal(policy, ConsoleMessage.Parser.ParseFrom(exam.ToByteArray()).ExamMode.Internet);
    }

    [Fact]
    public void An_overlay_and_its_state_round_trip_and_an_old_overlay_still_parses()
    {
        // M6 portion 1 (D-70): Overlay gained session_id / until_unix / hard_limit_unix, and
        // OverlayState is what the PC answers — on the link and up the helper's pipe.
        var overlay = new Overlay { Kind = Overlay.Types.Kind.Lock, Message = "Дивіться на дошку", SessionId = "lock-1", UntilUnix = 1_800_000_600, HardLimitUnix = 1_800_003_600 };
        var onTheLink = ConsoleMessage.Parser.ParseFrom(new ConsoleMessage { Overlay = overlay }.ToByteArray());
        var onThePipe = ServiceMessage.Parser.ParseFrom(new ServiceMessage { Overlay = overlay }.ToByteArray());
        Assert.Equal(overlay, onTheLink.Overlay);
        Assert.Equal(overlay, onThePipe.Overlay);

        var state = new OverlayState
        {
            Kind = Overlay.Types.Kind.Lock, SessionId = "lock-1", SinceUnix = 1_800_000_000, UntilUnix = 1_800_000_600, HardLimitUnix = 1_800_003_600,
            InputBlocked = true, Reason = string.Empty, SetByInstance = "console-a", Problem = string.Empty,
        };
        var fromAgent = AgentMessage.Parser.ParseFrom(new AgentMessage { OverlayState = state }.ToByteArray());
        var fromHelper = HelperMessage.Parser.ParseFrom(new HelperMessage { OverlayState = state }.ToByteArray());
        Assert.Equal(AgentMessage.PayloadOneofCase.OverlayState, fromAgent.PayloadCase);
        Assert.Equal(state, fromAgent.OverlayState);
        Assert.Equal(state, fromHelper.OverlayState);

        // An Overlay written before M6 — kind and message only, field numbers 1 and 2 — is
        // read by this build with the new fields at their defaults.
        var old = new Overlay { Kind = Overlay.Types.Kind.Lock, Message = "old" }.ToByteArray();
        var parsed = Overlay.Parser.ParseFrom(old);
        Assert.Equal(string.Empty, parsed.SessionId);
        Assert.Equal(0, parsed.UntilUnix);
        Assert.Equal(0, parsed.HardLimitUnix);
        Assert.Equal(7, old.Length);                   // tag+varint for kind, tag+length+"old" for message: nothing else on the wire
        Assert.Equal(old, parsed.ToByteArray());       // and this build writes the same bytes back while the new fields are unset
    }

    [Fact]
    public void Every_job_kind_in_the_documentation_exists_in_the_contract()
    {
        string[] documented =
        [
            "Shutdown", "Reboot", "Logoff", "RunScript", "InstallPackage", "ResetProfile",
            "SendFile", "CollectFiles", "SetConfig", "SelfUpdate", "Rekey",
        ];

        var declared = Enum.GetNames<Job.Types.Kind>();

        Assert.All(documented, name => Assert.Contains(name, declared));
    }

    [Fact]
    public void A_departure_notice_carries_the_taker_and_nothing_a_pc_could_make_up()
    {
        // The report a PC sends on its way out when another teacher machine took it over
        // (D-58). It is an ordinary event, so nothing on the wire changes; the message is a
        // machine-readable payload the console checks rather than believes, because a PC
        // naming a holder is still only a claim.
        var now = DateTimeOffset.UtcNow;
        var taker = Guid.NewGuid().ToString("d");

        var notice = LabControl.Shared.Link.DepartureNotice.Create(taker, now);
        Assert.Equal("link.taken_over", notice.Code);
        Assert.Equal(Event.Types.Severity.Info, notice.Severity);
        Assert.Equal(taker, LabControl.Shared.Link.DepartureNotice.TakerOf(notice));

        // Round trip: the console reads it off the wire, not out of the object it built.
        var decoded = AgentMessage.Parser.ParseFrom(new AgentMessage { Event = notice }.ToByteArray());
        Assert.Equal(taker, LabControl.Shared.Link.DepartureNotice.TakerOf(decoded.Event));

        // Anything else is not a departure notice: another code, an empty payload, a
        // sentence, or a megabyte of it.
        Assert.Null(LabControl.Shared.Link.DepartureNotice.TakerOf(new Event { Code = "session.logon", Message = taker }));
        Assert.Null(LabControl.Shared.Link.DepartureNotice.TakerOf(new Event { Code = "link.taken_over", Message = "" }));
        Assert.Null(LabControl.Shared.Link.DepartureNotice.TakerOf(new Event { Code = "link.taken_over", Message = "Lab PC took over" }));
        Assert.Null(LabControl.Shared.Link.DepartureNotice.TakerOf(new Event { Code = "link.taken_over", Message = new string('a', 65) }));
    }
}
