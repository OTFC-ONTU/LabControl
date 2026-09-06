using System.Text;

namespace LabControl.Shared.Jobs;

/// <summary>
/// The bytes a script is written with on the PC, per shell (D-32 item 5). The console sends
/// UTF-8 with a byte-order mark and whatever line endings its platform uses; each shell has
/// its own ideas about both, and getting them wrong is silent on the Mac and visible only on
/// a real PC — so the rules live here, where the Mac tests can hold them.
/// </summary>
public static class ScriptText
{
    private static readonly byte[] Bom = Encoding.UTF8.GetPreamble();

    /// <summary>
    /// Windows PowerShell 5.1 reads a file with a BOM as UTF-8 and one without as ANSI
    /// (D-29 item 7): make sure the BOM is there. Line endings do not matter to it.
    /// </summary>
    public static byte[] ForPowerShell(byte[] script) =>
        script.AsSpan().StartsWith(Bom) ? script : [.. Bom, .. script];

    /// <summary>
    /// cmd.exe runs the batch after <c>chcp 65001</c>, so the text stays UTF-8 — without a
    /// BOM, which cmd.exe reads as junk in front of the first command, and with CRLF line
    /// endings: with bare LF the batch parser eats the first characters of the following
    /// lines (seen on the VM: <c>'AME' is not recognized</c> for <c>echo user: %USERNAME%</c>).
    /// </summary>
    public static byte[] ForCmd(byte[] script)
    {
        var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetString(script);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(text);
    }
}
