using System.Net;
using LabControl.Console.Services;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Xunit;

namespace LabControl.Console.Tests;

public class UpdateTerminalReportTests
{
    [Fact]
    public void Unspecified_hello_is_refreshed_by_terminal_event_on_same_connection()
    {
        var connection = new AgentConnection(new Hello(), new MachineRecord(), "test", IPAddress.Loopback, DateTimeOffset.UtcNow);
        var publisher = new UpdateTerminalReport();
        Assert.Equal(UpdateState.Types.Phase.Unspecified, connection.UpdateState.Phase);
        Assert.Throws<IOException>(() => publisher.Read(() => throw new IOException("Locked"), "0.1.0"));
        var terminal = new UpdateTrialDocument { JobId = "job", Version = "0.2.0", Previous = "0.1.0", Phase = UpdateTrialPhase.RolledBack };
        Assert.True(connection.ApplyEvent(publisher.Read(() => terminal, "0.1.0")!));
        Assert.Equal(UpdateState.Types.Phase.RolledBack, connection.UpdateState.Phase);
        Assert.Equal("0.2.0", connection.UpdateState.FailedVersion);
        Assert.False(connection.ApplyEvent(new Event { Code = UpdateTerminalReport.RolledBackCode, Message = "untrusted arbitrary text" }));
        Assert.Equal("0.2.0", connection.UpdateState.FailedVersion);
        Assert.True(connection.ApplyEvent(new Event { Code = UpdateTerminalReport.StableCode }));
        Assert.Equal(UpdateState.Types.Phase.Stable, connection.UpdateState.Phase);
    }
}
