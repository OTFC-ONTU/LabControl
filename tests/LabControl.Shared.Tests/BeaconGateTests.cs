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

        // The first verified beacon of this lab after those failures is new information — the
        // room is being served — so it steps over the wait once (M5 portion 8); the next one
        // of the same uninterrupted stream does not.
        Assert.Equal(BeaconAction.Dial,
            gate.Consider(console.CreateBeacon("192.168.1.23", Defaults.ConsolePort, now), now).Action);
        var soon = now + TimeSpan.FromSeconds(1);
        Assert.Equal(BeaconAction.Ignore,
            gate.Consider(console.CreateBeacon("192.168.1.23", Defaults.ConsolePort, soon), soon).Action);

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

    /// <summary>
    /// M5 portion 8, finding E: a PC whose lab was not the active one spends a whole lesson
    /// being refused, so its dial backoff sits at the 30-second ceiling. When its own room is
    /// served again, the first verified beacon of that lab must get it back inside the
    /// 15-second target instead of making it wait out the ceiling — and must not turn into a
    /// dial every two seconds while a console that is up simply cannot be linked.
    /// </summary>
    [Fact]
    public void A_beacon_after_a_gap_steps_over_a_grown_backoff_once_and_a_steady_stream_never_does()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        using var console = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), now);
        var gate = new BeaconGate(LabTrustTests.PublicOnly(lab.Authority), lab.LabId);
        const string endpoint = "192.168.1.23:47800";

        // The lesson in the other room: this PC hears its own lab's beacon once, links, and
        // is then refused over and over while the console serves the other lab.
        Assert.Equal(BeaconAction.Dial, gate.Consider(console.CreateBeacon("192.168.1.23", Defaults.ConsolePort, now), now).Action);
        gate.Linked(console.InstanceId, endpoint, now);
        gate.Unlinked();

        var refusing = now;
        TimeSpan wait = default;
        for (var i = 0; i < 12; i++)
        {
            wait = gate.DialFailed(endpoint, refusing);
            refusing += wait;
        }

        Assert.True(wait >= Defaults.ReconnectDelayMax * 0.75, $"the backoff reached only {wait}");

        // One last refusal, so the endpoint is sitting on the full ceiling when the room
        // comes back two seconds later — the case the 15-second target is about.
        gate.DialFailed(endpoint, refusing);

        // The room is served again. The gap in this lab's own beacons is the whole lesson.
        var back = refusing + TimeSpan.FromSeconds(2);
        var verdict = gate.Consider(console.CreateBeacon("192.168.1.23", Defaults.ConsolePort, back), back);
        Assert.Equal(BeaconAction.Dial, verdict.Action);
        Assert.Contains("served again", verdict.Reason, StringComparison.Ordinal);

        // That dial fails too, and the console keeps beaconing without a pause — a console
        // that is up but cannot link this PC. The escalation carries on from where it was
        // rather than restarting at the floor, and no further beacon shortens it, so five
        // minutes of beacons cost dials at the ceiling's pace, not at the beacon's.
        var next = gate.DialFailed(endpoint, back);
        Assert.True(next >= Defaults.ReconnectDelayMax * 0.75, $"the escalation restarted at {next}");

        var beacons = (int)(TimeSpan.FromMinutes(5) / Defaults.BeaconInterval);
        var dials = 0;
        for (var i = 1; i <= beacons; i++)
        {
            var at = back + Defaults.BeaconInterval * i;
            if (gate.Consider(console.CreateBeacon("192.168.1.23", Defaults.ConsolePort, at), at).Action == BeaconAction.Dial)
            {
                dials++;
                gate.DialFailed(endpoint, at);
            }
        }

        var ceiling = (int)(TimeSpan.FromMinutes(5) / (Defaults.ReconnectDelayMax * 0.75)) + 1;
        Assert.InRange(dials, 0, ceiling);
        Assert.True(dials < beacons / 4, $"{dials} dials for {beacons} beacons");
    }

    /// <summary>The step-over is armed again by every serving gap, so switching back and forth stays fast.</summary>
    [Fact]
    public void Every_serving_gap_arms_the_step_over_again()
    {
        var now = DateTimeOffset.UtcNow;
        using var lab = TestLab.Create();
        using var console = ConsoleInstance.Mint(lab, "MacBook-2026", new FileSecretProtector(), now);
        var gate = new BeaconGate(LabTrustTests.PublicOnly(lab.Authority), lab.LabId);
        const string endpoint = "192.168.1.23:47800";

        var at = now;
        for (var round = 0; round < 5; round++)
        {
            // Refused for a while: the wait grows past the beacon interval.
            for (var i = 0; i < 6; i++)
            {
                at += gate.DialFailed(endpoint, at);
            }

            // The room comes back after a lesson elsewhere.
            at += TimeSpan.FromMinutes(10);
            Assert.Equal(BeaconAction.Dial, gate.Consider(console.CreateBeacon("192.168.1.23", Defaults.ConsolePort, at), at).Action);
        }
    }

    /// <summary>A beacon of another lab is not evidence of anything, backoff included.</summary>
    [Fact]
    public void A_beacon_of_another_lab_never_shortens_the_backoff()
    {
        var now = DateTimeOffset.UtcNow;
        using var ours = TestLab.Create();
        using var theirs = TestLab.Create();
        using var stranger = ConsoleInstance.Mint(theirs, "Other room", new FileSecretProtector(), now);
        var gate = new BeaconGate(LabTrustTests.PublicOnly(ours.Authority), ours.LabId);
        const string endpoint = "192.168.1.23:47800";

        for (var i = 0; i < 12; i++)
        {
            gate.DialFailed(endpoint, now);
        }

        var later = now + TimeSpan.FromMinutes(10);
        Assert.Equal(BeaconAction.Ignore, gate.Consider(stranger.CreateBeacon("192.168.1.23", Defaults.ConsolePort, later), later).Action);
    }

}
