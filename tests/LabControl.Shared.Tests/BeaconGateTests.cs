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

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(-30)]
    [InlineData(-60)]
    public void A_take_over_from_a_console_whose_clock_is_wrong_still_moves_the_room(int skewSeconds)
    {
        // The desk PC's clock is out by this much. Under the pre-M5 rule its press was
        // compared with the moment this PC linked, so a console behind the PC's clock could
        // never take its own room back; the rule now compares only arrival order (D-58).
        var skew = TimeSpan.FromSeconds(skewSeconds);
        var linkedAt = Whole(DateTimeOffset.UtcNow);

        using var lab = TestLab.Create();
        using var macBook = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), linkedAt);
        using var deskPc = ConsoleInstance.Mint(lab, "Lab PC", new FileSecretProtector(), linkedAt);

        var gate = new BeaconGate(LabTrustTests.PublicOnly(lab.Authority), lab.LabId);
        gate.Linked(macBook.InstanceId, $"192.168.1.23:{Defaults.ConsolePort}", linkedAt);

        // The press happens ten seconds later, timestamped on the taker's own wrong clock.
        var arrives = linkedAt.AddSeconds(10);
        var pressed = arrives + skew;
        var beacon = deskPc.CreateBeacon("192.168.1.40", Defaults.ConsolePort, pressed, pressed);

        var verdict = gate.Consider(beacon, arrives);

        Assert.Equal(BeaconAction.TakeOver, verdict.Action);
        Assert.Contains("Lab PC", verdict.Reason, StringComparison.Ordinal);

        // And still exactly once, however wrong the clock is.
        Assert.Equal(BeaconAction.Ignore, gate.Consider(beacon, arrives.AddSeconds(2)).Action);
    }

    [Fact]
    public void A_press_made_just_before_the_pc_linked_elsewhere_still_moves_it()
    {
        // The teacher pressed Take over while this PC was still dialling the other console.
        // The press is older than the link, which used to end the matter; what counts now is
        // that the beacon arrived afterwards and still belongs to the open window (D-58).
        var pressed = Whole(DateTimeOffset.UtcNow);
        var linkedAt = pressed.AddSeconds(5);

        using var lab = TestLab.Create();
        using var macBook = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), pressed);
        using var deskPc = ConsoleInstance.Mint(lab, "Lab PC", new FileSecretProtector(), pressed);

        var gate = new BeaconGate(LabTrustTests.PublicOnly(lab.Authority), lab.LabId);
        gate.Linked(macBook.InstanceId, $"192.168.1.23:{Defaults.ConsolePort}", linkedAt);

        var arrives = linkedAt.AddSeconds(1);
        Assert.Equal(BeaconAction.TakeOver,
            gate.Consider(deskPc.CreateBeacon("192.168.1.40", Defaults.ConsolePort, arrives, pressed), arrives).Action);
    }

    [Fact]
    public void A_take_over_that_arrived_before_the_link_came_up_is_ignored()
    {
        // A datagram already in flight when the link was established asked a question the
        // link itself has since answered; only arrival order on this PC's own clock decides.
        var pressed = Whole(DateTimeOffset.UtcNow);
        var arrives = pressed.AddSeconds(1);
        var linkedAt = arrives.AddSeconds(2);

        using var lab = TestLab.Create();
        using var macBook = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), pressed);
        using var deskPc = ConsoleInstance.Mint(lab, "Lab PC", new FileSecretProtector(), pressed);

        var gate = new BeaconGate(LabTrustTests.PublicOnly(lab.Authority), lab.LabId);
        gate.Linked(macBook.InstanceId, $"192.168.1.23:{Defaults.ConsolePort}", linkedAt);

        var verdict = gate.Consider(deskPc.CreateBeacon("192.168.1.40", Defaults.ConsolePort, arrives, pressed), arrives);

        Assert.Equal(BeaconAction.Ignore, verdict.Action);
        Assert.Contains("in flight", verdict.Reason, StringComparison.Ordinal);

        // The token was not spent on it: the next beacon of the same press, arriving after
        // the link, is the one that moves the room.
        var later = linkedAt.AddSeconds(2);
        Assert.Equal(BeaconAction.TakeOver,
            gate.Consider(deskPc.CreateBeacon("192.168.1.40", Defaults.ConsolePort, later, pressed), later).Action);
    }

    [Fact]
    public void A_press_that_does_not_belong_to_its_beacon_is_ignored()
    {
        // `ts` is already within +/-BeaconMaxSkew of this PC's clock, so bounding `take`
        // against `ts` bounds how stale a press may be without ever reading the taker's
        // clock as if it were ours (D-58).
        var linkedAt = Whole(DateTimeOffset.UtcNow);
        var bound = Defaults.TakeOverWindow + Defaults.BeaconMaxSkew;

        using var lab = TestLab.Create();
        using var macBook = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), linkedAt);
        using var deskPc = ConsoleInstance.Mint(lab, "Lab PC", new FileSecretProtector(), linkedAt);

        var gate = new BeaconGate(LabTrustTests.PublicOnly(lab.Authority), lab.LabId);
        gate.Linked(macBook.InstanceId, $"192.168.1.23:{Defaults.ConsolePort}", linkedAt);

        var sent = linkedAt.AddSeconds(10);
        var tooOld = deskPc.CreateBeacon("192.168.1.40", Defaults.ConsolePort, sent, sent - bound - TimeSpan.FromSeconds(1));
        var tooNew = deskPc.CreateBeacon("192.168.1.40", Defaults.ConsolePort, sent, sent + bound + TimeSpan.FromSeconds(1));
        var justInside = deskPc.CreateBeacon("192.168.1.40", Defaults.ConsolePort, sent, sent - bound);

        Assert.Equal(BeaconAction.Ignore, gate.Consider(tooOld, sent).Action);
        Assert.Contains("does not belong", gate.Consider(tooNew, sent).Reason, StringComparison.Ordinal);
        Assert.Equal(BeaconAction.TakeOver, gate.Consider(justInside, sent).Action);
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

    /// <summary>
    /// Whole seconds: a beacon's timestamps are unix seconds, so a test that works to the
    /// exact skew limit must not lose a fraction of a second in the truncation.
    /// </summary>
    private static DateTimeOffset Whole(DateTimeOffset instant) => DateTimeOffset.FromUnixTimeSeconds(instant.ToUnixTimeSeconds());
}
