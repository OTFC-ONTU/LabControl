using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Packaging;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// The <i>LAN access</i> banner's state machine (M5 portion 7, D-59 item 2), driven by a
/// fake firewall. The real COM path is Windows-only and is exercised on a Windows machine;
/// what is asserted here is that a refusal, a failure or a missing helper always ends in a
/// banner the teacher can act on, and never in an exception out of the console.
/// </summary>
public class NetworkReadinessTests
{
    private sealed class FakeFirewall : IConsoleFirewallProbe
    {
        public bool IsSupported { get; init; } = true;

        public bool Allowed { get; set; }

        public Exception? CheckThrows { get; set; }

        public string? Helper { get; set; } = @"C:\Programs\LabControl\Console\LabControl.ConsoleSetup.exe";

        public Exception? RequestThrows { get; set; }

        /// <summary>What the elevated helper answers; when it succeeds it also opens the ports, unless <see cref="Lies"/>.</summary>
        public bool Grants { get; set; } = true;

        /// <summary>What a run that did happen reported. <see cref="Grants"/> false means Windows never started it.</summary>
        public ConsoleElevationOutcome Outcome { get; set; } = ConsoleElevationOutcome.Completed;

        /// <summary>The helper opened the ports even though it reported a failure — a closed window after two added rules.</summary>
        public bool OpensPortsAnyway { get; set; }

        public bool Lies { get; set; }

        public int Checks { get; private set; }

        public int Requests { get; private set; }

        public ConsoleFirewallStatus Check()
        {
            Checks++;
            if (CheckThrows is { } error)
            {
                throw error;
            }

            return Allowed
                ? ConsoleFirewallStatus.AllAllowed
                : new ConsoleFirewallStatus(false, ConsoleFirewallRules.Required, []);
        }

        public string? FindHelper() => Helper;

        public ConsoleElevationOutcome RequestElevated(string helper)
        {
            Requests++;
            Assert.Equal(Helper, helper);
            if (RequestThrows is { } error)
            {
                throw error;
            }

            if (!Grants)
            {
                return ConsoleElevationOutcome.Refused;
            }

            if (Outcome == ConsoleElevationOutcome.Completed && !Lies)
            {
                Allowed = true;
            }
            else if (OpensPortsAnyway)
            {
                Allowed = true;
            }

            return Outcome;
        }
    }

    private static (NetworkReadiness Readiness, List<NetworkReadinessState> Seen) Watch(FakeFirewall firewall)
    {
        var readiness = new NetworkReadiness(firewall);
        var seen = new List<NetworkReadinessState>();
        readiness.Changed += () => seen.Add(readiness.State);
        return (readiness, seen);
    }

    [Fact]
    public void On_macOS_and_Linux_there_is_nothing_to_ask_for()
    {
        var firewall = new FakeFirewall { IsSupported = false };
        var (readiness, seen) = Watch(firewall);

        Assert.Equal(NetworkReadinessState.NotApplicable, readiness.State);
        readiness.Check();
        readiness.Allow();

        Assert.Equal(NetworkReadinessState.NotApplicable, readiness.State);
        Assert.False(readiness.HasBanner);
        Assert.Empty(seen);
        Assert.Equal(0, firewall.Checks);
        Assert.Equal(0, firewall.Requests);
    }

    [Fact]
    public void Allowed_ports_show_nothing_at_all()
    {
        var firewall = new FakeFirewall { Allowed = true };
        var (readiness, _) = Watch(firewall);

        readiness.Check();

        Assert.Equal(NetworkReadinessState.Allowed, readiness.State);
        Assert.False(readiness.HasBanner);
        Assert.Empty(readiness.Diagnostic);
    }

    [Fact]
    public void A_blocked_port_offers_the_banner_and_Allow_opens_it()
    {
        var firewall = new FakeFirewall();
        var (readiness, seen) = Watch(firewall);

        readiness.Check();
        Assert.Equal(NetworkReadinessState.Missing, readiness.State);
        Assert.True(readiness.HasBanner);

        readiness.Allow();

        Assert.Equal(NetworkReadinessState.Allowed, readiness.State);
        Assert.False(readiness.HasBanner);
        Assert.Equal(1, firewall.Requests);
        Assert.Equal([NetworkReadinessState.Missing, NetworkReadinessState.Allowed], seen);
    }

    [Fact]
    public void A_refused_elevation_becomes_the_two_commands_and_not_an_error()
    {
        var firewall = new FakeFirewall { Grants = false };
        var (readiness, _) = Watch(firewall);

        readiness.Check();
        readiness.Allow();

        Assert.Equal(NetworkReadinessState.Denied, readiness.State);
        Assert.True(readiness.HasBanner);
        Assert.Equal(ConsoleFirewallRules.NetshLines(), readiness.Diagnostic);
        Assert.All(readiness.Diagnostic, line => Assert.StartsWith("netsh advfirewall", line, StringComparison.Ordinal));
    }

    [Fact]
    public void A_console_without_its_installer_beside_it_says_so_with_the_commands()
    {
        var firewall = new FakeFirewall { Helper = null };
        var (readiness, _) = Watch(firewall);

        readiness.Check();
        readiness.Allow();

        Assert.Equal(NetworkReadinessState.Denied, readiness.State);
        Assert.Equal(2, readiness.Diagnostic.Count);
        // Nothing was launched: there was nothing to launch.
        Assert.Equal(0, firewall.Requests);
    }

    [Fact]
    public void Only_the_rules_themselves_prove_the_helper_worked()
    {
        var firewall = new FakeFirewall { Lies = true };
        var (readiness, _) = Watch(firewall);

        readiness.Check();
        readiness.Allow();

        Assert.Equal(NetworkReadinessState.Failed, readiness.State);
        Assert.Equal(ConsoleFirewallRules.NetshLines(), readiness.Diagnostic);
    }

    [Fact]
    public void A_firewall_that_will_not_answer_is_a_banner_not_a_crash()
    {
        var firewall = new FakeFirewall { CheckThrows = new IOException("the service is stopped") };
        var (readiness, _) = Watch(firewall);

        readiness.Check();

        Assert.Equal(NetworkReadinessState.Failed, readiness.State);
        Assert.Equal(ConsoleFirewallRules.NetshLines(), readiness.Diagnostic);
    }

    [Fact]
    public void A_launch_that_throws_is_a_banner_too()
    {
        var firewall = new FakeFirewall
        {
            RequestThrows = new System.ComponentModel.Win32Exception("the shell refused to start it"),
        };
        var (readiness, _) = Watch(firewall);

        readiness.Check();
        readiness.Allow();

        Assert.Equal(NetworkReadinessState.Failed, readiness.State);
    }

    [Fact]
    public async Task Checking_again_recovers_after_the_teacher_ran_the_commands_by_hand()
    {
        var firewall = new FakeFirewall { Grants = false };
        var readiness = new NetworkReadiness(firewall);

        await readiness.CheckAsync();
        await readiness.AllowAsync();
        Assert.Equal(NetworkReadinessState.Denied, readiness.State);

        // The teacher pasted the two netsh lines into an elevated prompt.
        firewall.Allowed = true;
        await readiness.CheckAsync();

        Assert.Equal(NetworkReadinessState.Allowed, readiness.State);
        Assert.False(readiness.HasBanner);
    }

    [Fact]
    public void Repeating_the_same_answer_does_not_flicker_the_banner()
    {
        var firewall = new FakeFirewall();
        var (readiness, seen) = Watch(firewall);

        readiness.Check();
        readiness.Check();
        readiness.Check();

        Assert.Equal([NetworkReadinessState.Missing], seen);
        Assert.Equal(3, firewall.Checks);
    }

    [Theory]
    [InlineData("Network.Blocked")]
    [InlineData("Network.Allow")]
    [InlineData("Network.Recheck")]
    [InlineData("Network.Denied")]
    [InlineData("Network.Failed")]
    public void Every_banner_string_is_in_the_resource_file(string key)
    {
        var text = LabControl.Console.Localization.Strings.Get(key);

        Assert.NotEqual("!" + key + "!", text);
        Assert.NotEmpty(text);
    }

    [Fact]
    public void The_diagnostic_banners_have_a_place_for_the_commands()
    {
        foreach (var key in new[] { "Network.Denied", "Network.Failed" })
        {
            Assert.Contains("{0}", LabControl.Console.Localization.Strings.Get(key), StringComparison.Ordinal);
        }

        Assert.DoesNotContain("{0}", LabControl.Console.Localization.Strings.Get("Network.Blocked"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_helper_that_exited_non_zero_is_believed_only_by_the_rules()
    {
        // The teacher added both rules and then closed the window, so the helper exited with a
        // failure. The ports are open; saying "Denied" here would send a teacher to an elevated
        // prompt to add rules that are already there.
        var firewall = new FakeFirewall { Outcome = ConsoleElevationOutcome.Failed, OpensPortsAnyway = true };
        var (readiness, _) = Watch(firewall);

        readiness.Check();
        readiness.Allow();

        Assert.Equal(NetworkReadinessState.Allowed, readiness.State);
        Assert.False(readiness.HasBanner);
        Assert.Equal(2, firewall.Checks);
    }

    [Fact]
    public void A_helper_that_exited_non_zero_and_changed_nothing_still_shows_the_commands()
    {
        var firewall = new FakeFirewall { Outcome = ConsoleElevationOutcome.Failed };
        var (readiness, _) = Watch(firewall);

        readiness.Check();
        readiness.Allow();

        Assert.Equal(NetworkReadinessState.Denied, readiness.State);
        Assert.Equal(ConsoleFirewallRules.NetshLines(), readiness.Diagnostic);
    }

    [Fact]
    public async Task A_probe_that_throws_something_unexpected_never_faults_the_fire_and_forget_task()
    {
        // MainViewModel does `_ = _network.CheckAsync()`. A task that faults with nobody
        // observing it is a crash waiting for the finalizer, so the work catches everything.
        var firewall = new FakeFirewall { CheckThrows = new InvalidCastException("a COM object of another shape") };
        var readiness = new NetworkReadiness(firewall);

        await readiness.CheckAsync();

        Assert.Equal(NetworkReadinessState.Failed, readiness.State);
        Assert.Equal(ConsoleFirewallRules.NetshLines(), readiness.Diagnostic);
    }

    [Fact]
    public void The_real_probe_asks_for_the_installer_beside_the_console()
    {
        var probe = new WindowsConsoleFirewallProbe();

        Assert.Equal(OperatingSystem.IsWindows(), probe.IsSupported);
        // The tests run from the build output, where no installer was ever placed.
        Assert.Null(probe.FindHelper());
        Assert.EndsWith(".exe", Defaults.ConsoleSetupExecutableName, StringComparison.Ordinal);
    }
}
