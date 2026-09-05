using LabControl.Console.Services;
using LabControl.Shared.Link;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// The session half of M2 as the console sees it: who is logged on, locked or not, whether
/// the helper is up — arriving as <c>SessionState</c> and surviving a reconnect (D-30).
/// </summary>
public class SessionStateTests
{
    [Fact]
    public async Task Session_changes_reach_the_connection_and_the_events_panel()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var code = console.IssueCodes(1)[0];
        await using var pc = TestAgent.Install(console, 3, code).Start();
        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked));

        pc.Link.PublishSessionState(new SessionState { Kind = SessionState.Types.Kind.Unspecified, User = "student", SessionId = 1, HelperAlive = true, Locked = false });
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId)?.HelperAlive == true));

        var connection = console.Session.FindLinked(pc.AgentId)!;
        Assert.Equal("student", connection.Machine.LoggedOnUser);
        Assert.False(connection.SessionLocked);

        pc.Link.PublishSessionState(new SessionState { Kind = SessionState.Types.Kind.Lock, User = "student", SessionId = 1, HelperAlive = true, Locked = true });
        Assert.True(await Wait.UntilAsync(() => connection.SessionLocked));
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "session.lock" && e.Number == 3);

        pc.Link.PublishSessionState(new SessionState { Kind = SessionState.Types.Kind.Logoff, User = string.Empty, SessionId = 1, HelperAlive = false, Locked = false });
        Assert.True(await Wait.UntilAsync(() => connection.HelperAlive == false));
        Assert.Null(connection.Machine.LoggedOnUser);
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "session.logoff");
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "session.helper_down");

        pc.Link.PublishSessionState(new SessionState { Kind = SessionState.Types.Kind.Logon, User = "student", SessionId = 1, HelperAlive = true, Locked = false });
        Assert.True(await Wait.UntilAsync(() => connection.HelperAlive == true));
        Assert.Contains(console.Session.Events.Recent, e => e.Code == "session.logon" && e.Message.Contains("student", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_latest_session_state_is_re_sent_after_a_reconnect_and_before_the_first_link()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var code = console.IssueCodes(1)[0];
        await using var pc = TestAgent.Install(console, 5, code);

        // Published before the PC has ever linked — the supervisor starts before the first beacon.
        pc.Link.PublishSessionState(new SessionState { User = "student", SessionId = 2, HelperAlive = true, Locked = true });
        pc.Start();

        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId)?.SessionLocked == true));
        Assert.Equal("student", console.Session.FindLinked(pc.AgentId)!.Machine.LoggedOnUser);

        // The link drops; a fresh connection object must learn the state again on its own.
        pc.Link.Disconnect("simulated cable pull");
        Assert.True(await Wait.UntilAsync(() => !console.Session.IsLinked(pc.AgentId)));
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId)?.SessionLocked == true, TimeSpan.FromSeconds(20)));
        Assert.True(console.Session.FindLinked(pc.AgentId)!.HelperAlive);
    }
}
