using LabControl.Console.Services;
using LabControl.FakeAgent;
using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Link;
using LabControl.Shared.Packages;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// M7 portion 5, robustness: the console's clock and a PC's clock disagree by minutes to hours,
/// either way. The lab has no time source (D-70 item 6), so this is the ordinary state of a room
/// whose PCs never see the internet. The PC here runs on the real clock; the console's clock is
/// moved with <see cref="TestConsole.CreateLabAsync"/>'s <c>clock</c>.
/// </summary>
public sealed class ClockSkewTests
{
    public static TheoryData<int> Skews => [-180, -10, 10, 60, 180];

    private static ExamRequest Exam(TimeSpan? timer) =>
        new("Test 3", timer, false, [], InternetPolicy.Types.Mode.Open, [], string.Empty, "Practical");

    /// <summary>
    /// Enrolment is the one place the console's clock is written into something the PC checks on
    /// its own: the certificate it issues. Its <c>NotBefore</c> was only five minutes before the
    /// console's "now", so a console ten minutes ahead issued a certificate that the PC called not
    /// yet valid — after the console had already spent the single-use code. The PC was left with a
    /// burned code and no certificate: a USB visit (robustness pass 2026-10-27).
    /// </summary>
    [Theory]
    [MemberData(nameof(Skews))]
    public async Task A_pc_enrols_and_links_whichever_way_the_consoles_clock_is_off(int minutes)
    {
        var skew = TimeSpan.FromMinutes(minutes);
        await using var console = await TestConsole.CreateLabAsync(clock: () => DateTimeOffset.UtcNow + skew);
        var code = console.Session.Enrollment.Generate(1, "test", DateTimeOffset.UtcNow + skew).Single();

        await using var pc = TestAgent.Install(console, 4, code).Start();

        Assert.True(await Wait.UntilAsync(() => pc.Link.State == LinkState.Linked, TimeSpan.FromSeconds(20)),
            "the PC did not link: " + string.Join(" | ", pc.Refusals));
        Assert.True(pc.Link.IsEnrolled);
        Assert.True(console.Session.IsLinked(pc.AgentId));

        // The certificate is good on the PC's clock and on the console's.
        var certificate = pc.Store.Certificate!;
        var trust = new LabTrust(console.Authority, console.Session.LabId);
        Assert.True(trust.TryValidate(certificate, LabRole.Agent, null, out _, out var onPc, DateTimeOffset.UtcNow), onPc.ToString());
        Assert.True(trust.TryValidate(certificate, LabRole.Agent, null, out _, out var onConsole, DateTimeOffset.UtcNow + skew), onConsole.ToString());

        // What the PC learned of the difference is the console's clock against its own.
        Assert.InRange(pc.Link.ClockSkew.TotalMinutes, minutes - 1, minutes + 1);
    }

    /// <summary>
    /// Renewal (D-25) is decided on each side's own clock — the PC asks when its own clock puts the
    /// certificate inside the lead time — and the new certificate must link at once, ahead or behind.
    /// </summary>
    [Theory]
    [InlineData(-180)]
    [InlineData(180)]
    public async Task A_certificate_renewed_by_a_console_whose_clock_is_off_links_at_once(int minutes)
    {
        var skew = TimeSpan.FromMinutes(minutes);
        await using var console = await TestConsole.CreateLabAsync(clock: () => DateTimeOffset.UtcNow + skew);
        await using var pc = TestAgent.InstallEnrolled(console, 9, TimeSpan.FromDays(30));
        var first = LabCertificates.SerialOf(pc.Store.Certificate!);

        pc.Start();

        Assert.True(await Wait.UntilAsync(() => LabCertificates.SerialOf(pc.Store.Certificate!) != first, TimeSpan.FromSeconds(30)));
        var renewed = LabCertificates.SerialOf(pc.Store.Certificate!);
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(pc.AgentId)?.CertificateSerial == renewed, TimeSpan.FromSeconds(20)));
        Assert.False(LabCertificates.NeedsRenewal(pc.Store.Certificate!, DateTimeOffset.UtcNow));
        Assert.Empty(console.Session.MachinesNeedingRenewal());
    }

    /// <summary>
    /// A lock, an internet policy and an exam travel as durations and are stamped on the PC's clock
    /// (D-70 item 6, D-73, D-75): a console hours off must neither end them at once nor stretch them
    /// past their limits. Confirmed end to end on the simulator, both ways.
    /// </summary>
    [Theory]
    [InlineData(-180)]
    [InlineData(180)]
    public async Task Lock_internet_and_exam_limits_count_on_the_pcs_clock(int minutes)
    {
        var skew = TimeSpan.FromMinutes(minutes);
        await using var console = await TestConsole.CreateLabAsync(clock: () => DateTimeOffset.UtcNow + skew);
        await using var machine = await StartMachineAsync(console, 3);
        var id = machine.AgentId;

        Assert.Equal(1, console.Session.Lock([id], "Eyes on me", TimeSpan.FromMinutes(10)));
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(id)?.OverlayLocked == true, TimeSpan.FromSeconds(10)));
        var overlay = console.Session.FindLinked(id)!.OverlayState!;
        AssertOnPcClock(overlay.UntilUnix, TimeSpan.FromMinutes(10));
        Assert.InRange(overlay.HardLimitUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0, (long)Defaults.LockHardLimit.TotalSeconds + 5);
        Assert.InRange(overlay.RemainingSeconds, 590, 600);
        Assert.Equal(1, console.Session.Unlock([id]));
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(id)?.OverlayLocked == false, TimeSpan.FromSeconds(10)));

        console.Session.SetInternet([id], InternetPolicy.Types.Mode.Blocked, [], string.Empty, TimeSpan.FromMinutes(45));
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(id)?.InternetMode == InternetPolicy.Types.Mode.Blocked, TimeSpan.FromSeconds(10)));
        var internet = console.Session.FindLinked(id)!.InternetState!;
        AssertOnPcClock(internet.UntilUnix, TimeSpan.FromMinutes(45));
        Assert.InRange(internet.RemainingSeconds, 45 * 60 - 10, 45 * 60);

        console.Session.StartExam([id], Exam(TimeSpan.FromMinutes(40)), wholeLab: false);
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(id)?.InExam == true, TimeSpan.FromSeconds(10)));
        var exam = console.Session.FindLinked(id)!.ExamState!;
        AssertOnPcClock(exam.UntilUnix, TimeSpan.FromMinutes(40));
        Assert.InRange(exam.RemainingSeconds, 40 * 60 - 10, 40 * 60);
        Assert.InRange(exam.HardLimitUnix - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0, (long)Defaults.ExamHardLimit.TotalSeconds + 5);

        // Extending counts from what the PC has left, never from either clock.
        Assert.Equal(1, console.Session.ExtendExam([id], TimeSpan.FromMinutes(5)));
        Assert.True(await Wait.UntilAsync(() => console.Session.FindLinked(id)!.ExamState!.UntilUnix > exam.UntilUnix + 4 * 60, TimeSpan.FromSeconds(10)));
        AssertOnPcClock(console.Session.FindLinked(id)!.ExamState!.UntilUnix, TimeSpan.FromMinutes(45));
    }

    /// <summary>
    /// <c>install_package</c> carries its limit as a duration, the console's inactivity timeout runs
    /// on the console's clock alone, and the status book stamps the answer on the console's clock —
    /// so a skewed PC neither times out early nor writes a time from the wrong clock.
    /// </summary>
    [Theory]
    [InlineData(-180)]
    [InlineData(180)]
    public async Task A_package_installs_and_its_status_is_stamped_on_the_consoles_clock(int minutes)
    {
        var skew = TimeSpan.FromMinutes(minutes);
        await using var console = await TestConsole.CreateLabAsync(clock: () => DateTimeOffset.UtcNow + skew);
        var entry = await PackageCatalogTests.AddAsync(console.Session.Packages, PackageCatalogTests.NsisInstaller("tool.exe", "skew"), "Tool");
        await using var machine = await StartMachineAsync(console, 6);

        Assert.Single(console.Session.InstallPackage([machine.AgentId], entry, InstallMode.Install));
        Assert.True(await Wait.UntilAsync(() => console.Session.PackageStatus.StateOf(entry, machine.AgentId) == PcPackageState.Installed, TimeSpan.FromSeconds(20)));

        var status = console.Session.PackageStatus.Last(entry.Id, machine.AgentId)!;
        var consoleNow = (DateTimeOffset.UtcNow + skew).ToUnixTimeSeconds();
        Assert.InRange(status.AtUnix, consoleNow - 30, consoleNow + 1);
    }

    private static void AssertOnPcClock(long untilUnix, TimeSpan duration)
    {
        var expected = (DateTimeOffset.UtcNow + duration).ToUnixTimeSeconds();
        Assert.InRange(untilUnix, expected - 15, expected + 1);
    }

    private static async Task<FakeMachine> StartMachineAsync(TestConsole console, int number)
    {
        var payload = SetupPayload.Open(console.Session.WritePayload(TestConsole.TempDirectory(), pcCount: 1));
        var store = FakeMachine.Install(Path.Combine(TestConsole.TempDirectory(), $"PC-{number:00}"), number, payload, System.Net.IPAddress.Loopback.ToString(), console.Port, burnedCode: false);
        var machine = new FakeMachine(store, null, TestLogging.Factory.CreateLogger($"PC-{number:00}"));
        machine.Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(machine.AgentId), TimeSpan.FromSeconds(20)));
        return machine;
    }
}
