using Xunit;

using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Discovery;
using LabControl.Shared.Link;

namespace LabControl.Console.Tests;

/// <summary>
/// Discovery over real UDP on this machine: agents find the console by its beacon, and
/// <i>Take over the lab</i> moves every PC from one console to the other (ARCHITECTURE
/// §3.7.2). These use a process-specific UDP port, isolated from an open desktop console.
/// </summary>
public sealed class BeaconTests
{
    [Fact]
    public async Task Agents_find_the_console_by_its_beacon_and_follow_a_take_over()
    {
        await using var a = await TestConsole.CreateLabAsync("Console A");
        var codes = a.IssueCodes(3);

        using var listener = new BeaconListener(TestConsole.BeaconPort);
        var agents = new List<TestAgent>();
        try
        {
            for (var n = 1; n <= 3; n++)
            {
                var agent = TestAgent.Install(a, n, codes[n - 1], pinHost: false);
                listener.Received += (datagram, _, receivedAt) => agent.Link.OfferBeacon(datagram, receivedAt);
                agents.Add(agent.Start());
            }

            listener.Start();
            Assert.True(listener.IsListening);

            Assert.True(await Wait.UntilAsync(() => a.Session.Linked.Count == 3, TimeSpan.FromSeconds(15)),
                $"{a.Session.Linked.Count} of 3 found console A by its beacon");

            // B starts while A holds the room: both see each other, nothing moves.
            await using var b = await TestConsole.JoinLabAsync(a, "Console B");
            Assert.True(await Wait.UntilAsync(() => a.Session.OtherConsoles.Any(o => o.Name == "Console B"), TimeSpan.FromSeconds(10)));
            Assert.True(await Wait.UntilAsync(() => b.Session.OtherConsoles.Any(o => o.Name == "Console A"), TimeSpan.FromSeconds(10)));
            await Task.Delay(Defaults.BeaconInterval * 2, TestContext.Current.CancellationToken);
            Assert.Equal(3, a.Session.Linked.Count);
            Assert.Empty(b.Session.Linked);

            // B knows nothing about the PCs yet — its list is a cache it fills from Hello.
            Assert.Empty(b.Session.Registry.Document.Machines);

            b.Session.TakeOver();

            Assert.True(await Wait.UntilAsync(() => b.Session.Linked.Count == 3, TimeSpan.FromSeconds(15)), $"{b.Session.Linked.Count} of 3 moved to B");
            Assert.True(await Wait.UntilAsync(() => a.Session.Linked.Count == 0, TimeSpan.FromSeconds(15)));
            Assert.All(agents, agent => Assert.Equal(b.Session.Instance.InstanceId, agent.Link.LinkedInstanceId));
            Assert.Contains(a.Session.Events.Recent, e => e.Code == "console.taken_over" && e.Message.Contains("Console B", StringComparison.Ordinal));

            // A watched all three leave during B's own take-over, so its banner credits B
            // with exactly those three — positively observed, not presumed (M5 §4.6, D-58).
            // A's link count drops before the departure is attributed, so this waits.
            Assert.True(await Wait.UntilAsync(
                () => a.Session.Registry.Document.Machines.All(m => a.Session.Ownership(m).Kind == OwnershipKind.ObservedElsewhere),
                TimeSpan.FromSeconds(10)),
                "A does not credit every PC to B: " +
                string.Join(", ", a.Session.Registry.Document.Machines.Select(m => m.Number + "=" + a.Session.Ownership(m).Kind)));
            Assert.Equal(3, a.Session.ObservedElsewhere(b.Session.Instance.InstanceId).Count);
            Assert.Equal(3, b.Session.Registry.Document.Machines.Count);

            // The same take-over value is not honoured twice: the room stays with B.
            await Task.Delay(Defaults.BeaconInterval * 2, TestContext.Current.CancellationToken);
            Assert.Equal(3, b.Session.Linked.Count);
            Assert.All(agents, agent => Assert.Equal(LinkState.Linked, agent.Link.State));
        }
        finally
        {
            foreach (var agent in agents)
            {
                await agent.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task A_forged_beacon_is_dropped_without_a_dial()
    {
        await using var ours = await TestConsole.CreateLabAsync("Ours");
        await using var theirs = await TestConsole.CreateLabAsync("Theirs");
        var code = ours.IssueCodes(1)[0];

        using var listener = new BeaconListener(TestConsole.BeaconPort);
        await using var agent = TestAgent.Install(ours, 1, code, pinHost: false);

        var dials = 0;
        var heard = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<string>();
        listener.Failed += failures.Enqueue;
        listener.Received += (datagram, _, _) =>
        {
            // Count what the gate would do with each datagram before handing it over.
            if (Beacon.TryParse(datagram.Span, out var beacon))
            {
                heard.AddOrUpdate(beacon.InstanceName + "@" + beacon.Endpoint, 1, (_, n) => n + 1);
            }

            if (beacon is not null && beacon.InstanceName == "Theirs")
            {
                var gate = new BeaconGate(agent.Store.Authority, agent.Store.Config.LabId, new LabControl.Shared.Identity.RevocationSet());
                if (gate.Consider(beacon, DateTimeOffset.UtcNow).Action == BeaconAction.Dial)
                {
                    Interlocked.Increment(ref dials);
                }
            }

            agent.Link.OfferBeacon(datagram);
        };

        listener.Start();
        agent.Start();

        Assert.True(await Wait.UntilAsync(() => agent.Link.State == LinkState.Linked, TimeSpan.FromSeconds(15)),
            "not linked; console events: " + string.Join(" | ", ours.Session.Events.Recent.Select(e => e.Message)) +
            "; agent refusals: " + string.Join(" | ", agent.Refusals) + "; listening=" + listener.IsListening +
            "; sockets: " + BeaconListener.Describe() + "; state=" + agent.Link.State +
            "; heard: " + string.Join(", ", heard.Select(kv => kv.Key + "=" + kv.Value)) + "; ours=" + ours.Port + "; listener failures: " + string.Join(" | ", failures));
        Assert.Equal(ours.Session.Instance.InstanceId, agent.Link.LinkedInstanceId);
        Assert.Equal(0, dials);
        Assert.Empty(theirs.Session.Linked);
        Assert.DoesNotContain(theirs.Session.Events.Recent, e => e.Code.StartsWith("link.", StringComparison.Ordinal));
    }
}
