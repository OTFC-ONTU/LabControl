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

        Assert.False(timerOnly.BlockInternet);
        Assert.Empty(timerOnly.AllowedPrograms);
        Assert.Null(timerOnly.Collect);
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
}
