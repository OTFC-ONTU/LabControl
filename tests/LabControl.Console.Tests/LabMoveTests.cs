using System.Text;
using LabControl.Console.Services;
using LabControl.Shared;
using LabControl.Shared.Jobs;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;
using LabControl.Shared.Protocol;
using Xunit;

namespace LabControl.Console.Tests;

/// <summary>
/// Moving a lab whose key was lost to a new key (D-82 items 3–4): the move script is valid
/// Windows PowerShell that runs the installed <c>Setup.exe --rekey</c> — never <c>Uninstall.exe</c>
/// — with the PC's own payload carried inside; the new lab is created beside the old one,
/// remembered, with the layout, scripts and presets carried; each linked PC gets one move with a
/// code of the new lab; a PC that links later is moved by the old profile's session.
/// </summary>
public class LabMoveTests
{
    private const string MoveId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void The_move_script_parses_as_Windows_PowerShell_and_carries_the_payload_inside()
    {
        var setup = Encoding.UTF8.GetBytes("{\"lab_id\":\"new\",\"enrollment_codes\":[\"ABCD-EFGH\"]}");
        var authority = new byte[] { 0x30, 0x82, 0x01, 0x02, 0xFF };

        var text = LabMoveScript.Build(MoveId, setup, authority);

        Assert.Empty(ScriptAnalysis.Analyze(text, ScriptShell.PowerShell).Errors);
        Assert.Contains(Convert.ToBase64String(setup), text, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(authority), text, StringComparison.Ordinal);
        Assert.Contains(@"C:\Program Files\LabControl\Setup.exe", text, StringComparison.Ordinal);
        Assert.Contains("--rekey --payload", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Uninstall", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*S-1-5-18", text, StringComparison.Ordinal);
        Assert.Contains("*S-1-5-32-544", text, StringComparison.Ordinal);
        Assert.Contains(LabMoveScript.ScheduledMarker, text, StringComparison.Ordinal);
        Assert.Contains(LabMoveScript.RefusedMarker, text, StringComparison.Ordinal);
        Assert.Contains(Defaults.InstallationFileName, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Materials", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcde'")]
    public void A_move_id_that_could_break_the_script_is_refused(string moveId) =>
        Assert.Throws<ArgumentException>(() => LabMoveScript.Build(moveId, [1], [1]));

    [Fact]
    public async Task The_new_lab_is_created_beside_the_old_one_remembered_with_its_layout_scripts_and_presets()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var old = console.Session;
        old.Registry.Document.Layout.Add(new LayoutTile { Number = 7, Column = 3, Row = 1 });
        old.SaveLab();
        old.InternetPresets.Save(null, "Python docs", ["docs.python.org"]);
        var bootstrap = console.Bootstrap;

        var target = bootstrap.CreateLabForMove(old, "Viacheslav", "a new passphrase", out var recovery);

        Assert.NotEqual(old.LabId, target.LabId);
        Assert.Equal(old.LabName, target.LabName);
        Assert.NotNull(recovery);
        var store = bootstrap.StoreFor(target.LabId);
        Assert.True(store.HasLab);
        Assert.Contains(store.LoadLab(target.LabId, target.LabName).Layout, t => t.Number == 7 && t.Column == 3 && t.Row == 1);
        Assert.Contains(store.LoadInternetPresets()!.Presets, p => p.Name == "Python docs");
        Assert.Equal(target.LabId, store.LoadInternetPresets()!.LabId);
        Assert.Equal(old.Scripts.Scripts.Count, store.LoadScripts()!.Scripts.Count);

        // Remembered on this computer: the codes for PCs that come back later need nobody to type.
        var instance = store.LoadInstance()!;
        var vault = new LabKeyVault(store, store.LoadLabKey())
        {
            Memory = new LabKeyMemory(store, instance.InstanceId, () => new LabControl.Shared.Protection.FileSecretProtector()),
        };
        Assert.True(vault.TryReopen());

        // The old profile knows where its PCs go.
        Assert.Equal(target.LabId, bootstrap.Profiles.Find(old.LabId)?.MovedToLabId);
        Assert.Equal(target.LabId, bootstrap.MoveTargetOf(old.LabId)?.LabId);
        Assert.Null(bootstrap.MoveTargetOf(target.LabId));
    }

    [Fact]
    public async Task Each_linked_pc_is_sent_one_move_with_its_own_code_of_the_new_lab_and_a_pc_linking_later_is_moved_too()
    {
        await using var console = await TestConsole.CreateLabAsync();
        var codes = console.IssueCodes(2);
        await using var first = TestAgent.Install(console, 4, codes[0]).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.IsLinked(first.AgentId), TimeSpan.FromSeconds(15)));
        var target = console.Bootstrap.CreateLabForMove(console.Session, "Viacheslav", "a new passphrase", out _);

        var jobs = console.Session.MoveToNewKey([first.AgentId, "not-linked"], target);

        var job = Assert.Single(jobs);
        Assert.Equal(Job.Types.Kind.RunScript, job.Kind);
        Assert.Equal(first.AgentId, job.AgentId);
        Assert.Empty(console.Session.MoveToNewKey([first.AgentId], target));

        // The code is one of the new lab's, unused, and travels only inside the script text.
        var newCodes = target.Store.LoadEnrollment(target.LabId);
        var minted = Assert.Single(newCodes.Codes);
        var offer = console.Session.Files.Find(job.Args[RunScriptRequest.ReferenceKey]);
        Assert.NotNull(offer);
        string text;
        using (var reader = new StreamReader(offer!.Open(), Encoding.UTF8))
        {
            text = reader.ReadToEnd();
        }

        var setupLine = text.Split('\n').Single(l => l.Contains("setup.json", StringComparison.Ordinal) && l.Contains("FromBase64String", StringComparison.Ordinal));
        var base64 = setupLine[(setupLine.IndexOf("FromBase64String('", StringComparison.Ordinal) + 18)..setupLine.LastIndexOf("'))", StringComparison.Ordinal)];
        var payload = JsonStore.Parse<SetupPayloadDocument>(Encoding.UTF8.GetString(Convert.FromBase64String(base64)), Defaults.SetupFileName, SetupPayloadDocument.Migrations);
        Assert.Equal(target.LabId, payload.LabId);
        Assert.Equal(minted.Code, Assert.Single(payload.EnrollmentCodes).Replace("-", string.Empty, StringComparison.Ordinal));
        Assert.Equal(RunScriptRequest.SystemValue, job.Args[RunScriptRequest.RunAsKey]);

        // A PC that links while the old lab is being moved gets its move without anyone asking.
        console.Session.MoveTarget = target;
        await using var second = TestAgent.Install(console, 5, codes[1]).Start();
        Assert.True(await Wait.UntilAsync(() => console.Session.MovesSent.Contains(second.AgentId), TimeSpan.FromSeconds(15)));
        Assert.Equal(2, target.Store.LoadEnrollment(target.LabId).Codes.Count);
    }
}
