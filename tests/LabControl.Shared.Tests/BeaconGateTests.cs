using Xunit;

using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Link;
using LabControl.Shared.Protection;

namespace LabControl.Shared.Tests;

/// <summary>
/// The agent's beacon policy. Two promises are tested here: a beacon flood costs a PC
/// almost nothing (PROTOCOL, "Discovery beacon"), and <i>Take over the lab</i> moves a PC
/// between two teacher machines exactly once per press, so a console rebroadcasting the
/// same value for 30 s cannot cause a re-dial loop (ARCHITECTURE §3.7.2).
/// </summary>
public sealed class BeaconGateTests
{
    [Fact]
    public void An_unlinked_agent_dials_the_console_it_hears()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        using var console = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), now);
        var gate = new BeaconGate(LabTrustTests.PublicOnly(lab.Authority), lab.LabId);

        var verdict = gate.Consider(console.CreateBeacon("192.168.1.23", Defaults.ConsolePort, now).ToDatagram(), now);

        Assert.Equal(BeaconAction.Dial, verdict.Action);
        Assert.Equal($"192.168.1.23:{Defaults.ConsolePort}", verdict.Beacon!.Endpoint);
    }

    [Fact]
    public void A_beacon_flood_costs_one_dial_every_two_seconds()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        using var console = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), now);
        var gate = new BeaconGate(LabTrustTests.PublicOnly(lab.Authority), lab.LabId);

        var dials = 0;
        for (var i = 0; i < 1000; i++)
        {
            var at = now.AddMilliseconds(i * 10);   // 100 beacons a second for ten seconds
            if (gate.Consider(console.CreateBeacon("192.168.1.23", Defaults.ConsolePort, at), at).Action == BeaconAction.Dial)
            {
                dials++;
            }
        }

        Assert.InRange(dials, 1, (int)(TimeSpan.FromSeconds(10) / Defaults.MinDialInterval) + 1);
    }

    [Fact]
    public void A_forged_beacon_never_reaches_a_dial()
    {
        var now = DateTimeOffset.UtcNow;
        using var ours = TestLab.Create();
        using var theirs = TestLab.Create();
        using var impostor = ConsoleInstance.Mint(theirs, "Impostor", new FileSecretProtector(), now);

        var gate = new BeaconGate(LabTrustTests.PublicOnly(ours.Authority), ours.LabId);
        var beacon = impostor.CreateBeacon("192.168.1.99", Defaults.ConsolePort, now);
        beacon.LabId = ours.LabId;

        var verdict = gate.Consider(beacon, now);

        Assert.Equal(BeaconAction.Ignore, verdict.Action);
        Assert.Contains("endorsed", verdict.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BeaconAction.Ignore, gate.Consider("{}"u8, now).Action);
    }

    [Fact]
    public void A_linked_agent_ignores_the_other_console_until_it_takes_over()
    {
        var linkedAt = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        using var macBook = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), linkedAt);
        using var deskPc = ConsoleInstance.Mint(lab, "Lab PC", new FileSecretProtector(), linkedAt);

        var gate = new BeaconGate(LabTrustTests.PublicOnly(lab.Authority), lab.LabId);
        gate.Linked(macBook.InstanceId, $"192.168.1.23:{Defaults.ConsolePort}", linkedAt);

        // Its own console: nothing to do. The other console: two consoles hold disjoint
        // sets of PCs, so this one stays where it is (§3.7.2).
        var later = linkedAt.AddSeconds(10);
        Assert.Equal(BeaconAction.Ignore,
            gate.Consider(macBook.CreateBeacon("192.168.1.23", Defaults.ConsolePort, later), later).Action);
        Assert.Equal(BeaconAction.Ignore,
            gate.Consider(deskPc.CreateBeacon("192.168.1.40", Defaults.ConsolePort, later), later).Action);

        // Now the teacher at the desk PC presses Take over the lab.
        var pressed = linkedAt.AddSeconds(20);
        var takeBeacon = deskPc.CreateBeacon("192.168.1.40", Defaults.ConsolePort, pressed, pressed);
        var verdict = gate.Consider(takeBeacon, pressed);

        Assert.Equal(BeaconAction.TakeOver, verdict.Action);
        Assert.Contains("Lab PC", verdict.Reason, StringComparison.Ordinal);

        // The same press, rebroadcast for 30 s, is honoured only once.
        for (var i = 1; i <= 15; i++)
        {
            var at = pressed.AddSeconds(i * 2);
            var repeat = deskPc.CreateBeacon("192.168.1.40", Defaults.ConsolePort, at, pressed);
            Assert.Equal(BeaconAction.Ignore, gate.Consider(repeat, at).Action);
        }
    }

    [Fact]
    public void A_take_over_older_than_the_current_link_is_ignored()
    {
        var pressed = DateTimeOffset.UtcNow;
        var linkedAt = pressed.AddSeconds(5);

        using var lab = TestLab.Create();
        using var macBook = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), pressed);
        using var deskPc = ConsoleInstance.Mint(lab, "Lab PC", new FileSecretProtector(), pressed);

        var gate = new BeaconGate(LabTrustTests.PublicOnly(lab.Authority), lab.LabId);
        gate.Linked(macBook.InstanceId, $"192.168.1.23:{Defaults.ConsolePort}", linkedAt);

        // The PC connected to the MacBook *after* the desk PC pressed the button, so it
        // already answered the question — moving it back would ping-pong the room.
        var at = linkedAt.AddSeconds(1);
        Assert.Equal(BeaconAction.Ignore,
            gate.Consider(deskPc.CreateBeacon("192.168.1.40", Defaults.ConsolePort, at, pressed), at).Action);
    }

    [Fact]
    public void Two_consoles_pressing_the_button_move_the_room_back_and_forth()
    {
        var start = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        using var macBook = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), start);
        using var deskPc = ConsoleInstance.Mint(lab, "Lab PC", new FileSecretProtector(), start);

        var gate = new BeaconGate(LabTrustTests.PublicOnly(lab.Authority), lab.LabId);
        gate.Linked(macBook.InstanceId, "a", start);

        var firstPress = start.AddSeconds(10);
        Assert.Equal(BeaconAction.TakeOver,
            gate.Consider(deskPc.CreateBeacon("192.168.1.40", Defaults.ConsolePort, firstPress, firstPress), firstPress).Action);

        gate.Unlinked();
        gate.Linked(deskPc.InstanceId, "b", firstPress);

        var secondPress = start.AddSeconds(60);
        Assert.Equal(BeaconAction.TakeOver,
            gate.Consider(macBook.CreateBeacon("192.168.1.23", Defaults.ConsolePort, secondPress, secondPress), secondPress).Action);
    }

    [Fact]
    public void A_failed_dial_backs_off_and_a_link_clears_it()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        using var console = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), now);
        var gate = new BeaconGate(LabTrustTests.PublicOnly(lab.Authority), lab.LabId);
        const string endpoint = "192.168.1.23:47800";

        var first = gate.DialFailed(endpoint, now);
        var second = gate.DialFailed(endpoint, now);
        var third = gate.DialFailed(endpoint, now);

        Assert.True(second > first);
        Assert.True(third > second);
        Assert.True(third <= Defaults.ReconnectDelayMax * 1.25);

        // While backed off, a beacon from that endpoint is not dialled again.
        Assert.Equal(BeaconAction.Ignore,
            gate.Consider(console.CreateBeacon("192.168.1.23", Defaults.ConsolePort, now), now).Action);

        gate.Linked(console.InstanceId, endpoint, now);
        gate.Unlinked();
        Assert.Equal(BeaconAction.Dial,
            gate.Consider(console.CreateBeacon("192.168.1.23", Defaults.ConsolePort, now), now).Action);
    }

    [Fact]
    public void Backoff_grows_to_the_ceiling_and_resets()
    {
        var backoff = new ReconnectBackoff();

        var first = backoff.Next();
        Assert.InRange(first, Defaults.ReconnectDelayMin, Defaults.ReconnectDelayMin * 1.25);

        for (var i = 0; i < 20; i++)
        {
            Assert.True(backoff.Next() <= Defaults.ReconnectDelayMax * 1.25);
        }

        backoff.Reset();
        Assert.Equal(0, backoff.Attempts);
        Assert.InRange(backoff.Next(), Defaults.ReconnectDelayMin, Defaults.ReconnectDelayMin * 1.25);
    }
}
