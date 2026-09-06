using Xunit;

using System.Text;
using LabControl.Shared.Jobs;

namespace LabControl.Shared.Tests;

/// <summary>The bytes each shell gets (D-32 item 5), learned the hard way on the VM.</summary>
public sealed class ScriptTextTests
{
    private static readonly byte[] Bom = Encoding.UTF8.GetPreamble();

    [Fact]
    public void PowerShell_gets_a_bom_exactly_once()
    {
        var plain = Encoding.UTF8.GetBytes("Write-Output 'привіт'\n");
        var withBom = ScriptText.ForPowerShell(plain);

        Assert.True(withBom.AsSpan().StartsWith(Bom));
        Assert.Equal(withBom, ScriptText.ForPowerShell(withBom));
        Assert.Equal(plain, withBom[Bom.Length..]);
    }

    [Fact]
    public void Cmd_gets_no_bom_and_crlf_line_endings()
    {
        var script = ScriptText.ForCmd([.. Bom, .. Encoding.UTF8.GetBytes("@echo off\necho user: %USERNAME%\r\necho привіт\n")]);
        var text = Encoding.UTF8.GetString(script);

        Assert.False(script.AsSpan().StartsWith(Bom));
        Assert.Equal("@echo off\r\necho user: %USERNAME%\r\necho привіт\r\n", text);
        Assert.DoesNotContain("\r\r", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Cmd_keeps_utf8_bytes_intact()
    {
        var cyrillic = Encoding.UTF8.GetBytes("echo привіт, лабораторіє\r\n");
        Assert.Equal(cyrillic, ScriptText.ForCmd(cyrillic));
    }
}
