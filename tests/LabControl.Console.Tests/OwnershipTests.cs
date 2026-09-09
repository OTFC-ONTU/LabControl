using Xunit;

using LabControl.Console.Services;
using LabControl.Console.ViewModels;
using LabControl.Shared;
using LabControl.Shared.Discovery;
using LabControl.Shared.Identity;
using LabControl.Shared.Link;
using LabControl.Shared.Setup;

namespace LabControl.Console.Tests;

/// <summary>
/// M5 portion 5 (D-58): <i>Take over the lab</i> works between teacher machines whose clocks
/// disagree, and a console says who holds a PC only when it has actually seen that happen.
/// Everything here runs two real consoles and real agents over loopback UDP and TLS.
/// </summary>
public sealed class OwnershipTests
{
    [Fact]
    public async Task A_console_whose_clock_is_behind_still_takes_the_room_and_gets_it_back()
    {
        // The desk PC's clock is half a minute behind everybody else's. Its `take` is
        // therefore older than the moment each agent linked to the MacBook, which is exactly
        // what used to make the press do nothing (D-58 item 1).
        var behind = TimeSpan.FromSeconds(-30);
        await using var macBook = await TestConsole.CreateLabAsync("MacBook-2026");
        var codes = macBook.IssueCodes(3);

        using var listener = new BeaconListener(TestConsole.BeaconPort);
        var agents = new List<TestAgent>();
        try
        {
            for (var n = 1; n <= 3; n++)
            {
                var agent = TestAgent.Install(macBook, n, codes[n - 1], pinHost: false);
                listener.Received += (datagram, _) => agent.Link.OfferBeacon(datagram);
                agents.Add(agent.Start());
            }

            listener.Start();
            Assert.True(await Wait.UntilAsync(() => macBook.Session.Linked.Count == 3, TimeSpan.FromSeconds(15)),
                $"{macBook.Session.Linked.Count} of 3 linked to the MacBook");

            await using var deskPc = await TestConsole.JoinLabAsync(macBook, "Lab PC", clock: () => DateTimeOffset.UtcNow + behind);
            Assert.True(await Wait.UntilAsync(() => macBook.Session.OtherConsoles.Any(o => o.Name == "Lab PC"), TimeSpan.FromSeconds(10)));

            deskPc.Session.TakeOver();
            Assert.True(await Wait.UntilAsync(() => deskPc.Session.Linked.Count == 3, TimeSpan.FromSeconds(15)),
                $"only {deskPc.Session.Linked.Count} of 3 followed a console whose clock is 30 s behind");
            Assert.All(agents, a => Assert.Equal(deskPc.Session.Instance.InstanceId, a.Link.LinkedInstanceId));

            // The take-over is honoured once: the same press keeps arriving for 30 s and the
            // room stays where it is instead of bouncing.
            await Task.Delay(Defaults.BeaconInterval * 2, TestContext.Current.CancellationToken);
            Assert.Equal(3, deskPc.Session.Linked.Count);

            // And the MacBook — whose clock is ahead of the desk PC's — takes it straight back.
            macBook.Session.TakeOver();
            Assert.True(await Wait.UntilAsync(() => macBook.Session.Linked.Count == 3, TimeSpan.FromSeconds(15)),
                $"only {macBook.Session.Linked.Count} of 3 came back");
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
    public async Task A_pc_that_is_simply_off_is_offline_here_and_unknown_while_another_console_runs()
    {
        await using var macBook = await TestConsole.CreateLabAsync("MacBook-2026");
        var code = macBook.IssueCodes(1)[0];

        var agent = TestAgent.Install(macBook, 7, code);
        agent.Start();
        Assert.True(await Wait.UntilAsync(() => macBook.Session.Linked.Count == 1, TimeSpan.FromSeconds(15)));

        var machine = Assert.Single(macBook.Session.Registry.Document.Machines);
        Assert.Equal(OwnershipKind.LinkedHere, macBook.Session.Ownership(machine).Kind);

        // The PC is switched off. Nobody took it over, so nobody is credited with it.
        await agent.DisposeAsync();
        Assert.True(await Wait.UntilAsync(() => macBook.Session.Linked.Count == 0, TimeSpan.FromSeconds(15)));

        var alone = macBook.Session.Ownership(machine);
        Assert.Equal(OwnershipKind.Offline, alone.Kind);
        Assert.Equal(string.Empty, alone.HolderName);

        // A second teacher machine appears, holding nothing and having taken nothing. The
        // old rule counted every PC not linked here as held by it; the honest answer is that
        // this console does not know where PC-07 is (D-58 item 2).
        await using var deskPc = await TestConsole.JoinLabAsync(macBook, "Lab PC");
        Assert.True(await Wait.UntilAsync(() => macBook.Session.OtherConsoles.Any(o => o.Name == "Lab PC"), TimeSpan.FromSeconds(10)));

        var withOther = macBook.Session.Ownership(machine);
        Assert.Equal(OwnershipKind.Unknown, withOther.Kind);
        Assert.Equal(string.Empty, withOther.HolderName);
        Assert.Empty(macBook.Session.ObservedElsewhere(deskPc.Session.Instance.InstanceId));

        var tile = new MachineTileViewModel(machine, macBook.Session.Screens.Get(machine.AgentId));
        tile.Refresh(machine, null, withOther, macBook.Session.Now);
        Assert.Equal(TileStatus.NotSeen, tile.Status);
        Assert.Contains("not seen", tile.StatusText, StringComparison.Ordinal);
        Assert.DoesNotContain("Lab PC", tile.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pc_watched_leaving_for_the_other_console_is_named_and_the_banner_counts_only_those()
    {
        await using var macBook = await TestConsole.CreateLabAsync("MacBook-2026");
        var codes = macBook.IssueCodes(2);

        using var listener = new BeaconListener(TestConsole.BeaconPort);
        var agents = new List<TestAgent>();
        try
        {
            for (var n = 1; n <= 2; n++)
            {
                var agent = TestAgent.Install(macBook, n, codes[n - 1], pinHost: false);
                listener.Received += (datagram, _) => agent.Link.OfferBeacon(datagram);
                agents.Add(agent.Start());
            }

            listener.Start();
            Assert.True(await Wait.UntilAsync(() => macBook.Session.Linked.Count == 2, TimeSpan.FromSeconds(15)),
                $"{macBook.Session.Linked.Count} of 2 linked");

            // A third PC this console knows from lab.json but has never had on the line: the
            // banner must not count it for anybody.
            macBook.Session.Registry.Persist(document => document.Machines.Add(
                new Shared.Persistence.MachineRecord { AgentId = Guid.NewGuid().ToString("d"), Number = 9 }));

            await using var deskPc = await TestConsole.JoinLabAsync(macBook, "Lab PC");
            Assert.True(await Wait.UntilAsync(() => macBook.Session.OtherConsoles.Any(o => o.Name == "Lab PC"), TimeSpan.FromSeconds(10)));

            deskPc.Session.TakeOver();
            Assert.True(await Wait.UntilAsync(() => deskPc.Session.Linked.Count == 2, TimeSpan.FromSeconds(15)));
            Assert.True(await Wait.UntilAsync(() => macBook.Session.Linked.Count == 0, TimeSpan.FromSeconds(15)));

            var deskInstance = deskPc.Session.Instance.InstanceId;
            Assert.True(await Wait.UntilAsync(() => macBook.Session.ObservedElsewhere(deskInstance).Count == 2, TimeSpan.FromSeconds(10)));

            var taken = macBook.Session.Registry.Document.Machines.Single(m => m.Number == 1);
            var never = macBook.Session.Registry.Document.Machines.Single(m => m.Number == 9);

            var observed = macBook.Session.Ownership(taken);
            Assert.Equal(OwnershipKind.ObservedElsewhere, observed.Kind);
            Assert.Equal("Lab PC", observed.HolderName);
            Assert.Equal(deskInstance, observed.Holder!.InstanceId);

            // The PC that was never on this console's line is not swept into the count.
            Assert.Equal(OwnershipKind.Unknown, macBook.Session.Ownership(never).Kind);
            Assert.DoesNotContain(macBook.Session.ObservedElsewhere(deskInstance), m => m.Number == 9);

            var tile = new MachineTileViewModel(taken, macBook.Session.Screens.Get(taken.AgentId));
            tile.Refresh(taken, null, observed, macBook.Session.Now);
            Assert.Equal(TileStatus.HeldElsewhere, tile.Status);
            Assert.Contains("Lab PC", tile.StatusText, StringComparison.Ordinal);

            // The banner: three PCs in the list, two positively observed, and the wording
            // says "at least" because the third could be anywhere (D-58 item 2).
            var banner = await BannerAsync(macBook, "other:" + deskInstance);
            Assert.Contains("at least 2 of 3", banner, StringComparison.Ordinal);
            Assert.Contains("PC-01, PC-02", banner, StringComparison.Ordinal);
            Assert.DoesNotContain("PC-09", banner, StringComparison.Ordinal);
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
    public async Task A_console_that_has_taken_nothing_says_so_in_the_banner()
    {
        await using var macBook = await TestConsole.CreateLabAsync("MacBook-2026");
        var code = macBook.IssueCodes(1)[0];

        await using var agent = TestAgent.Install(macBook, 4, code);
        agent.Start();
        Assert.True(await Wait.UntilAsync(() => macBook.Session.Linked.Count == 1, TimeSpan.FromSeconds(15)));

        await using var deskPc = await TestConsole.JoinLabAsync(macBook, "Lab PC");
        Assert.True(await Wait.UntilAsync(() => macBook.Session.OtherConsoles.Any(o => o.Name == "Lab PC"), TimeSpan.FromSeconds(10)));

        var banner = await BannerAsync(macBook, "other:" + deskPc.Session.Instance.InstanceId);
        Assert.Contains("does not know which PCs it holds", banner, StringComparison.Ordinal);
    }

    /// <summary>
    /// The text of one banner, off a main view model built over the running session. There
    /// is no dispatcher here, so the lock stands in for the UI thread: the session raises its
    /// events from gRPC threads and every posted refresh must still be serialized with the
    /// test's own ticks.
    /// </summary>
    private static async Task<string> BannerAsync(TestConsole console, string key)
    {
        var gate = new Lock();
        MainViewModel view;
        lock (gate)
        {
            view = new MainViewModel(console.Session, console.Bootstrap, new SilentDialogs(), action =>
            {
                lock (gate)
                {
                    action();
                }
            });
        }

        Assert.True(await Wait.UntilAsync(() =>
        {
            lock (gate)
            {
                view.Tick();
                return view.Banners.Any(b => b.Key == key);
            }
        }, TimeSpan.FromSeconds(10)), "no banner for " + key);

        lock (gate)
        {
            var text = view.Banners.First(b => b.Key == key).Text;
            view.Detach();
            return text;
        }
    }

    /// <summary>Every dialog cancelled: these tests only read what the view model computed.</summary>
    private sealed class SilentDialogs : IDialogs
    {
        public Task<UnlockAnswer?> UnlockAsync(string reason) => Task.FromResult<UnlockAnswer?>(null);

        public Task<HolderAnswer?> AddHolderAsync() => Task.FromResult<HolderAnswer?>(null);

        public Task<string?> AskTextAsync(string title, string prompt, string initial = "", bool secret = false) => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string title, string message, string confirmLabel, bool destructive = false) => Task.FromResult(false);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowRecoveryCodeAsync(RecoveryCode code) => Task.FromResult(false);

        public Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension) => Task.FromResult<string?>(null);

        public Task<string?> PickOpenFileAsync(string title, string extension) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<string>> PickOpenFilesAsync(string title, IReadOnlyList<FileFilter> filters) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task ShowImportResultsAsync(IReadOnlyList<ImportFileResult> results) => Task.CompletedTask;

        public Task<SendFilesAnswer?> SendFilesAsync(int pcCount) => Task.FromResult<SendFilesAnswer?>(null);

        public Task<string?> PickFolderAsync(string title) => Task.FromResult<string?>(null);

        public Task<AgentBuild?> PushAgentBuildAsync(int pcCount) => Task.FromResult<AgentBuild?>(null);

        public void ShowScreen(ScreenViewModel screen)
        {
        }
    }
}
