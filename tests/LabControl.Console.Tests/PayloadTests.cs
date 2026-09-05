using Xunit;

using LabControl.FakeAgent;
using LabControl.Shared;
using LabControl.Shared.Link;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using LabControl.Shared.Setup;
using Microsoft.Extensions.Logging;

namespace LabControl.Console.Tests;

/// <summary>
/// The stick's trust part end to end: the console writes <c>setup.json</c> + <c>ca.crt</c>,
/// the simulator installs PCs from it the way Setup.exe would, and they enrol.
/// </summary>
public sealed class PayloadTests
{
    [Fact]
    public async Task Fake_machines_install_from_a_written_payload_and_enrol()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var stick = TestConsole.TempDirectory();
        var target = console.Session.WritePayload(stick, pcCount: 2);

        Assert.True(File.Exists(Path.Combine(target, Defaults.SetupFileName)));
        Assert.True(File.Exists(Path.Combine(target, Defaults.CaCertificateFileName)));
        Assert.Equal(2 + Defaults.SpareEnrollmentCodes, console.Session.Enrollment.UnusedCodeCount);

        var payload = SetupPayload.Open(stick);
        Assert.Equal(console.Session.LabId, payload.Document.LabId);

        var data = TestConsole.TempDirectory();
        var machines = new List<FakeMachine>();
        try
        {
            for (var n = 1; n <= 2; n++)
            {
                var store = FakeMachine.Install(Path.Combine(data, $"PC-{n:00}"), n, payload, "127.0.0.1", console.Port, burnedCode: false);
                machines.Add(new FakeMachine(store, null, TestLogging.Factory.CreateLogger($"fake PC-{n:00}")));
            }

            // Codes taken by an install move to the stick's used list, as Setup.exe marks them.
            var rewritten = JsonStore.Load<SetupPayloadDocument>(Path.Combine(target, Defaults.SetupFileName), SetupPayloadDocument.Migrations);
            Assert.Equal(Defaults.SpareEnrollmentCodes, rewritten.EnrollmentCodes.Count);
            Assert.Equal(2, rewritten.UsedEnrollmentCodes.Count);

            // The burned-code failure takes one of those spent codes, in this run or a later one.
            var reopened = SetupPayload.Open(stick);
            using var burned = FakeMachine.Install(Path.Combine(data, "PC-03"), 3, reopened, "127.0.0.1", console.Port, burnedCode: true);
            Assert.Contains(burned.Config.EnrollmentCode, rewritten.UsedEnrollmentCodes);
            Assert.Equal(Defaults.SpareEnrollmentCodes, reopened.Document.EnrollmentCodes.Count);

            foreach (var machine in machines)
            {
                machine.Start();
            }

            Assert.True(await Wait.UntilAsync(() => console.Session.Linked.Count == 2, TimeSpan.FromSeconds(15)));
            Assert.All(machines, m => Assert.Equal(LinkState.Linked, m.Link.State));

            // A simulated reboot: the PC goes dark, the console notices, and it comes back.
            var job = console.Session.CreateJobs([machines[0].AgentId], Job.Types.Kind.Reboot).Single();
            Assert.True(await Wait.UntilAsync(() => job.State == Shared.Lab.JobState.Succeeded));
            Assert.True(await Wait.UntilAsync(() => !console.Session.IsLinked(machines[0].AgentId), TimeSpan.FromSeconds(10)));
            Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(machines[0].AgentId), TimeSpan.FromSeconds(30)));
        }
        finally
        {
            foreach (var machine in machines)
            {
                await machine.DisposeAsync();
            }
        }
    }
}
