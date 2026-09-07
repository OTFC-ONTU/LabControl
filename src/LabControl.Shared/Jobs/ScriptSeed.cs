using System.Globalization;
using LabControl.Shared.Persistence;

namespace LabControl.Shared.Jobs;

/// <summary>A file from the repository's <c>scripts/library/</c>, embedded in the console.</summary>
public sealed record SeedScript(string FileName, string Text);

/// <summary>
/// Turns a seed file into a library record (D-38). The shell comes from the extension
/// (<c>.cmd</c>/<c>.bat</c> → cmd, anything else → PowerShell), the name from the file
/// name, the description from the first comment line, and two optional header comments
/// set the rest: <c>run-as: user</c> and <c>timeout: 300</c>. The text is kept whole,
/// header included, so what the teacher sees is what the file said.
/// </summary>
public static class ScriptSeed
{
    public const string RunAsDirective = "run-as:";
    public const string TimeoutDirective = "timeout:";

    public static ScriptRecord ToRecord(SeedScript seed, DateTimeOffset now)
    {
        var extension = Path.GetExtension(seed.FileName);
        var shell = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase)
            ? ScriptShell.Cmd
            : ScriptShell.PowerShell;

        var record = new ScriptRecord
        {
            Id = Guid.NewGuid().ToString("d"),
            Name = Path.GetFileNameWithoutExtension(seed.FileName),
            Shell = ScriptRecord.ShellValue(shell),
            Text = seed.Text.TrimStart('﻿'),
            CreatedAtUnix = now.ToUnixTimeSeconds(),
            UpdatedAtUnix = now.ToUnixTimeSeconds(),
            SeedFile = seed.FileName,
        };

        foreach (var raw in record.Text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (!TryStripComment(line, shell, out var comment))
            {
                break;
            }

            if (comment.Length == 0)
            {
                continue;
            }

            if (comment.StartsWith(RunAsDirective, StringComparison.OrdinalIgnoreCase))
            {
                record.RunAs = ScriptRecord.RunAsValue(ScriptRecord.ParseRunAs(comment[RunAsDirective.Length..].Trim()));
            }
            else if (comment.StartsWith(TimeoutDirective, StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(comment[TimeoutDirective.Length..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
                {
                    record.TimeoutSeconds = seconds;
                }
            }
            else if (record.Description.Length == 0)
            {
                record.Description = comment;
            }
        }

        return record;
    }

    /// <summary>The comment's text, or false when the line is code — the header ends there.</summary>
    private static bool TryStripComment(string line, ScriptShell shell, out string comment)
    {
        comment = string.Empty;
        if (shell == ScriptShell.Cmd)
        {
            if (line.StartsWith("@echo off", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var text = line.TrimStart('@');
            if (text.StartsWith("rem ", StringComparison.OrdinalIgnoreCase) || text.Equals("rem", StringComparison.OrdinalIgnoreCase))
            {
                comment = text.Length > 3 ? text[4..].Trim() : string.Empty;
                return true;
            }

            if (text.StartsWith("::", StringComparison.Ordinal))
            {
                comment = text[2..].Trim();
                return true;
            }

            return false;
        }

        if (line.StartsWith('#'))
        {
            comment = line.TrimStart('#').Trim();
            return true;
        }

        return false;
    }
}
