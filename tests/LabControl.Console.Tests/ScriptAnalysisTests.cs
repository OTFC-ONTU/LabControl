using LabControl.Console.Services;
using LabControl.Shared.Jobs;
using Xunit;

namespace LabControl.Console.Tests;

public sealed class ScriptAnalysisTests
{
    [Fact]
    public void Parsing_reports_locations_without_executing_text()
    {
        var result = ScriptAnalysis.Analyze("Write-Output 'hello'\nif ($true) {", ScriptShell.PowerShell);
        Assert.Contains(result.Errors, e => e.Line == 2);
        Assert.Contains(result.Tokens, t => t.Kind == "StringLiteral");
        Assert.Empty(ScriptAnalysis.Analyze("throw 'must never execute'", ScriptShell.PowerShell).Errors);
    }

    [Theory]
    [InlineData("$x ?? 'fallback'")]
    [InlineData("hostname && whoami")]
    [InlineData("$true ? 1 : 2")]
    public void PowerShell_7_operators_do_not_pass_for_Windows_PowerShell(string script) =>
        Assert.NotEmpty(ScriptAnalysis.Analyze(script, ScriptShell.PowerShell).Errors);

    [Fact]
    public void All_embedded_PowerShell_templates_parse()
    {
        var scripts = SeedScripts.Embedded();
        Assert.Equal(15, scripts.Count(s => s.FileName.StartsWith("open-") || s.FileName.StartsWith("close-") && s.FileName != "close-browsers.ps1"));
        foreach (var script in scripts.Where(s => s.FileName.EndsWith(".ps1")))
            Assert.True(ScriptAnalysis.Analyze(script.Text, ScriptShell.PowerShell).Errors.Count == 0, script.FileName);
    }

    [Fact]
    public void Cmd_uses_its_own_basic_checks_not_the_PowerShell_parser()
    {
        var result = ScriptAnalysis.Analyze("@echo off\nset X=hello\necho %X%\n", ScriptShell.Cmd);
        Assert.Empty(result.Errors);
        Assert.NotEmpty(result.Tokens);
    }
}
