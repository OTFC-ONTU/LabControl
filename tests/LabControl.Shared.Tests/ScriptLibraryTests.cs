using Xunit;

using LabControl.Shared;
using LabControl.Shared.Identity;
using LabControl.Shared.Jobs;
using LabControl.Shared.Lab;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Tests;

/// <summary>The script library's document and seed (D-31 item 4, D-38): what a seed file becomes, what the file on disk carries, and that a backup carries it.</summary>
public sealed class ScriptLibraryTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 7, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_powershell_seed_takes_its_description_and_directives_from_the_header()
    {
        var seed = new SeedScript("close-browsers.ps1", "﻿# Closes every browser window.\n# run-as: user\n# timeout: 30\n\n$x = 1\n# not a header line any more\nexit 0\n");
        var record = ScriptSeed.ToRecord(seed, Now);

        Assert.Equal("close-browsers", record.Name);
        Assert.Equal("Closes every browser window.", record.Description);
        Assert.Equal(RunScriptRequest.PowerShellValue, record.Shell);
        Assert.Equal(RunScriptRequest.UserValue, record.RunAs);
        Assert.Equal(30, record.TimeoutSeconds);
        Assert.StartsWith("# Closes", record.Text, StringComparison.Ordinal);
        Assert.Equal("close-browsers.ps1", record.SeedFile);
        Assert.Equal(Now.ToUnixTimeSeconds(), record.CreatedAtUnix);
        Assert.NotEmpty(record.Id);
    }

    [Fact]
    public void A_cmd_seed_reads_rem_lines_and_defaults_the_rest()
    {
        var seed = new SeedScript("clear-temp.cmd", "@echo off\r\nrem Empties the temporary directories.\r\n:: timeout: 300\r\necho hi\r\nrem run-as: user (too late, this is code now)\r\n");
        var record = ScriptSeed.ToRecord(seed, Now);

        Assert.Equal("clear-temp", record.Name);
        Assert.Equal(RunScriptRequest.CmdValue, record.Shell);
        Assert.Equal(ScriptShell.Cmd, record.ShellKind);
        Assert.Equal("Empties the temporary directories.", record.Description);
        Assert.Equal(300, record.TimeoutSeconds);
        Assert.Equal(RunScriptRequest.SystemValue, record.RunAs);
    }

    [Fact]
    public void A_seed_without_a_header_still_becomes_a_script()
    {
        var record = ScriptSeed.ToRecord(new SeedScript("plain.ps1", "Write-Output 1\n"), Now);
        Assert.Equal("plain", record.Name);
        Assert.Equal(string.Empty, record.Description);
        Assert.Equal((int)Defaults.ScriptDefaultTimeout.TotalSeconds, record.TimeoutSeconds);
    }

    [Fact]
    public void The_library_round_trips_through_json_with_the_jobs_vocabulary()
    {
        var document = new ScriptsDocument
        {
            LabId = "lab",
            SeedImportedAtUnix = Now.ToUnixTimeSeconds(),
            Scripts = [new ScriptRecord { Id = "id-1", Name = "n", Description = "d", Shell = "cmd", RunAs = "user", TimeoutSeconds = 7, Text = "echo привіт\r\n" }],
        };

        var json = JsonStore.Serialize(document, ScriptsDocument.Migrations);
        Assert.StartsWith("{\n  \"schema_version\": " + Defaults.ScriptsSchemaVersion, json, StringComparison.Ordinal);
        Assert.Contains("\"shell\": \"cmd\"", json, StringComparison.Ordinal);
        Assert.Contains("\"run_as\": \"user\"", json, StringComparison.Ordinal);

        var back = JsonStore.Parse<ScriptsDocument>(json, Defaults.ScriptsFileName, ScriptsDocument.Migrations);
        var script = Assert.Single(back.Scripts);
        Assert.Equal(ScriptShell.Cmd, script.ShellKind);
        Assert.Equal(ScriptRunAs.User, script.RunAsKind);
        Assert.Equal(TimeSpan.FromSeconds(7), script.Timeout);
        Assert.Equal("echo привіт\r\n", script.Text);
    }

    [Fact]
    public void A_backup_carries_the_library_and_an_old_backup_has_none()
    {
        using var lab = TestLab.Create(out _);
        var scripts = new ScriptsDocument { LabId = lab.LabId, Scripts = [new ScriptRecord { Id = "s", Name = "pc-info", Text = "hostname" }] };

        var withScripts = LabBackup.Export(lab, new LabDocument { LabId = lab.LabId }, new Dictionary<string, string>(), "x", Now, scripts: scripts);
        Assert.DoesNotContain("pc-info", LabBackup.Serialize(withScripts), StringComparison.Ordinal);
        Assert.Equal("pc-info", Assert.Single(LabBackup.Open(withScripts, lab).Scripts!.Scripts).Name);

        var without = LabBackup.Export(lab, new LabDocument { LabId = lab.LabId }, new Dictionary<string, string>(), "x", Now);
        Assert.Null(LabBackup.Open(without, lab).Scripts);
    }
}
