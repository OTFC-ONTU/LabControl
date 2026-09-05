using Xunit;

using LabControl.Shared;
using LabControl.Shared.Files;
using LabControl.Shared.Jobs;
using LabControl.Shared.Protocol;

namespace LabControl.Shared.Tests;

/// <summary>The <c>run_script</c> arguments (D-31), the built-in scripts and the file offers behind them.</summary>
public sealed class RunScriptTests
{
    private static readonly string Hash = new('a', 64);

    [Fact]
    public void Arguments_round_trip_through_the_wire_form()
    {
        var request = new RunScriptRequest(Hash, Hash, ScriptShell.Cmd, ScriptRunAs.User, TimeSpan.FromSeconds(45), "test-whoami");
        var job = new Job { Id = "j1", Kind = Job.Types.Kind.RunScript };
        job.Args.Add(request.ToArgs());

        Assert.True(RunScriptRequest.TryParse(job, out var parsed, out var error), error);
        Assert.Equal(request, parsed);
        Assert.Equal(".cmd", parsed.Extension);
    }

    [Fact]
    public void Defaults_apply_when_the_console_left_a_parameter_out()
    {
        var job = new Job { Id = "j2", Kind = Job.Types.Kind.RunScript };
        job.Args[RunScriptRequest.ReferenceKey] = Hash;
        job.Args[RunScriptRequest.Sha256Key] = Hash.ToUpperInvariant();

        Assert.True(RunScriptRequest.TryParse(job, out var parsed, out _));
        Assert.Equal(ScriptShell.PowerShell, parsed.Shell);
        Assert.Equal(ScriptRunAs.System, parsed.RunAs);
        Assert.Equal(Defaults.ScriptDefaultTimeout, parsed.Timeout);
        Assert.Equal("script", parsed.Name);
        Assert.Equal(Hash, parsed.Sha256);
    }

    [Theory]
    [InlineData(RunScriptRequest.ShellKey, "bash", "shell")]
    [InlineData(RunScriptRequest.RunAsKey, "root", "run")]
    [InlineData(RunScriptRequest.TimeoutKey, "soon", "timeout")]
    [InlineData(RunScriptRequest.TimeoutKey, "0", "timeout")]
    [InlineData(RunScriptRequest.Sha256Key, "abc", "sha256")]
    public void A_job_this_agent_cannot_run_is_refused_with_the_reason(string key, string value, string expectedWord)
    {
        var job = new Job { Id = "j3", Kind = Job.Types.Kind.RunScript };
        job.Args[RunScriptRequest.ReferenceKey] = Hash;
        job.Args[RunScriptRequest.Sha256Key] = Hash;
        job.Args[key] = value;

        Assert.False(RunScriptRequest.TryParse(job, out _, out var error));
        Assert.Contains(expectedWord, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_name_from_the_console_can_never_walk_the_directory_tree()
    {
        Assert.Equal("evil.ps1", RunScriptRequest.SafeName(@"..\..\evil.ps1"));
        Assert.Equal("cwindowsx", RunScriptRequest.SafeName("c:/windows/x"));
        Assert.Equal(string.Empty, RunScriptRequest.SafeName("../"));
    }

    [Fact]
    public void The_hundred_line_script_says_what_the_acceptance_criterion_needs()
    {
        foreach (var shell in new[] { ScriptShell.PowerShell, ScriptShell.Cmd })
        {
            var text = TestScripts.Text(TestScriptKind.HundredLines, shell);
            Assert.Contains("100", text);
            Assert.Contains("exit", text);
            Assert.Contains("3", text.Split('\n').Last(l => l.Contains("exit", StringComparison.Ordinal)));
        }

        Assert.Contains("Start-Sleep", TestScripts.Text(TestScriptKind.Hang, ScriptShell.PowerShell));
        Assert.Contains("timeout", TestScripts.Text(TestScriptKind.Hang, ScriptShell.Cmd));
        Assert.Contains("USERNAME", TestScripts.Text(TestScriptKind.WhoAmI, ScriptShell.PowerShell));
    }

    [Fact]
    public void Offering_the_same_text_twice_yields_the_same_reference_and_a_bom()
    {
        var offers = new FileOffers();
        var first = offers.OfferText("Write-Output 'привіт'", "a");
        var second = offers.OfferText("Write-Output 'привіт'", "b");

        Assert.Equal(first.Reference, second.Reference);
        Assert.Equal(first.Sha256, first.Reference);
        Assert.True(FileHash.LooksLikeSha256(first.Reference));
        Assert.Equal(1, offers.Count);

        using var stream = first.Open();
        var bytes = new byte[3];
        Assert.Equal(3, stream.Read(bytes, 0, 3));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes);
        Assert.Equal(first.Size, stream.Length);
    }

    [Fact]
    public void An_offer_from_disk_hashes_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "labcontrol-offer-" + Guid.NewGuid().ToString("n") + ".bin");
        var content = new byte[Defaults.FileChunkBytes * 2 + 17];
        Random.Shared.NextBytes(content);
        File.WriteAllBytes(path, content);
        try
        {
            var offer = new FileOffers().OfferFile(path);
            Assert.Equal(FileHash.Sha256Hex(content), offer.Reference);
            Assert.Equal(content.Length, offer.Size);
            Assert.Null(new FileOffers().Find(offer.Reference));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
